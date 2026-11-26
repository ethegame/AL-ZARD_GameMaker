using System.IO;
using System.Text;
using System.Text.Json;
using GameMaker.Pty;

namespace GameMaker.Services;

// GPT 칸 = OpenAI Codex CLI 대화형 세션 (Claude 칸과 같은 터미널 길 — Pty/IPty.cs). 실행 파일 찾기 · 인자 만들기 · 지난 대화 찾기.
// 권한 = 작업 폴더 안에만 쓰기 (-s workspace-write), 그 밖·인터넷 명령은 승인창 (-a on-request). 기본값은 AppConfig.CodexArgs
// 안내(지침) 주기 = 실행 때마다 명령줄 설정 -c developer_instructions 로 (이번 실행에만 적용). 게임 폴더에 AGENTS.md 같은 파일을 만들지 않는다 —
//   사용자의 게임 파일과 섞이지 않고, 앱이 지침을 고쳐도 낡은 파일이 남지 않는다.
// 이어 하기 = codex resume --last (Codex 는 작업 폴더별로 마지막 대화를 찾는다)
public static class CodexLauncher
{
    // 설치 한 줄 (Node.js 필요 — 터미널 칸 안내 글 = wwwroot/terminal.js 도 같은 줄)
    public const string InstallLine = "npm install -g @openai/codex@latest";

    private const string ContextFileName = "codex-context.md";
    private const int MaxInlineArgsChars = 28000;     // Windows 명령줄 한도 32767 아래 여유
    private const int MaxFirstLineBytes = 256 * 1024;
    private const int MaxSessionFiles = 300;

    public sealed record Request(string WorkDir, string DataDir, string TeamDocs, string Context, bool ContinueLast, string ExtraArgs);

    public sealed record Command(string Exe, string Args);

    // Codex 기록 폴더 (CODEX_HOME 이 있으면 그곳, 없으면 ~/.codex)
    public static string CodexHome()
    {
        string? env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (false == string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    // 실행 파일 찾기. 설정이 기본값 "codex" 가 아니면 그 경로 그대로. 기본값이면 PATH → npm 전역 폴더(설치 직후 PATH 가 아직 옛것이어도 찾게).
    // 못 찾으면 ""
    public static string Find(string configured)
    {
        if (false == string.Equals(configured, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        List<string> dirs = new();
        AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] names;
        if (OperatingSystem.IsWindows())
        {
            // 앱을 켠 뒤에 깔았다면 이 앱의 PATH 는 옛것이라 레지스트리 값도 봄
            AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            names = new[] { "codex.exe", "codex.cmd" };
        }
        else
        {
            dirs.Add(Path.Combine(home, ".npm-global", "bin"));
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
            names = new[] { "codex" };
        }

        foreach (string dir in dirs)
        {
            foreach (string name in names)
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 에 이상한 글자가 든 줄은 건너뜀
                }
            }
        }

        return "";
    }

    // 터미널에 띄울 (실행 파일, 인자). codexPath = Find 의 결과
    public static Command Build(string codexPath, Request request)
    {
        (string exe, string prefix, string? cmdPath) = ResolveEntry(codexPath);
        string inline = BuildArgs(prefix, request, request.Context);
        if (null == cmdPath && inline.Length <= MaxInlineArgsChars)
        {
            return new Command(exe, inline);
        }

        // 지침이 너무 길거나 .cmd 껍데기(cmd.exe 를 거치면 따옴표·& 같은 글자가 깨짐)뿐이면 지침 대신 "이 파일을 먼저 읽어라" 한 줄을 준다
        string pointer = WriteContextFile(request.Context);
        string shortText = $"먼저 이 파일을 끝까지 읽고 그 지침을 따른다: {pointer}";
        string rest = BuildArgs(prefix, request, shortText);
        if (null == cmdPath)
        {
            return new Command(exe, rest);
        }

        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new Command(comSpec, $"/d /s /c \"\"{cmdPath}\" {rest}\"");
    }

    private static string BuildArgs(string prefix, Request request, string instructions)
    {
        List<string> args = new();
        if (prefix.Length > 0)
        {
            args.Add(prefix);
        }

        if (request.ContinueLast)
        {
            args.Add("resume");
            args.Add("--last");
        }

        if (false == string.IsNullOrWhiteSpace(request.ExtraArgs))
        {
            args.Add(request.ExtraArgs.Trim());
        }

        // 쓰기를 더 줄 폴더: 팀 문서(현황·기록)·그림 워크플로·요청 파일. 게임 폴더는 작업 폴더라 기본으로 쓰기 가능
        foreach (string dir in WritableDirs(request))
        {
            args.Add("--add-dir");
            args.Add(CommandLine.Quote(dir));
        }

        args.Add("-c");
        args.Add(CommandLine.Quote("developer_instructions=" + TomlString(instructions)));
        return string.Join(' ', args);
    }

    private static List<string> WritableDirs(Request request)
    {
        List<string> dirs = new();
        foreach (string dir in new[] { request.TeamDocs, Path.Combine(request.DataDir, "workflows"), Path.Combine(request.DataDir, "requests") })
        {
            if (0 == dir.Length)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
                dirs.Add(Path.GetFullPath(dir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 못 만들면 그 폴더만 쓰기 없이 (Codex 가 승인을 물음)
            }
        }

        return dirs;
    }

    // (실행 파일, 앞에 붙일 인자, .cmd 껍데기 경로 — 직접 못 띄울 때만). 그림 창구(Services/GptDrawer.cs)도 같은 길로 띄운다
    public static (string Exe, string Prefix, string? CmdPath) ResolveEntry(string codexPath)
    {
        if (false == OperatingSystem.IsWindows() || false == codexPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return (codexPath, "", null);
        }

        // npm 껍데기(codex.cmd)는 안에서 node 로 codex.js 를 돌릴 뿐 — cmd.exe 를 거치지 않게 node 로 바로 띄운다
        string dir = Path.GetDirectoryName(codexPath) ?? "";
        string script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
        string node = FindNode(dir);
        if (File.Exists(script) && node.Length > 0)
        {
            return (node, CommandLine.Quote(script), null);
        }

        return (codexPath, "", codexPath);
    }

    private static string FindNode(string npmDir)
    {
        List<string> dirs = new() { npmDir };
        AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH"));
        AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        foreach (string dir in dirs)
        {
            try
            {
                string candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // 이상한 PATH 줄은 건너뜀
            }
        }

        return "";
    }

    private static string WriteContextFile(string text)
    {
        string path = Path.Combine(AppConfig.LocalAppDir, ContextFileName);
        Directory.CreateDirectory(AppConfig.LocalAppDir);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    // TOML 기본 문자열 ("…") — 줄바꿈·따옴표·역슬래시·제어문자를 이스케이프. 한글은 그대로
    public static string TomlString(string text)
    {
        StringBuilder sb = new("\"");
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || 0x7f == c)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    // 이 작업 폴더에서 한 지난 Codex 대화가 있나 — 기록 파일 첫 줄(session_meta)의 cwd 를 봄. 최근 파일 몇백 개까지만
    public static bool HasConversation(string workDir)
    {
        if (0 == workDir.Length)
        {
            return false;
        }

        try
        {
            string sessions = Path.Combine(CodexHome(), "sessions");
            if (false == Directory.Exists(sessions))
            {
                return false;
            }

            string target = NormalizeDir(workDir);
            IEnumerable<FileInfo> files = new DirectoryInfo(sessions).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxSessionFiles);
            foreach (FileInfo file in files)
            {
                string? cwd = ReadSessionCwd(file.FullName);
                if (null != cwd && NormalizeDir(cwd) == target)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 기록을 못 읽으면 "없음" — 이어 하기 대신 새 대화로 뜸
        }

        return false;
    }

    private static string? ReadSessionCwd(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[(int)Math.Min(stream.Length, MaxFirstLineBytes)];
            int read = stream.Read(buffer, 0, buffer.Length);
            int end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (end < 0)
            {
                end = read;
            }

            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, end));
            if (doc.RootElement.TryGetProperty("payload", out JsonElement payload) && payload.TryGetProperty("cwd", out JsonElement cwd) && JsonValueKind.String == cwd.ValueKind)
            {
                return cwd.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 쓰는 중이거나 첫 줄이 잘렸으면 건너뜀
        }

        return null;
    }

    private static string NormalizeDir(string path)
    {
        string text = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        text = text.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? text.ToLowerInvariant() : text;
    }

    private static void AddPathDirs(List<string> dirs, string? pathValue)
    {
        if (null == pathValue)
        {
            return;
        }

        foreach (string part in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
            if (dir.Length > 0)
            {
                dirs.Add(dir);
            }
        }
    }
}
