using System.IO;
using System.Text;
using System.Text.Json;
using GameMaker.Core;
using GameMaker.Services;

namespace GameMaker.Views;

// 오른쪽 AI 칸 엔진 고르기 아래 사용량 미터 (화면 = wwwroot/usage.js). 값 받기만 하고 그림은 화면이 그린다. 화면은 지금 실제로 도는 엔진 것을 보여 줌.
//   Claude = Services/PlanUsageReceiver.cs (Claude Code 확장이 보내 주는 5 Hours · Weekly)
//   GPT    = Services/CodexRateLimits.cs (Codex 대화 기록 끝에서 읽음 — GPT 가 도는 동안 30초마다, 그 밖엔 켤 때 한 번)
//   GEMINI = Services/AgyQuota.cs (`agy -p /quota` — 부를 때마다 agy 로그 파일이 하나 생겨서 드물게: 켤 때(캐시가 오래됐으면)·GEMINI 로 바꿀 때,
//            GEMINI 가 도는 동안 15분마다, 아닐 때 1시간마다. 마지막 성공 답 = 설정 폴더 gemini-usage.json — 다시 켜도 바로 보이고 자동 고르기에 씀)
// 셋 다 계정 단위 값이라 이 앱 밖에서 쓴 것도 반영된다. 자동 고르기(TerminalSession)·GPT 그림 한도 검사(MainWindow.Draw.cs)는 RemainingOf 로만 묻는다.
// 11/26 MainWindow partial(Usage.cs) → 이 클래스: 스냅샷을 남이 직접 읽던 것(엔진 고르기·그림)을 묻기로 바꾸려고
public sealed class UsageMonitor : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CodexReadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AgyShownInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AgyHiddenInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan AgyForceMinInterval = TimeSpan.FromMinutes(1);
    private const string AgyCacheFileName = "gemini-usage.json";

    private readonly HostWindow _host;
    private readonly AppConfig _config;
    private readonly ClaudeTerminal _terminal;
    private readonly Action<object> _post;
    private readonly PlanUsageReceiver _planUsage;
    private readonly CodexRateLimits _codexLimits = new();
    private readonly DispatcherTimer _timer = new() { Interval = TickInterval };
    private UsageSnapshot? _codexSnapshot;
    private bool _codexReading;
    private DateTime _codexReadUtc = DateTime.MinValue;
    private UsageSnapshot? _geminiSnapshot;
    private bool _agyReading;
    private bool _agyStopped;                 // /quota 가 모델을 돌린 흔적 → 이번 실행 동안 다시 안 부름
    private DateTime _agyReadUtc = DateTime.MinValue;

    public UsageMonitor(HostWindow host, AppConfig config, ClaudeTerminal terminal, Action<object> post)
    {
        _host = host;
        _config = config;
        _terminal = terminal;
        _post = post;
        _planUsage = new PlanUsageReceiver(AppConfig.LocalAppDir, _host.Log);
        _planUsage.Changed += () => _host.Dispatcher.BeginInvoke(Post);
        _timer.Tick += (_, _) => OnTick();
    }

    public void Start()
    {
        _planUsage.Start();
        _terminal.PlanUsageModDir = _planUsage.ModDir;
        LoadAgyCache();
        _timer.Start();
        ReadCodexUsage(force: true);
        ReadAgyUsage(force: false);
    }

    public void Dispose()
    {
        _timer.Stop();
        _planUsage.Dispose();
    }

    // 창 최소화 중엔 미터가 안 보이므로 쉼. 돌아오면 한 번 바로 읽어 맞춤 (agy 는 자기 15분·1시간 문을 그대로 지킴)
    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
        OnTick();
    }

    private void OnTick()
    {
        if (AiEngines.Gpt == _terminal.Ai)
        {
            ReadCodexUsage(force: false);
        }

        ReadAgyUsage(force: false);
        Post();
    }

    // 엔진을 바꿨을 때·GPT 그림을 그린 뒤: 그 엔진 값을 바로 다시 읽음
    public void Refresh(string engine)
    {
        if (AiEngines.Gpt == engine)
        {
            ReadCodexUsage(force: true);
        }

        if (AiEngines.Gemini == engine)
        {
            ReadAgyUsage(force: true);
        }
    }

    // 그 엔진 한도 창들 중 가장 적게 남은 % (모름·다시 차는 시각 지남 = null)
    public double? RemainingOf(string engine)
    {
        IEnumerable<UsageWindow> windows;
        if (AiEngines.Gpt == engine)
        {
            windows = _codexSnapshot?.Windows ?? Array.Empty<UsageWindow>();
        }
        else if (AiEngines.Gemini == engine)
        {
            windows = _geminiSnapshot?.Windows ?? Array.Empty<UsageWindow>();
        }
        else
        {
            windows = _planUsage.Windows();
        }

        return MinRemaining(windows);
    }

    private static double? MinRemaining(IEnumerable<UsageWindow> windows)
    {
        double? min = null;
        DateTime now = DateTime.UtcNow;
        foreach (UsageWindow window in windows)
        {
            if (false == window.IsLive(now))
            {
                continue;
            }

            double left = 100 - window.UsedPercent;
            if (null == min || left < min.Value)
            {
                min = left;
            }
        }

        return min;
    }

    // 백그라운드에서 Codex 기록 읽기 (겹쳐 읽지 않음)
    private void ReadCodexUsage(bool force)
    {
        if (_codexReading || (false == force && DateTime.UtcNow - _codexReadUtc < CodexReadInterval))
        {
            return;
        }

        _codexReading = true;
        _codexReadUtc = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            UsageSnapshot snapshot;
            try
            {
                snapshot = _codexLimits.Read();
            }
            catch (Exception ex)
            {
                snapshot = UsageSnapshot.Fail(ex.Message);
            }

            _host.Dispatcher.BeginInvoke(() =>
            {
                _codexSnapshot = snapshot;
                _codexReading = false;
                Post();
            });
        });
    }

    // agy /quota 부르기 (겹쳐 부르지 않음). force = 바꿀 때 — 그래도 마지막 읽은 지 1분 안이면 다시 안 부름 (탭을 왔다 갔다 해도 몰아 부르지 않게)
    private void ReadAgyUsage(bool force)
    {
        if (_agyReading || _agyStopped)
        {
            return;
        }

        TimeSpan since = DateTime.UtcNow - _agyReadUtc;
        TimeSpan interval = AgyHiddenInterval;
        if (AiEngines.Gemini == _terminal.Ai)
        {
            interval = AgyShownInterval;
        }

        if (force)
        {
            interval = AgyForceMinInterval;
        }

        if (since < interval)
        {
            return;
        }

        _agyReadUtc = DateTime.UtcNow;
        string exe = AgyLauncher.Find(AppConfig.ExpandPath(_config.AgyExe));
        if (0 == exe.Length)
        {
            if (null == _geminiSnapshot || null != _geminiSnapshot.Error)
            {
                _geminiSnapshot = UsageSnapshot.Fail("Antigravity(agy) 가 아직 없어요 — GEMINI 칸을 고르면 설치 안내가 나와요");
            }

            return;
        }

        _agyReading = true;
        _ = Task.Run(async () =>
        {
            (string? json, string? error) = await AgyQuota.RunAsync(exe);
            _host.Dispatcher.BeginInvoke(() =>
            {
                _agyReading = false;
                ApplyAgyAnswer(json, error);
                Post();
            });
        });
    }

    private void ApplyAgyAnswer(string? json, string? error)
    {
        if (null == json)
        {
            if (null == _geminiSnapshot || null != _geminiSnapshot.Error)
            {
                _geminiSnapshot = UsageSnapshot.Fail(error ?? "알 수 없음");
            }

            return;
        }

        string? spend = AgyQuota.ModelSpendSign(json);
        if (null != spend)
        {
            _agyStopped = true;
            _host.Log($"GEMINI 사용량: agy /quota 가 모델을 돌림 ({spend}) — 이번 실행 동안 읽기를 멈춤");
            return;
        }

        DateTime now = DateTime.UtcNow;
        UsageSnapshot parsed = AgyQuota.Parse(json, now);
        if (null == parsed.Error)
        {
            _geminiSnapshot = parsed;
            SaveAgyCache(json, now);
            return;
        }

        if (null == _geminiSnapshot || null != _geminiSnapshot.Error)
        {
            _geminiSnapshot = parsed;
        }
    }

    private sealed class AgyCache
    {
        public DateTime ReceivedUtc { get; set; }
        public string Json { get; set; } = "";
    }

    // 다시 켜도 바로 보이게 + 캐시가 아직 새것이면 켤 때 agy 를 부르지 않음
    private void LoadAgyCache()
    {
        try
        {
            string path = Path.Combine(AppConfig.LocalAppDir, AgyCacheFileName);
            if (false == File.Exists(path))
            {
                return;
            }

            AgyCache? cache = JsonSerializer.Deserialize<AgyCache>(File.ReadAllText(path, Encoding.UTF8));
            if (null == cache)
            {
                return;
            }

            DateTime received = DateTime.SpecifyKind(cache.ReceivedUtc, DateTimeKind.Utc);
            UsageSnapshot parsed = AgyQuota.Parse(cache.Json, received);
            if (null == parsed.Error)
            {
                _geminiSnapshot = parsed;
                _agyReadUtc = received;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 깨졌으면 다음 읽기 때 다시 채워짐
        }
    }

    private static void SaveAgyCache(string json, DateTime receivedUtc)
    {
        try
        {
            string path = Path.Combine(AppConfig.LocalAppDir, AgyCacheFileName);
            Directory.CreateDirectory(AppConfig.LocalAppDir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new AgyCache { ReceivedUtc = receivedUtc, Json = json }), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 못 쓰면 다음에 켤 때 다시 읽음
        }
    }

    // 세 엔진 값을 같이 보냄 — 엔진을 바꾸면 화면이 바로 그쪽 값을 보여 줌
    public void Post()
    {
        List<UsageWindow> claude = _planUsage.Windows();
        string claudeHint = null == _planUsage.ModDir
            ? "사용량 표시를 켜지 못했어요 (이 PC 에 파일을 쓰지 못함)"
            : "이 앱에서 연 Claude 가 한 번 답하면 채워져요 (계속 비어 있으면 Claude Code 를 최신으로 업데이트해 보세요)";
        DateTime? claudeAt = _planUsage.LastReceivedUtc();
        UsageSnapshot? gpt = _codexSnapshot;
        List<UsageWindow> gptWindows = gpt?.Windows.Where(window => window.IsLive(DateTime.UtcNow)).ToList() ?? new List<UsageWindow>();
        string gptHint = gpt?.Error ?? (null == gpt ? "Codex 기록 읽는 중…" : "GPT 가 답할 때마다 Codex 가 한도를 기록해요");
        UsageSnapshot? gemini = _geminiSnapshot;
        List<UsageWindow> geminiWindows = gemini?.Windows.Where(window => window.IsLive(DateTime.UtcNow)).ToList() ?? new List<UsageWindow>();
        string geminiHint = gemini?.Error ?? (null == gemini ? "GEMINI 한도 읽는 중…" : "GEMINI 창 안에서 /quota 로도 볼 수 있어요");
        if (_agyStopped)
        {
            geminiHint = "이번 실행 동안 GEMINI 한도 읽기를 멈췄어요 — GEMINI 창 안에서 /quota 로 보세요";
        }

        _post(new
        {
            t = "usage",
            engine = _terminal.Ai,   // 지금 도는 엔진·고른 값도 같이 (엔진 알림을 놓쳐도 30초 안에 맞춰짐)
            choice = _config.AiEngine,
            claude = UsageBody(claude, claudeHint, claudeAt, Array.Empty<string>()),
            gpt = UsageBody(gptWindows, gptHint, gpt?.ReceivedUtc, gpt?.Details ?? Array.Empty<string>()),
            gemini = UsageBody(geminiWindows, geminiHint, gemini?.ReceivedUtc, gemini?.Details ?? Array.Empty<string>()),
        });
    }

    private static object UsageBody(List<UsageWindow> windows, string hint, DateTime? receivedUtc, IReadOnlyList<string> details)
    {
        var items = windows.Select(window => new
        {
            label = window.Label,
            percent = window.UsedPercent,
            resetsAt = null == window.ResetsAtUtc ? (long?)null : new DateTimeOffset(DateTime.SpecifyKind(window.ResetsAtUtc.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            minutes = window.WindowMinutes,
        }).ToList();
        long? received = null == receivedUtc ? null : new DateTimeOffset(DateTime.SpecifyKind(receivedUtc.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return new { windows = items, hint, received, details };
    }
}
