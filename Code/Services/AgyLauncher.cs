using System.IO;
using System.Text;
using GameMaker.Pty;

namespace GameMaker.Services;

// GEMINI 칸 = Google Antigravity CLI (agy) 대화형 세션 (Claude·GPT 칸과 같은 터미널 길 — Pty/IPty.cs). 실행 파일 찾기 · 인자 만들기.
// agy = npm 이 아닌 실행 파일 하나 — 공식 설치 스크립트가 %LOCALAPPDATA%\agy\bin\agy.exe (맥 = ~/.local/bin/agy) 에 둔다. Node.js 필요 없음.
// 모델은 꼭 지정: agy 기본 모델은 따로 세는 비싼 묶음(Claude·GPT)이라 무료 Gemini 묶음 모델을 명령줄로 (AppConfig.AgyModel).
// 권한 = --mode accept-edits (파일 고치기는 묻지 않고, 셸 명령은 승인창) + 쓰기 폴더 --add-dir (GPT 칸과 같은 범위). 기본값 = AppConfig.AgyArgs
// 안내(지침) 주기: agy 에는 이번 실행에만 붙는 시스템 프롬프트 인자가 없다. 규칙 파일(~/.gemini/GEMINI.md · 게임 폴더 GEMINI.md·.agents)은
//   사용자의 개인 설정이나 게임 파일을 건드리므로 쓰지 않고, 지침을 앱 설정 폴더 안 gemini\gemini-context.md 에 써서
//   첫 입력(-i)으로 "이 파일을 먼저 읽어라" 한 줄을 준다. 이어 하기(--continue)는 이미 읽은 대화라 첫 입력 없이
public static class AgyLauncher
{
    // 설치 한 줄 (터미널 칸 안내 글 = wwwroot/terminal.js 도 같은 줄)
    public static string InstallLine
    {
        get
        {
            if (false == OperatingSystem.IsWindows())
            {
                return "curl -fsSL https://antigravity.google/cli/install.sh | bash";
            }

            return "irm https://antigravity.google/cli/install.ps1 | iex";
        }
    }

    // 무료 Gemini 묶음 모델 (허브 앱의 허브 AI 와 같은 값)
    public const string DefaultModel = "gemini-3.8-flash-high";

    private const string ContextDirName = "gemini";
    private const string ContextFileName = "gemini-context.md";

    public sealed record Request(string WorkDir, string DataDir, string TeamDocs, string Context, bool ContinueLast, string Model, string ExtraArgs);

    // 실행 파일 찾기. 설정이 기본값 "agy" 가 아니면 그 경로 그대로. 기본값이면 PATH → 설치 스크립트 자리 (설치 직후 PATH 가 아직 옛것이어도 찾게).
    // 못 찾으면 ""
    public static string Find(string configured)
    {
        if (false == string.Equals(configured, "agy", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        List<string> dirs = new();
        AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH"));
        string name = "agy";
        if (OperatingSystem.IsWindows())
        {
            // 앱을 켠 뒤에 깔았다면 이 앱의 PATH 는 옛것이라 레지스트리 값도 봄
            AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPathDirs(dirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"));
            name = "agy.exe";
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
        }

        foreach (string dir in dirs)
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

        return "";
    }

    // 터미널에 띄울 인자
    public static string BuildArgs(Request request)
    {
        List<string> args = new();
        if (request.ContinueLast)
        {
            args.Add("--continue");
        }

        string model = request.Model.Trim();
        if (0 == model.Length)
        {
            model = DefaultModel;
        }

        args.Add("--model");
        args.Add(CommandLine.Quote(model));
        if (false == string.IsNullOrWhiteSpace(request.ExtraArgs))
        {
            args.Add(request.ExtraArgs.Trim());
        }

        // 쓰기를 더 줄 폴더: 팀 문서(현황·기록)·그림 워크플로·요청 파일 + 지침 파일 폴더(읽기용). 게임 폴더는 작업 폴더라 기본으로 들어감
        string contextDir = Path.Combine(AppConfig.LocalAppDir, ContextDirName);
        foreach (string dir in WritableDirs(request, contextDir))
        {
            args.Add("--add-dir");
            args.Add(CommandLine.Quote(dir));
        }

        if (false == request.ContinueLast && request.Context.Length > 0)
        {
            string pointer = WriteContextFile(contextDir, request.Context);
            args.Add("-i");
            args.Add(CommandLine.Quote($"먼저 이 파일을 끝까지 읽고 그 지침을 이 대화 내내 따른다: {pointer} — 다 읽었으면 준비됐다고 한 줄만 말하고 사장님 말을 기다린다."));
        }

        return string.Join(' ', args);
    }

    private static List<string> WritableDirs(Request request, string contextDir)
    {
        List<string> dirs = new();
        foreach (string dir in new[] { request.TeamDocs, Path.Combine(request.DataDir, "workflows"), Path.Combine(request.DataDir, "requests"), contextDir })
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
                // 못 만들면 그 폴더만 빼고 (agy 가 승인을 물음)
            }
        }

        return dirs;
    }

    private static string WriteContextFile(string dir, string text)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ContextFileName);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
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
