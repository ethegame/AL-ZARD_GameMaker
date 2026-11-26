using System.IO;
using System.Text;
using System.Text.Json;

namespace GameMaker.Services;

// GPT(Codex CLI) 사용량 한도 읽기. 출처 = Codex 가 대화마다 남기는 기록
//   <CODEX_HOME 또는 ~/.codex>\sessions\YYYY\MM\DD\rollout-*.jsonl (+ archived_sessions\*.jsonl) 의
//   token_count 줄 payload.rate_limits.primary/secondary {used_percent, window_minutes, resets_at(유닉스 초)} · plan_type · credits — 답할 때마다 한 줄, 계정 단위.
// 파일이 수십 MB 라 끝부분만 읽는다. 후보 파일 여럿 중 줄 시각이 가장 늦은 것을 씀. 백그라운드에서 부를 것 (다른 스레드에서 동시에 부르지 않는다)
public sealed class CodexRateLimits
{
    private const int RecentDays = 8;          // 주간 창(7일) + 하루
    private const int MaxFiles = 6;            // 최근에 쓴 파일 몇 개까지 볼지
    private static readonly int[] TailBytes = { 512 * 1024, 4 * 1024 * 1024 };

    private string _lastKey = "";
    private UsageSnapshot? _last;

    public UsageSnapshot Read()
    {
        string home = CodexLauncher.CodexHome();
        List<FileInfo> files = RecentRollouts(home);
        if (0 == files.Count)
        {
            return UsageSnapshot.Fail(Directory.Exists(home) ? "최근 8일 Codex 대화 기록 없음" : "Codex 기록 폴더 없음 (GPT 를 아직 안 씀)");
        }

        List<FileInfo> candidates = files.Take(MaxFiles).ToList();
        string key = string.Join(";", candidates.Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"));
        if (key == _lastKey && null != _last)
        {
            return _last;
        }

        UsageSnapshot? found = null;
        foreach (FileInfo file in candidates)
        {
            string? line = LastRateLimitLine(file.FullName);
            UsageSnapshot? one = null == line ? null : Parse(line);
            if (null != one && (null == found || one.ReceivedUtc > found.ReceivedUtc))
            {
                found = one;
            }
        }

        _lastKey = key;
        _last = found ?? UsageSnapshot.Fail("최근 기록에 한도 줄이 없음 (GPT 가 한 번 답하면 생겨요)");
        return _last;
    }

    private static List<FileInfo> RecentRollouts(string home)
    {
        List<FileInfo> files = new();
        DateTime since = DateTime.UtcNow.AddDays(-RecentDays);
        AddJsonl(files, Path.Combine(home, "sessions"), since, SearchOption.AllDirectories);
        AddJsonl(files, Path.Combine(home, "archived_sessions"), since, SearchOption.TopDirectoryOnly);
        files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        return files;
    }

    private static void AddJsonl(List<FileInfo> files, string dir, DateTime sinceUtc, SearchOption option)
    {
        try
        {
            if (false == Directory.Exists(dir))
            {
                return;
            }

            foreach (FileInfo info in new DirectoryInfo(dir).EnumerateFiles("*.jsonl", option))
            {
                if (info.LastWriteTimeUtc >= sinceUtc)
                {
                    files.Add(info);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 폴더를 못 읽으면 그 폴더만 건너뜀
        }
    }

    private static bool IsRateLimitLine(string line)
    {
        return line.Contains("\"token_count\"", StringComparison.Ordinal) && line.Contains("\"rate_limits\":{", StringComparison.Ordinal);
    }

    // 파일 끝 512KB → 없으면 4MB 안에서 마지막 한도 줄 (첫 줄은 잘린 줄일 수 있어 버림)
    private static string? LastRateLimitLine(string path)
    {
        foreach (int tail in TailBytes)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, stream.Length - tail);
                bool isWholeFile = 0 == start;
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[stream.Length - start];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                string[] lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
                for (int i = lines.Length - 1; i >= (isWholeFile ? 0 : 1); --i)
                {
                    if (IsRateLimitLine(lines[i]))
                    {
                        return lines[i];
                    }
                }

                if (isWholeFile)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;   // 쓰는 중 잠김 등 — 다음 주기에 다시
            }
        }

        return null;
    }

    private static UsageSnapshot? Parse(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (false == root.TryGetProperty("payload", out JsonElement payload) || false == payload.TryGetProperty("rate_limits", out JsonElement limits) || JsonValueKind.Object != limits.ValueKind)
            {
                return null;
            }

            DateTime received = DateTime.UtcNow;
            if (root.TryGetProperty("timestamp", out JsonElement stamp) && stamp.TryGetDateTime(out DateTime parsed))
            {
                received = parsed.ToUniversalTime();
            }

            List<UsageWindow> windows = new();
            AddWindow(windows, limits, "primary");
            AddWindow(windows, limits, "secondary");
            if (0 == windows.Count)
            {
                return null;
            }

            List<string> details = new();
            string? plan = StringOf(limits, "plan_type");
            if (null != plan)
            {
                details.Add($"요금제: {plan}");
            }

            return new UsageSnapshot(windows, received, details, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void AddWindow(List<UsageWindow> windows, JsonElement limits, string name)
    {
        if (false == limits.TryGetProperty(name, out JsonElement item) || JsonValueKind.Object != item.ValueKind)
        {
            return;
        }

        if (false == item.TryGetProperty("used_percent", out JsonElement used) || JsonValueKind.Number != used.ValueKind)
        {
            return;
        }

        double? minutes = null;
        if (item.TryGetProperty("window_minutes", out JsonElement window) && JsonValueKind.Number == window.ValueKind)
        {
            minutes = window.GetDouble();
        }

        DateTime? resets = null;
        if (item.TryGetProperty("resets_at", out JsonElement at) && JsonValueKind.Number == at.ValueKind)
        {
            resets = DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()).UtcDateTime;
        }

        windows.Add(new UsageWindow(null != minutes ? UsageWindow.LabelOf(minutes.Value) : name, used.GetDouble(), resets, minutes));
    }

    private static string? StringOf(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value) && JsonValueKind.String == value.ValueKind)
        {
            return value.GetString();
        }

        return null;
    }
}
