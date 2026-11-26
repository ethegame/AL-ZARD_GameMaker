using System.IO;
using GameMaker.Pty;

namespace GameMaker.Services;

// 오른쪽 터미널 = 진짜 AI 대화형 세션 1개 (Windows = ConPTY, 맥 = forkpty — Pty/IPty.cs PtyLauncher). 엔진 = Ai (claude = Claude Code · gpt = Codex CLI · gemini = Antigravity CLI, 고르는 곳 = Views/TerminalSession.cs).
// 권한 확인창·긴 작업·도구 출력이 그대로 보인다. 작업 폴더 = 지금 프로젝트의 게임 폴더 (없으면 DataDir), 안내(시스템 프롬프트) = claude_context.md (세 엔진 모두 — 엔진별 절 적용 뒤).
// 이어 하기 = Claude 는 --continue, Codex 는 resume --last (이 작업 폴더의 마지막 대화 → 프로젝트마다·엔진마다 대화가 갈림). 앱을 다시 켜도 대화가 이어진다.
// GEMINI 는 작업 폴더별 지난 대화를 알 길이 없어 켤 때는 늘 새 대화 — 끝난 뒤 Enter·[이어 하기] 만 agy --continue (가장 최근 대화).
// Codex 쪽 인자 만들기 = Services/CodexLauncher.cs, agy 쪽 = Services/AgyLauncher.cs
public sealed class ClaudeTerminal : IDisposable
{
    // 백그라운드 스레드에서 호출됨
    public event Action<string>? Output;
    public event Action<int>? Exited;

    private const string ContextFileName = "claude_context.md";
    // 앱을 Claude 세션 안에서 띄웠을 때 중첩 세션으로 오인하지 않게.
    // CLAUDE_CODE_CHILD_SESSION 이 남으면 기록 저장이 꺼져 --continue 가 안 된다
    private static readonly string[] RemovedEnvVars = { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_CHILD_SESSION" };

    private readonly AppConfig _config;
    private readonly string _dataDir;
    private IPty? _pty;

    public ClaudeTerminal(AppConfig config, string dataDir)
    {
        _config = config;
        _dataDir = dataDir;
        Ai = AiEngines.Normalize(config.AiEngine);
    }

    public bool IsRunning => null != _pty;

    // 지금 실제 엔진 (AiEngines.Claude | Gpt | Gemini — 자동은 MainWindow 가 하나로 풀어서 넣음). 바꾼 뒤 Start 하면 그 엔진으로 뜬다
    public string Ai { get; set; }

    // Claude 사용량 확장 폴더 (Services/PlanUsageReceiver.cs — 있으면 이 앱이 띄우는 Claude 에만 --plugin-dir 로 붙임)
    public string? PlanUsageModDir { get; set; }

    // 지금 프로젝트의 팀 문서·게임 폴더 (절대 경로, 프로젝트 없으면 ""). 바꾼 뒤 Start 하면 새 context 로 뜬다
    public string TeamDocsPath { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string Engine { get; set; } = Project.EngineGodot; // "godot" | "web" — context 의 {Engine}·엔진별 절

    // 터미널 작업 폴더 = 지금 프로젝트의 게임 폴더 (프로젝트 없으면 DataDir).
    // 게임 폴더가 없으면 조용히 새로 만들지 않는다 — 데이터 폴더가 바뀌었거나 지워진 것일 수 있어 빈 폴더에서 대화를 이으면 헷갈림
    private string WorkDir()
    {
        if (0 == GameDir.Length)
        {
            Directory.CreateDirectory(_dataDir);
            return _dataDir;
        }

        return GameDir;
    }

    // 터미널을 띄울 수 없는 이유 (없으면 null) — 화면에 오류로 보여 줌
    public string? CheckWorkDir()
    {
        if (GameDir.Length > 0 && false == Directory.Exists(GameDir))
        {
            return $"이 프로젝트의 게임 폴더가 없습니다: {GameDir} — 폴더를 옮기거나 지웠다면 되돌리거나, 프로젝트를 삭제하고 새로 만들어 주세요.";
        }

        return null;
    }

    // 이 프로젝트 작업 폴더에 지난 대화 기록이 있는지 (Claude = ~/.claude/projects/<경로의 영숫자 외 문자를 '-' 로>/*.jsonl, Codex = 기록 첫 줄의 작업 폴더). 프로젝트 없음 = 새 대화
    public bool HasPreviousConversation()
    {
        if (0 == GameDir.Length)
        {
            return false;
        }

        if (AiEngines.Gpt == Ai)
        {
            return CodexLauncher.HasConversation(Path.GetFullPath(GameDir));
        }

        if (AiEngines.Gemini == Ai)
        {
            return false;
        }

        string encoded = new string(Path.GetFullPath(GameDir).TrimEnd(Path.DirectorySeparatorChar).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", encoded);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.jsonl").Any();
    }

    public void Start(short cols, short rows, bool continueLast)
    {
        Stop();
        if (AiEngines.Gpt == Ai)
        {
            StartCodex(cols, rows, continueLast);
            return;
        }

        if (AiEngines.Gemini == Ai)
        {
            StartAgy(cols, rows, continueLast);
            return;
        }

        List<string> args = new();
        if (continueLast)
        {
            args.Add("--continue");
        }

        string workDir = WorkDir();
        // DataDir = 작업 폴더가 게임 폴더일 때 팀 문서(projects\<id>\docs)·ComfyUI 그림·요청 등을 읽고 쓰려고. ComfyUI·Godot 폴더는 있을 때만.
        // --add-dir 가 아니라 이 세션에만 주는 설정(--settings 의 permissions.additionalDirectories)으로 붙인다: Claude Code 2.1.326+ 는
        // --add-dir 로 신뢰 안 된 폴더를 붙이면 켤 때마다 "Adding a new directory … No, exit / Yes, add it" 확인창을 띄우고(기본 = No, exit),
        // 폴더가 둘이면 똑같이 생긴 창이 연달아 떠 두 번째에서 Enter 를 누르면 바로 꺼진다 (1.1.1). 설정 쪽은 확인창 없이 파일 접근만 준다.
        // 사용자 설정 파일(~/.claude.json·~/.claude/settings.json)은 건드리지 않음 — 이번 세션 명령줄에만
        List<string> extraDirs = new();
        foreach (string dir in new[] { workDir == _dataDir ? "" : _dataDir, _config.ResolveComfyRoot(), GodotDir() })
        {
            if (dir.Length > 0 && Directory.Exists(dir))
            {
                extraDirs.Add(Path.GetFullPath(dir));
            }
        }

        if (extraDirs.Count > 0)
        {
            string settings = System.Text.Json.JsonSerializer.Serialize(new { permissions = new { additionalDirectories = extraDirs } });
            args.Add("--settings");
            args.Add(CommandLine.Quote(settings));
        }

        string? context = LoadContext();
        if (null != context)
        {
            args.Add("--append-system-prompt");
            args.Add(CommandLine.Quote(context));
        }

        // 사용량 미터용 확장 — 이 앱이 띄우는 Claude 에만 붙는다
        if (null != PlanUsageModDir && Directory.Exists(PlanUsageModDir))
        {
            args.Add("--plugin-dir");
            args.Add(CommandLine.Quote(PlanUsageModDir));
        }

        string exe = ClaudeExe();
        string extraArgs = (_config.ClaudeArgs ?? "").Trim();
        // 권한 모드를 설정에서 따로 안 정했으면 auto (1.1.5), 이 claude 가 auto 를 모르면 예전 기본 acceptEdits
        if (false == extraArgs.Contains("--permission-mode", StringComparison.Ordinal))
        {
            string mode = ClaudeAutoMode.ArgsFor(Path.IsPathRooted(exe) ? exe : FindOnPath(exe));
            if (0 == mode.Length)
            {
                mode = "--permission-mode acceptEdits";
            }

            args.Add(mode);
        }

        if (extraArgs.Length > 0)
        {
            args.Add(extraArgs);
        }

        Attach(PtyLauncher.Start(exe, string.Join(' ', args), workDir, cols, rows, RemovedEnvVars));
    }

    // GPT = Codex CLI. 못 찾으면 FileNotFoundException (화면이 설치 안내를 띄움)
    private void StartCodex(short cols, short rows, bool continueLast)
    {
        string exe = CodexLauncher.Find(Resolve(_config.CodexExe));
        if (0 == exe.Length)
        {
            throw new FileNotFoundException("Codex 를 찾지 못했습니다");
        }

        string workDir = WorkDir();
        CodexLauncher.Request request = new(workDir, _dataDir, TeamDocsPath, LoadContext() ?? "", continueLast, _config.CodexArgs);
        CodexLauncher.Command command = CodexLauncher.Build(exe, request);
        Attach(PtyLauncher.Start(command.Exe, command.Args, workDir, cols, rows, Array.Empty<string>()));
    }

    // GEMINI = Antigravity CLI. 못 찾으면 FileNotFoundException (화면이 설치 안내를 띄움)
    private void StartAgy(short cols, short rows, bool continueLast)
    {
        string exe = AgyLauncher.Find(Resolve(_config.AgyExe));
        if (0 == exe.Length)
        {
            throw new FileNotFoundException("Antigravity(agy) 를 찾지 못했습니다");
        }

        string workDir = WorkDir();
        AgyLauncher.Request request = new(workDir, _dataDir, TeamDocsPath, LoadContext() ?? "", continueLast, _config.AgyModel, _config.AgyArgs);
        Attach(PtyLauncher.Start(exe, AgyLauncher.BuildArgs(request), workDir, cols, rows, Array.Empty<string>()));
    }

    private void Attach(IPty pty)
    {
        // 엔진·프로젝트를 바꾸면 Stop 이 _pty 를 비우고 새 PTY 를 붙인다. 죽인 옛 PTY 의 늦은 출력·종료가
        // 새 세션을 "끝났다"로 보이게 하지 않도록, 지금 붙어 있는 PTY 의 것만 올린다.
        pty.Output += text =>
        {
            if (ReferenceEquals(_pty, pty))
            {
                Output?.Invoke(text);
            }
        };
        pty.Exited += code =>
        {
            if (!ReferenceEquals(_pty, pty))
            {
                return;
            }

            _pty = null;
            Exited?.Invoke(code);
        };
        _pty = pty;
    }

    public void Write(string text)
    {
        _pty?.Write(text);
    }

    public void Write(byte[] data)
    {
        _pty?.Write(data);
    }

    public void Resize(short cols, short rows)
    {
        _pty?.Resize(cols, rows);
    }

    public void Stop()
    {
        IPty? pty = _pty;
        _pty = null;
        pty?.Dispose();
    }

    // 설정이 기본값 "claude" 인데 이 앱의 PATH 에 없으면 공식 설치기 자리(~/.local/bin)를 봄 — 앱을 켠 뒤 Claude Code 를 깔면 PATH 가 아직 옛것이라서
    // "claude" 같은 이름만 있을 때 PATH 에서 실제 파일 (못 찾으면 "")
    private static string FindOnPath(string name)
    {
        string fileName = name;
        if (OperatingSystem.IsWindows() && false == name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = name + ".exe";
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private string ClaudeExe()
    {
        string configured = Resolve(_config.ClaudeExe);
        if (false == string.Equals(configured, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        string[] pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string fileName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        bool onPath = pathDirs.Any(dir => File.Exists(Path.Combine(dir.Trim(), fileName)));
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", fileName);
        return false == onPath && File.Exists(installed) ? installed : configured;
    }

    // Godot 실행 파일이 든 폴더 (못 찾았거나 PATH 이름뿐이면 "")
    private string GodotDir()
    {
        string exe = _config.ResolveGodotExe();
        return exe.Length > 0 && Path.IsPathRooted(exe) ? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "" : "";
    }

    // <exe 폴더>/claude_context.md 의 {자리표시자} 치환. 파일 없으면 null
    private string? LoadContext()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ContextFileName);
        if (false == File.Exists(path))
        {
            return null;
        }

        string comfyRoot = _config.ResolveComfyRoot();
        string godotExe = _config.ResolveGodotExe();
        return ApplyEngineSections(ApplySections(File.ReadAllText(path), "ai", Ai), Engine)
            .Replace("{Ai}", AiEngines.ToolName(Ai))
            .Replace("{Engine}", Engine == Project.EngineWeb ? "웹 (HTML5 + JS)" : "Godot 4.7.2")
            .Replace("{DataDir}", _dataDir)
            .Replace("{ComfyRoot}", comfyRoot.Length > 0 ? comfyRoot : "(없음 — 그림 도구를 아직 설치하지 않음)")
            .Replace("{ComfyUrl}", _config.ComfyUrl)
            .Replace("{ControlPort}", _config.ControlPort.ToString())
            .Replace("{TeamDocs}", TeamDocsPath.Length > 0 ? TeamDocsPath : "(프로젝트 없음 — 사장님이 맨 위 [새 프로젝트] 를 눌러야 함)")
            .Replace("{GameDir}", GameDir.Length > 0 ? GameDir : "(프로젝트 없음)")
            .Replace("{GodotExe}", godotExe.Length > 0 ? Resolve(godotExe) : "(Godot 를 찾지 못함 — AL-ZARD_GameMaker 를 다시 설치하면 들어옵니다)");
    }

    // <!--engine:godot--> … <!--/engine--> 처럼 줄 단위로 싼 절은 그 게임 엔진일 때만 남긴다 (표시 줄은 항상 지움)
    public static string ApplyEngineSections(string text, string engine)
    {
        return ApplySections(text, "engine", engine);
    }

    // 같은 모양으로 <!--ai:claude--> … <!--/ai--> = 그 AI(claude | gpt | gemini)일 때만. kind 가 다른 표시 줄은 건드리지 않는다
    private static string ApplySections(string text, string kind, string value)
    {
        string open = $"<!--{kind}:";
        string close = $"<!--/{kind}-->";
        System.Text.StringBuilder result = new();
        string? active = null;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(open, StringComparison.Ordinal) && trimmed.EndsWith("-->", StringComparison.Ordinal))
            {
                active = trimmed[open.Length..^"-->".Length].Trim();
                continue;
            }

            if (close == trimmed)
            {
                active = null;
                continue;
            }

            if (null == active || active == value)
            {
                result.Append(line).Append('\n');
            }
        }

        // 마지막 줄 끝에 줄바꿈을 덧붙이지 않게 (원문이 줄바꿈으로 안 끝났으면)
        if (result.Length > 0 && false == text.EndsWith('\n'))
        {
            result.Length--;
        }

        return result.ToString();
    }

    private static string Resolve(string path)
    {
        string expanded = AppConfig.ExpandPath(path);
        return expanded.Length > 0 && Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : expanded;
    }

    public void Dispose()
    {
        Stop();
    }
}
