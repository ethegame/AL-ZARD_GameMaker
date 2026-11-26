using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameMaker.Services;

// Claude 요금제 사용량(5시간·주간 %) 받기. 출처 = Claude Code 의 확장(mod) 이벤트 session.measure 의 rateLimits [{kind, percentUsed, resetsAt}] — 답이 끝날 때마다 옴.
// 1. 시작할 때 <%LOCALAPPDATA%\AL-ZARD_GAMEMAKER>\plan-usage-mod\ 에 확장 파일을 쓰고, 이 앱이 띄우는 Claude 에만 --plugin-dir 로 붙인다 (ClaudeTerminal.PlanUsageModDir)
//    → 사용자가 따로 여는 Claude 나 다른 프로그램의 Claude 에는 아무 영향 없음. 확장 이름·파일·토큰 머리글이 모두 이 앱 고유라 다른 앱의 같은 종류 확장과 겹치지 않는다
// 2. 127.0.0.1 무작위 포트 HttpListener + 켤 때마다 새 토큰 → plan-usage-listen.txt "포트\r\n토큰". 확장은 보낼 때마다 이 파일을 다시 읽는다 (파일이 없으면 아무것도 안 함)
// 3. 확장은 훅 안이 아니라 타이머에서 POST — 훅 안에서 기다리면 Claude 의 답이 같이 묶임
// 값은 계정 단위라 세션마다 같은 값이 온다 → 종류별 최신 1개. 창 구분 = resetsAt (늦게 온 이전 창 값은 버림). 앱을 껐다 켜도 다시 차는 시각 전까진 마지막 값을 보여 준다
public sealed class PlanUsageReceiver : IDisposable
{
    public const string FiveHourKind = "five_hour";
    public const string SevenDayKind = "seven_day";

    private const string ModDirName = "plan-usage-mod";
    private const string ModName = "gamemaker-plan-usage";
    private const string ListenFileName = "plan-usage-listen.txt";
    private const string StateFileName = "plan-usage.json";
    private const string UsagePath = "/usage";
    private const string TokenHeader = "X-GAMEMAKER-Token";
    private const int MaxBodyBytes = 64 * 1024;
    private const int PortMin = 40000;
    private const int PortMax = 60000;
    private const int PortTries = 10;

    private sealed class Limit
    {
        public double Percent { get; set; }
        public DateTime? ResetsAtUtc { get; set; }
        public DateTime ReceivedUtc { get; set; }
    }

    private sealed class StateData
    {
        public Dictionary<string, Limit> Limits { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly string _dir;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private StateData _state = new();
    private HttpListener? _listener;
    private string _token = "";

    public PlanUsageReceiver(string dir, Action<string> log)
    {
        _dir = dir;
        _log = log;
    }

    // 확장 폴더 (만들지 못했으면 null → Claude 에 안 붙임)
    public string? ModDir { get; private set; }

    // 새 값이 들어왔을 때 (받는 스레드에서 불림 — 화면 일은 부른 쪽이 UI 로 넘길 것)
    public event Action? Changed;

    public void Start()
    {
        _state = LoadState();
        try
        {
            ModDir = WriteMod();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Claude 사용량 표시 꺼짐 — 확장 파일을 쓰지 못함: {ex.Message}");
            return;
        }

        if (false == StartListener())
        {
            _log("Claude 사용량 수신 시작 실패 — 빈 포트를 못 찾음");
        }
    }

    public void Dispose()
    {
        HttpListener? listener = _listener;
        _listener = null;
        try
        {
            listener?.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // 이미 닫힘
        }

        // 내가 쓴 파일일 때만 지움 (이 앱을 둘 켠 경우 다른 쪽 것을 지우지 않게)
        string listenPath = Path.Combine(_dir, ListenFileName);
        try
        {
            if (_token.Length > 0 && File.Exists(listenPath) && File.ReadAllText(listenPath).Contains(_token, StringComparison.Ordinal))
            {
                File.Delete(listenPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 못 지워도 다음에 덮어씀
        }
    }

    // 5 Hours · Weekly (값이 있고 다시 차는 시각 전인 것만)
    public List<UsageWindow> Windows()
    {
        List<UsageWindow> windows = new();
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            AddLive(windows, FiveHourKind, "5 Hours", TimeSpan.FromHours(5).TotalMinutes, now);
            AddLive(windows, SevenDayKind, "Weekly", TimeSpan.FromDays(7).TotalMinutes, now);
        }

        return windows;
    }

    // 마지막으로 값을 받은 시각 (없으면 null) — 툴팁용
    public DateTime? LastReceivedUtc()
    {
        lock (_lock)
        {
            return _state.Limits.Count > 0 ? _state.Limits.Values.Max(limit => limit.ReceivedUtc) : null;
        }
    }

    private void AddLive(List<UsageWindow> windows, string kind, string label, double minutes, DateTime now)
    {
        if (_state.Limits.TryGetValue(kind, out Limit? limit) && (null == limit.ResetsAtUtc || limit.ResetsAtUtc > now))
        {
            windows.Add(new UsageWindow(label, limit.Percent, limit.ResetsAtUtc, minutes));
        }
    }

    private StateData LoadState()
    {
        try
        {
            string path = Path.Combine(_dir, StateFileName);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<StateData>(File.ReadAllText(path)) ?? new StateData();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 깨진 파일 = 처음부터
        }

        return new StateData();
    }

    private void SaveState()
    {
        try
        {
            string path = Path.Combine(_dir, StateFileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 못 해도 이번 실행 동안은 보임
        }
    }

    private string WriteMod()
    {
        string dir = Path.Combine(_dir, ModDirName);
        string listenPath = Path.Combine(_dir, ListenFileName).Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(dir, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        UTF8Encoding utf8 = new(false);
        File.WriteAllText(Path.Combine(dir, ".claude-plugin", "plugin.json"),
            "{ \"name\": \"" + ModName + "\", \"version\": \"0.0.0\", \"description\": \"AL-ZARD_GameMaker: forwards session.measure rate limits to the app on loopback. Generated at every app start.\" }\n", utf8);
        File.WriteAllText(Path.Combine(dir, "hooks", "hooks.json"),
            "{ \"description\": \"AL-ZARD_GameMaker plan usage reporter\", \"modules\": [\"./register.ts\"] }\n", utf8);
        string register =
            "// GENERATED by AL-ZARD_GameMaker at every start (Services/PlanUsageReceiver.cs) - edits are overwritten.\n" +
            "// Observes session.measure only; posts from a timer so a slow app never holds the turn.\n" +
            "import type { Register } from 'claude-code'\n\n" +
            $"const LISTEN = {JsonSerializer.Serialize(listenPath)}\n" +
            "const PORT = /^[1-9][0-9]{0,4}$/\n" +
            "const TOKEN = /^[A-Za-z0-9_-]{43}$/\n\n" +
            "export const register: Register = on => {\n" +
            "  on('session.measure', ($, e, next) => {\n" +
            "    $.clock.after(0, async () => {\n" +
            "      try {\n" +
            "        const [port = '', token = ''] = (await $.fs.read(LISTEN)).split('\\r\\n')\n" +
            "        if (!PORT.test(port) || Number(port) > 65535 || !TOKEN.test(token)) return\n" +
            $"        await $.http.fetch(`http://127.0.0.1:${{port}}{UsagePath}`, {{\n" +
            "          method: 'POST',\n" +
            $"          headers: {{ 'Content-Type': 'application/json', '{TokenHeader}': token }},\n" +
            "          body: JSON.stringify({ sessionId: await $.session.id(), rateLimits: e.rateLimits }),\n" +
            "        })\n" +
            "      } catch {}\n" +
            "    })\n" +
            "    return next(e)\n" +
            "  })\n" +
            "}\n";
        File.WriteAllText(Path.Combine(dir, "hooks", "register.ts"), register, utf8);
        return dir;
    }

    private bool StartListener()
    {
        // 43자 토큰 (확장의 TOKEN 정규식과 같은 모양)
        _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        for (int i = 0; i < PortTries; i++)
        {
            int port = Random.Shared.Next(PortMin, PortMax);
            HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            try
            {
                string listenPath = Path.Combine(_dir, ListenFileName);
                string temp = listenPath + ".tmp";
                File.WriteAllText(temp, $"{port}\r\n{_token}", new UTF8Encoding(false));
                File.Move(temp, listenPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listener.Close();
                _log($"Claude 사용량 수신 파일 작성 실패: {ex.Message}");
                return false;
            }

            _listener = listener;
            _ = Task.Run(() => ReceiveLoopAsync(listener));
            return true;
        }

        return false;
    }

    private async Task ReceiveLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // 닫힘
            }

            string? body = ReadRequest(context);
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // 상대가 먼저 끊음
            }

            if (null != body && Apply(ParseLimits(body)))
            {
                Changed?.Invoke();
            }
        }
    }

    // 토큰·경로·방식이 맞는 POST 만 본문을 돌려줌 (아니면 null + 401)
    private string? ReadRequest(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            context.Response.StatusCode = 401;
            if ("POST" != request.HttpMethod || UsagePath != request.Url?.AbsolutePath || _token != request.Headers[TokenHeader])
            {
                return null;
            }

            context.Response.StatusCode = 200;
            MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int read = request.InputStream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static List<(string Kind, Limit Limit)> ParseLimits(string body)
    {
        List<(string Kind, Limit Limit)> result = new();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (false == doc.RootElement.TryGetProperty("rateLimits", out JsonElement limits) || JsonValueKind.Array != limits.ValueKind)
            {
                return result;
            }

            DateTime now = DateTime.UtcNow;
            foreach (JsonElement item in limits.EnumerateArray())
            {
                if (JsonValueKind.Object != item.ValueKind
                    || false == item.TryGetProperty("kind", out JsonElement kindValue) || JsonValueKind.String != kindValue.ValueKind
                    || false == item.TryGetProperty("percentUsed", out JsonElement percent) || JsonValueKind.Number != percent.ValueKind)
                {
                    continue;
                }

                string kind = kindValue.GetString() ?? "";
                double value = percent.GetDouble();
                // 미터에 쓰는 두 종류만 받음 (다른 종류는 이 앱이 안 보여 줌)
                if ((FiveHourKind != kind && SevenDayKind != kind) || double.IsNaN(value) || value < 0)
                {
                    continue;
                }

                Limit limit = new() { Percent = value, ReceivedUtc = now };
                if (item.TryGetProperty("resetsAt", out JsonElement resetsAt) && JsonValueKind.String == resetsAt.ValueKind
                    && DateTime.TryParse(resetsAt.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed))
                {
                    limit.ResetsAtUtc = parsed;
                }

                result.Add((kind, limit));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // 이상한 본문은 무시
        }

        return result;
    }

    private bool Apply(List<(string Kind, Limit Limit)> readings)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach ((string kind, Limit limit) in readings)
            {
                // 늦게 도착한 이전 창 값은 버림 (같은 창이면 줄어든 값이어도 최신 우선)
                if (_state.Limits.TryGetValue(kind, out Limit? held) && null != held.ResetsAtUtc && limit.ResetsAtUtc < held.ResetsAtUtc)
                {
                    continue;
                }

                _state.Limits[kind] = limit;
                changed = true;
            }

            if (changed)
            {
                SaveState();
            }
        }

        return changed;
    }
}
