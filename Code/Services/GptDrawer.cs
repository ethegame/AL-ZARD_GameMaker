using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GameMaker.Pty;

namespace GameMaker.Services;

// 그림 = GPT. 제어 창구 POST /draw (Views/MainWindow.Draw.cs) 가 부른다 — 터미널이 어떤 엔진이든 그림은 Codex 의 그림 도구($imagegen)로.
// 길: 앱 설정 폴더 안 빈 작업 폴더에서 `codex exec --json -s read-only --skip-git-repo-check -C <폴더> -` (프롬프트 = stdin, UTF-8 BOM 없이)
//   → stdout JSON 줄 thread.started.thread_id → <CODEX_HOME 또는 ~/.codex>\generated_images\<thread_id>\*.png → 게임 폴더/<dir>/<name>.png 로 복사.
//   read-only 샌드박스여도 그림 도구는 됨 (11/26 실측) — 그림 말고 다른 파일은 못 만들게.
// 시간 제한 6분 (넘으면 프로세스 트리 종료). 실행 파일 찾기·.cmd 껍데기 풀기 = Services/CodexLauncher.cs
public sealed class GptDrawer
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(6);
    private const string MissingDependencyText = "Missing optional dependency";
    private const string WorkDirName = "gpt-draw";

    private readonly object _healthLock = new();
    private string _healthPath = "";
    private Task<bool>? _health;

    public sealed record Result(List<string> Images, string? Error);

    // 실행 검사 (`codex --version` 끝 코드 0, 실제 실행 파일이 빠진 npm 설치 걸러냄). 경로별로 이번 실행 동안 한 번
    public Task<bool> CheckHealthAsync(string codexPath)
    {
        lock (_healthLock)
        {
            if (null == _health || _healthPath != codexPath)
            {
                _healthPath = codexPath;
                _health = RunHealthAsync(codexPath);
            }

            return _health;
        }
    }

    private static async Task<bool> RunHealthAsync(string codexPath)
    {
        try
        {
            using Process process = new() { StartInfo = StartInfo(codexPath, "--version", Path.GetTempPath()) };
            process.Start();
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource cancel = new(HealthTimeout);
            try
            {
                await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return true;   // 느린 것뿐일 수 있어 막지 않음 (실제 실패는 그릴 때 드러남)
            }

            string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            return 0 == process.ExitCode && false == output.Contains(MissingDependencyText, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 백그라운드에서 부를 것. 성공 = 새로 생긴 PNG 경로들 (Codex 그림 폴더 안 — 복사는 부르는 쪽)
    public static async Task<Result> DrawAsync(string codexPath, string prompt, CancellationToken token)
    {
        string workDir = Path.Combine(AppConfig.LocalAppDir, WorkDirName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string args = $"exec --json -s read-only --skip-git-repo-check -C {CommandLine.Quote(workDir)} -";
            using Process process = new() { StartInfo = StartInfo(codexPath, args, workDir) };
            process.Start();
            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            process.StandardInput.Close();

            string threadId = "";
            string lastError = "";
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(RunTimeout);
            try
            {
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
                    if (null == line)
                    {
                        break;
                    }

                    ReadEvent(line, ref threadId, ref lastError);
                }

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (token.IsCancellationRequested)
                {
                    return new Result(new List<string>(), "중단됨");
                }

                return new Result(new List<string>(), $"{RunTimeout.TotalMinutes:0}분 안에 안 끝나서 멈췄어요");
            }

            List<string> images = FindImages(threadId);
            if (images.Count > 0)
            {
                return new Result(images, null);
            }

            string why = lastError;
            if (0 == why.Length)
            {
                why = Short(await stderr.ConfigureAwait(false));
            }

            if (0 == threadId.Length)
            {
                return new Result(images, $"Codex 가 시작하지 못했어요 (끝 코드 {process.ExitCode}) {why}".Trim());
            }

            return new Result(images, $"GPT 가 그림을 만들지 않았어요 (끝 코드 {process.ExitCode}) {why}".Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다음에 지워도 됨 — 빈 작업 폴더일 뿐
            }
        }
    }

    // JSON 줄 하나: thread.started → 대화 id, error·turn.failed → 마지막 오류 글
    private static void ReadEvent(string line, ref string threadId, ref string lastError)
    {
        if (false == line.TrimStart().StartsWith('{'))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string type = "";
            if (root.TryGetProperty("type", out JsonElement typeValue) && JsonValueKind.String == typeValue.ValueKind)
            {
                type = typeValue.GetString() ?? "";
            }

            if ("thread.started" == type && root.TryGetProperty("thread_id", out JsonElement id) && JsonValueKind.String == id.ValueKind)
            {
                threadId = id.GetString() ?? "";
                return;
            }

            if ("error" == type && root.TryGetProperty("message", out JsonElement message) && JsonValueKind.String == message.ValueKind)
            {
                lastError = Short(message.GetString() ?? "");
                return;
            }

            if ("turn.failed" == type && root.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement failMessage) && JsonValueKind.String == failMessage.ValueKind)
            {
                lastError = Short(failMessage.GetString() ?? "");
            }
        }
        catch (JsonException)
        {
            // JSON 이 아닌 줄은 건너뜀
        }
    }

    private static List<string> FindImages(string threadId)
    {
        List<string> images = new();
        if (0 == threadId.Length || threadId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return images;
        }

        string dir = Path.Combine(CodexLauncher.CodexHome(), "generated_images", threadId);
        try
        {
            if (Directory.Exists(dir))
            {
                images.AddRange(new DirectoryInfo(dir).GetFiles("*.png").OrderBy(file => file.LastWriteTimeUtc).Select(file => file.FullName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 못 읽으면 "그림 없음" 으로
        }

        return images;
    }

    private static ProcessStartInfo StartInfo(string codexPath, string args, string workDir)
    {
        (string exe, string prefix, string? cmdPath) = CodexLauncher.ResolveEntry(codexPath);
        string fileName = exe;
        string arguments = (prefix + " " + args).Trim();
        if (null != cmdPath)
        {
            // 노드로 바로 못 띄우는 .cmd 껍데기 — cmd.exe 를 거침 (인자에 따옴표 경로뿐이라 깨질 글자 없음)
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            arguments = $"/d /s /c \"\"{cmdPath}\" {args}\"";
        }

        return new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir,
        };
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 이미 끝남
        }
    }

    private static string Short(string text)
    {
        const int Max = 200;
        string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (one.Length > Max)
        {
            one = one[..Max] + "…";
        }

        return one;
    }
}
