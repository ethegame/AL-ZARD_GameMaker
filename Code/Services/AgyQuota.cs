using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GameMaker.Services;

// GEMINI(Antigravity CLI, agy) 한도 읽기. agy 는 한도를 파일에 남기지 않아서 `agy -p /quota --output-format json` 을 부른다
//   (읽기 전용 slash 명령은 print 모드에서 모델 호출 없이 답함 — 허브 앱 실측 3초 · 토큰 0 · 대화 생성 없음).
//   command.data.groups[].buckets[] {id, window, remaining_fraction, reset_time}. 주간 묶음만 있음 (5 Hours 창 없음):
//   gemini-* = Gemini (Flash·Pro) — GEMINI 칸이 쓰는 무료 묶음, 3p-* = Antigravity 안 Claude·GPT-OSS (따로 세는 한도).
// 안전장치: 답에 모델이 돈 흔적(토큰·턴·대화 id)이 있으면 부르는 쪽이 이번 실행 동안 다시 안 부른다 (ModelSpendSign).
// 셸 없이 Process 로 직접 — 셸을 거치면 "/quota" 가 경로로 바뀌어 일반 질문이 될 수 있음
public static class AgyQuota
{
    public const string Args = "-p /quota --output-format json";
    private const string WeeklyWindow = "weekly";
    private const string GeminiBucketPrefix = "gemini";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(40);

    // 성공 = stdout JSON, 실패 = 이유
    public static async Task<(string? Json, string? Error)> RunAsync(string exePath)
    {
        try
        {
            ProcessStartInfo info = new(exePath, Args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),   // 게임 폴더 설정·신뢰 묻기가 끼어들지 않게
            };
            using Process process = new() { StartInfo = info };
            process.Start();
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource cancel = new(RunTimeout);
            try
            {
                await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                    // 이미 끝남
                }

                return (null, $"{RunTimeout.TotalSeconds:0}초 안에 안 끝남");
            }

            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            int start = output.IndexOf('{');
            if (start < 0)
            {
                string reason = output.Trim();
                if (error.Trim().Length > 0)
                {
                    reason = error.Trim();
                }

                return (null, $"한도 답이 없음 (끝 코드 {process.ExitCode}) {Short(reason)}");
            }

            return (output[start..], null);
        }
        catch (Exception ex)
        {
            return (null, $"실행 실패: {ex.Message}");
        }
    }

    // 모델이 돈 흔적: 토큰 > 0 · 턴 > 0 · 대화 id 있음 (정상 /quota = 셋 다 0·빈 값)
    public static string? ModelSpendSign(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.TryGetProperty("total_tokens", out JsonElement tokens) && JsonValueKind.Number == tokens.ValueKind && tokens.GetDouble() > 0)
            {
                return $"토큰 {tokens.GetDouble():0} 씀";
            }

            if (root.TryGetProperty("num_turns", out JsonElement turns) && JsonValueKind.Number == turns.ValueKind && turns.GetDouble() > 0)
            {
                return $"턴 {turns.GetDouble():0}";
            }

            string conversation = StringOf(root, "conversation_id") ?? "";
            if (conversation.Length > 0)
            {
                return "대화 생성";
            }
        }
        catch (JsonException)
        {
            // JSON 이 아니면 Parse 가 실패를 알림
        }

        return null;
    }

    // 미터에는 Gemini 묶음만 (GEMINI 칸이 쓰는 모델의 한도). 다른 묶음은 툴팁 덧붙임 줄로
    public static UsageSnapshot Parse(string json, DateTime receivedUtc)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            bool hasGroups = root.TryGetProperty("command", out JsonElement command)
                && command.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("groups", out JsonElement groups)
                && JsonValueKind.Array == groups.ValueKind;
            if (false == hasGroups)
            {
                return UsageSnapshot.Fail("한도 정보가 없는 답 (로그인 전일 수 있어요 — GEMINI 칸에서 한 번 로그인)");
            }

            List<UsageWindow> windows = new();
            List<string> details = new();
            foreach (JsonElement group in root.GetProperty("command").GetProperty("data").GetProperty("groups").EnumerateArray())
            {
                if (false == group.TryGetProperty("buckets", out JsonElement buckets) || JsonValueKind.Array != buckets.ValueKind)
                {
                    continue;
                }

                string groupName = StringOf(group, "name") ?? "?";
                foreach (JsonElement bucket in buckets.EnumerateArray())
                {
                    if (WeeklyWindow != StringOf(bucket, "window"))
                    {
                        continue;
                    }

                    if (false == bucket.TryGetProperty("remaining_fraction", out JsonElement remaining) || JsonValueKind.Number != remaining.ValueKind)
                    {
                        continue;
                    }

                    DateTime? resets = null;
                    if (bucket.TryGetProperty("reset_time", out JsonElement reset) && reset.TryGetDateTime(out DateTime at))
                    {
                        resets = at.ToUniversalTime();
                    }

                    double used = Math.Clamp((1 - remaining.GetDouble()) * 100, 0, 100);
                    if (IsGeminiBucket(StringOf(bucket, "id")))
                    {
                        windows.Add(new UsageWindow("Weekly", used, resets, TimeSpan.FromDays(7).TotalMinutes));
                    }
                    else
                    {
                        details.Add($"{groupName} (따로 세는 한도) · Weekly 남음 {100 - used:0.#}%");
                    }
                }
            }

            if (0 == windows.Count)
            {
                return UsageSnapshot.Fail("Gemini 주간 한도 묶음이 없음");
            }

            return new UsageSnapshot(windows, receivedUtc, details, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return UsageSnapshot.Fail($"한도 답 해석 실패: {ex.Message}");
        }
    }

    private static bool IsGeminiBucket(string? id)
    {
        return (id ?? "").StartsWith(GeminiBucketPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? StringOf(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value) && JsonValueKind.String == value.ValueKind)
        {
            return value.GetString();
        }

        return null;
    }

    private static string Short(string text)
    {
        const int Max = 160;
        string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (one.Length > Max)
        {
            one = one[..Max] + "…";
        }

        return one;
    }
}
