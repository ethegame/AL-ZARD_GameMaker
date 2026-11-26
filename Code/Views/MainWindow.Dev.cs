using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameMaker.Core;
using GameMaker.Services;

namespace GameMaker.Views;

// 개발 쪽: 오른쪽 AI 터미널 (Claude · GPT · GEMINI — 엔진 고르기·자동 = MainWindow.Ai.cs), 게임(Godot 프로젝트 · 웹 게임) 실행.
// 앱 자체 업데이트는 새 버전 설치 파일로 한다 (설치 = 업데이트, 데이터 폴더는 그대로) — 앱 안에서 다시 빌드·교체하는 길은 없다.
public partial class MainWindow
{
    private const string GodotProjectFile = "project.godot";
    private static readonly string[] SkippedGameDirs = { "outputs", "workflows", "picked", "logs", "requests", ".godot" };
    private static readonly Regex GodotNamePattern = new(@"^config/name\s*=\s*""(.*)""", RegexOptions.Multiline);
    private const string WebIndexFile = "index.html";
    private static readonly Regex HtmlTitlePattern = new(@"<title>([^<]*)</title>", RegexOptions.IgnoreCase);

    private readonly ClaudeTerminal _terminal;
    private readonly WebGameServer _webServer = new(); // 웹 프로젝트 [▶ 플레이] 용 (누를 때 켬, 앱 종료 때 끔)
    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _workflowDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private FileSystemWatcher? _workflowWatcher;
    private bool _terminalStartedOnce;
    private static readonly TimeSpan ContinueFailWindow = TimeSpan.FromSeconds(10);
    private DateTime? _continueStartedUtc;            // --continue 로 띄운 시각 (곧바로 끝나면 새 대화로 다시)
    private DateTime _terminalStartedUtc = DateTime.MinValue; // 터미널을 띄운 시각 (Codex 가 바로 끝나면 안내를 덧붙임)
    private (short Cols, short Rows) _lastTerminalSize = (80, 24);

    // 터미널 출력 합치기: PTY read 마다 보내지 않고 UI 틱 단위로 묶어서 보냄
    private readonly object _outputLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private bool _flushScheduled;

    private void InitializeDev()
    {
        InitializeProjects();
        _terminal.Output += QueueTerminalOutput;
        _terminal.Exited += code => Dispatcher.BeginInvoke(() => OnTerminalExited(code));

        // 터미널 claude 가 workflows/ 를 고치면 목록·폼 다시 읽기
        _workflowWatcher = new FileSystemWatcher(_workflows.Folder, "*.json") { EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _workflowDebounce.Stop();
            _workflowDebounce.Start();
        });
        _workflowWatcher.Created += changed;
        _workflowWatcher.Changed += changed;
        _workflowWatcher.Deleted += changed;
        _workflowWatcher.Renamed += (sender, e) => changed(sender, e);
        _workflowDebounce.Tick += (_, _) =>
        {
            _workflowDebounce.Stop();
            PostWorkflowList(null);
            Post(new { t = "wf-changed" });
        };

        _gameTimer.Tick += (_, _) => PostGames();
        _gameTimer.Start();
        InitializeTeams();
    }

    private void DisposeDev()
    {
        _gameTimer.Stop();
        _webServer.Dispose();
        DisposeTeams();
        _workflowWatcher?.Dispose();
        _terminal.Dispose();
    }

    private void OnDevMessage(string type, JsonNode message)
    {
        switch (type)
        {
            case "term-created": {
                // 앱 시작 후 첫 터미널 = 지난 대화 이어 하기
                if (false == _terminal.IsRunning)
                {
                    ChooseEngineForNewSession();
                    StartTerminal(ReadShort(message, "cols"), ReadShort(message, "rows"), false == _terminalStartedOnce && _terminal.HasPreviousConversation());
                }

                break;
            }
            case "ai-engine-set": {
                SetAiEngine(message["engine"]?.GetValue<string>());
                break;
            }
            case "term-start": {
                // [새 세션] = 자동이면 엔진을 다시 고름. 이어 하기는 지금 엔진 그대로 (그 엔진의 대화를 잇는 것이라)
                bool continueLast = true == message["continue"]?.GetValue<bool>();
                if (false == continueLast)
                {
                    ChooseEngineForNewSession();
                }

                StartTerminal(ReadShort(message, "cols"), ReadShort(message, "rows"), continueLast);
                break;
            }
            case "term-in": {
                _terminal.Write(message["d"]?.GetValue<string>() ?? "");
                break;
            }
            case "term-bin": {
                // xterm onBinary: 각 문자가 0~255 바이트 (마우스 리포트 등)
                _terminal.Write(Encoding.Latin1.GetBytes(message["d"]?.GetValue<string>() ?? ""));
                break;
            }
            case "term-resize": {
                _terminal.Resize(ReadShort(message, "cols"), ReadShort(message, "rows"));
                break;
            }
            case "term-copy": {
                string text = message["d"]?.GetValue<string>() ?? "";
                if (text.Length > 0)
                {
                    Clipboard.SetText(text);
                }

                break;
            }
            case "term-paste": {
                if (Clipboard.ContainsText())
                {
                    Post(new { t = "term-paste", d = Clipboard.GetText() });
                }
                // 텍스트 없이 이미지만 있으면 Alt+V(ESC v) → Claude Code 가 직접 클립보드 이미지를 붙인다 (Windows — 맥은 terminal.js 가 Ctrl+V 를 그대로 보냄).
                // Codex 는 Ctrl+V 가 클립보드 이미지 붙이기
                else if (Clipboard.ContainsImage())
                {
                    _terminal.Write(AiEngines.Gpt == _terminal.Ai ? "\x16" : "\x1bv");
                }

                break;
            }
            case "games": {
                PostGames();
                break;
            }
            case "game-run": {
                string dir = GameDir(message);
                if (CurrentIsWeb())
                {
                    PlayWeb(dir);
                }
                else
                {
                    StartGodot(dir, editor: false);
                }

                break;
            }
            case "game-edit": {
                if (CurrentIsWeb())
                {
                    throw new InvalidOperationException("웹 프로젝트는 에디터가 없습니다 — [폴더] 로 여세요.");
                }

                StartGodot(GameDir(message), editor: true);
                break;
            }
            case "game-folder": {
                OpenShell(GameDir(message));
                break;
            }
            case "projects":
            case "project-select":
            case "project-new":
            case "project-delete": {
                OnProjectMessage(type, message);
                break;
            }
            default: {
                OnTeamMessage(type, message);
                break;
            }
        }
    }

    // ---------- 터미널 ----------

    private void StartTerminal(short cols, short rows, bool continueLast)
    {
        _terminalStartedOnce = true;
        _lastTerminalSize = (cols, rows);
        _continueStartedUtc = continueLast ? DateTime.UtcNow : null;
        _terminalStartedUtc = DateTime.UtcNow;
        string engine = _terminal.Ai;
        string? workDirError = _terminal.CheckWorkDir();
        if (null != workDirError)
        {
            _continueStartedUtc = null;
            PostError(workDirError);
            Post(new { t = "term-exit", code = -1, engine });
            return;
        }

        try
        {
            _terminal.Start(cols, rows, continueLast);
            Post(new { t = "term-started", continueLast, engine });
        }
        catch (Exception ex)
        {
            // Claude Code / Codex 가 없거나 PATH 에 없음 — 화면 터미널 칸이 설치 안내를 띄움
            _continueStartedUtc = null;
            string configured = _config.ClaudeExe;
            if (AiEngines.Gpt == engine)
            {
                configured = _config.CodexExe;
            }
            else if (AiEngines.Gemini == engine)
            {
                configured = _config.AgyExe;
            }

            PostError($"{AiEngines.ToolName(engine)} 를 시작하지 못했습니다 ({configured}): {ex.Message}");
            Post(new { t = "term-exit", code = -1, missing = true, engine });
        }
    }

    // --continue 가 곧바로 끝남 = 이어 갈 대화 없음 → 새 대화로
    private void OnTerminalExited(int code)
    {
        bool continueFailed = null != _continueStartedUtc && 0 != code && DateTime.UtcNow - _continueStartedUtc.Value < ContinueFailWindow;
        _continueStartedUtc = null;
        if (continueFailed)
        {
            QueueTerminalOutput("\r\n\x1b[33m[이어 갈 대화가 없어 새 대화로 시작합니다]\x1b[0m\r\n");
            StartTerminal(_lastTerminalSize.Cols, _lastTerminalSize.Rows, continueLast: false);
            return;
        }

        // Codex 가 시작하자마자 오류로 끝남 = 설치가 깨졌거나 로그인 전일 수 있음 — 화면에 남은 오류 글 아래에 다음 할 일을 한 줄
        if (AiEngines.Gpt == _terminal.Ai && 0 != code && DateTime.UtcNow - _terminalStartedUtc < ContinueFailWindow)
        {
            QueueTerminalOutput("\r\n\x1b[33m[Codex 가 바로 끝났어요 — 위 글을 확인하세요. 'Missing optional dependency' 라면 PowerShell 에서 " + CodexLauncher.InstallLine + " --include=optional 로 다시 설치하고, 아니면 PowerShell 에서 codex 를 직접 한 번 실행해 로그인 상태를 확인해 보세요]\x1b[0m\r\n");
        }

        if (AiEngines.Gemini == _terminal.Ai && 0 != code && DateTime.UtcNow - _terminalStartedUtc < ContinueFailWindow)
        {
            QueueTerminalOutput("\r\n\x1b[33m[GEMINI(Antigravity) 가 바로 끝났어요 — 위 글을 확인하세요. PowerShell 에서 agy 를 직접 한 번 실행해 구글 계정 로그인을 끝내 보고, 그래도 안 되면 PowerShell 에서 " + AgyLauncher.InstallLine + " 로 다시 설치해 보세요]\x1b[0m\r\n");
        }

        Post(new { t = "term-exit", code, engine = _terminal.Ai });
    }

    // PTY 출력 스레드에서 호출됨
    private void QueueTerminalOutput(string text)
    {
        lock (_outputLock)
        {
            _pendingOutput.Append(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        Dispatcher.BeginInvoke(FlushTerminalOutput, DispatcherPriority.Normal);
    }

    private void FlushTerminalOutput()
    {
        string data;
        lock (_outputLock)
        {
            data = _pendingOutput.ToString();
            _pendingOutput.Clear();
            _flushScheduled = false;
        }

        if (data.Length > 0)
        {
            Post(new { t = "term-out", d = data });
        }
    }

    private static short ReadShort(JsonNode message, string key)
    {
        int value = message[key]?.GetValue<int>() ?? 80;
        return (short)Math.Clamp(value, 2, short.MaxValue);
    }

    // ---------- 게임 (Godot · 웹) ----------

    private bool CurrentIsWeb()
    {
        return true == _projects.Current?.IsWeb;
    }

    // 웹 게임 = 게임 폴더의 index.html (바로 그 폴더만). <title> 이 있으면 이름으로, 없으면 프로젝트 이름
    private object? FindWebGame(string gameRoot)
    {
        string index = Path.Combine(gameRoot, WebIndexFile);
        if (false == File.Exists(index))
        {
            return null;
        }

        Match title = HtmlTitlePattern.Match(File.ReadAllText(index));
        string name = title.Success && title.Groups[1].Value.Trim().Length > 0 ? title.Groups[1].Value.Trim() : (_projects.Current?.Name ?? Path.GetFileName(gameRoot));
        return new { path = gameRoot, name, folder = Path.GetRelativePath(_dataDir, gameRoot) };
    }

    // 게임 폴더를 127.0.0.1 서버로 내주고 기본 브라우저로 연다 (file:// 은 ES 모듈·fetch 가 막힘)
    private void PlayWeb(string gameRoot)
    {
        string url = _webServer.Serve(gameRoot, _projects.Current?.Id ?? "");
        OpenExternalUrl(url);
        if (_webServer.UsedFallbackPort)
        {
            PostError("이번엔 게임 저장이 따로 보일 수 있습니다 (늘 쓰던 포트가 사용 중이라 다른 포트로 열었습니다)");
        }
    }

    // 지금 프로젝트의 게임 폴더 안(자신 또는 아래 2단계까지)에서 project.godot 이 있는 폴더 = 게임. 다른 프로젝트·공용 폴더는 안 봄.
    // 웹 프로젝트면 게임 폴더의 index.html 하나
    private void PostGames()
    {
        List<object> games = new();
        string gameRoot = _terminal.GameDir;
        bool isWeb = CurrentIsWeb();
        if (isWeb && gameRoot.Length > 0 && Directory.Exists(gameRoot))
        {
            object? web = FindWebGame(gameRoot);
            if (null != web)
            {
                games.Add(web);
            }
        }
        else if (gameRoot.Length > 0 && Directory.Exists(gameRoot))
        {
            IEnumerable<string> dirs = File.Exists(Path.Combine(gameRoot, GodotProjectFile)) ? new[] { gameRoot } : FindGameDirs(gameRoot, depth: 2);
            foreach (string dir in dirs)
            {
                Match match = GodotNamePattern.Match(File.ReadAllText(Path.Combine(dir, GodotProjectFile)));
                games.Add(new { path = dir, name = match.Success ? match.Groups[1].Value : Path.GetFileName(dir), folder = Path.GetRelativePath(_dataDir, dir) });
            }
        }

        string engineName = isWeb ? "웹" : "Godot";
        string empty = 0 == gameRoot.Length ? "프로젝트 없음 — [새 프로젝트]" : (isWeb ? "아직 index.html 없음 — 오른쪽 AI 에게 기획부터 시켜 보세요" : "아직 Godot 프로젝트 없음 — 오른쪽 AI 에게 기획부터 시켜 보세요");
        string godotExe = _config.ResolveGodotExe();
        // 웹은 Godot 실행 파일이 없어도 버튼이 켜짐
        Post(new { t = "games", items = games, godot = godotExe.Length > 0 && File.Exists(godotExe), empty, engine = gameRoot.Length > 0 ? (isWeb ? Project.EngineWeb : Project.EngineGodot) : "", engineName });
    }

    private static IEnumerable<string> FindGameDirs(string root, int depth)
    {
        foreach (string dir in Directory.GetDirectories(root))
        {
            if (SkippedGameDirs.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, GodotProjectFile)))
            {
                yield return dir;
                continue;
            }

            if (depth > 1)
            {
                foreach (string child in FindGameDirs(dir, depth - 1))
                {
                    yield return child;
                }
            }
        }
    }

    // JS 가 보낸 경로는 지금 프로젝트의 게임 폴더 아래만 허용 (웹 = 게임 폴더 자체이고 index.html 이 있어야 함)
    private string GameDir(JsonNode message)
    {
        string dir = Path.GetFullPath(message["path"]?.GetValue<string>() ?? "");
        string gameRoot = _terminal.GameDir;
        string rootPrefix = gameRoot.Length > 0 ? Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar : "";
        bool insideProject = rootPrefix.Length > 0 && (dir + Path.DirectorySeparatorChar).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        string marker = CurrentIsWeb() ? WebIndexFile : GodotProjectFile;
        if (false == insideProject || false == File.Exists(Path.Combine(dir, marker)))
        {
            throw new InvalidOperationException($"게임 프로젝트가 아닙니다: {dir}");
        }

        return dir;
    }

    private void StartGodot(string projectDir, bool editor)
    {
        string exe = _config.ResolveGodotExe();
        if (0 == exe.Length || false == File.Exists(exe))
        {
            throw new InvalidOperationException("Godot 를 찾지 못했습니다 — AL-ZARD_GameMaker 설치 파일을 다시 실행하면 함께 들어옵니다.");
        }

        ProcessStartInfo info = new(exe) { WorkingDirectory = projectDir, UseShellExecute = false };
        if (editor)
        {
            info.ArgumentList.Add("--editor");
        }

        info.ArgumentList.Add("--path");
        info.ArgumentList.Add(projectDir);
        Process.Start(info);
    }
}
