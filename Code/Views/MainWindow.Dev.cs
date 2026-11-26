using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameMaker.Core;
using GameMaker.Services;

namespace GameMaker.Views;

// 개발 쪽: 메시지 나눔 (터미널·엔진 = TerminalSession, 프로젝트 = MainWindow.Projects.cs, 팀 = MainWindow.Teams.cs) + 게임(Godot 프로젝트 · 웹 게임) 실행.
// 앱 자체 업데이트는 새 버전 설치 파일로 한다 (설치 = 업데이트, 데이터 폴더는 그대로) — 앱 안에서 다시 빌드·교체하는 길은 없다.
public partial class MainWindow
{
    private const string GodotProjectFile = "project.godot";
    private static readonly string[] SkippedGameDirs = { "outputs", "workflows", "picked", "logs", "requests", ".godot" };
    private static readonly Regex GodotNamePattern = new(@"^config/name\s*=\s*""(.*)""", RegexOptions.Multiline);
    private const string WebIndexFile = "index.html";
    private static readonly Regex HtmlTitlePattern = new(@"<title>([^<]*)</title>", RegexOptions.IgnoreCase);

    private readonly ClaudeTerminal _terminal;  // 띄우기는 TerminalSession 이, 게임 폴더·팀 문서 경로는 여기서도 읽음
    private readonly WebGameServer _webServer = new(); // 웹 프로젝트 [▶ 플레이] 용 (누를 때 켬, 앱 종료 때 끔)
    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _workflowDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private FileSystemWatcher? _workflowWatcher;

    private void InitializeDev()
    {
        InitializeProjects();

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
        if (_session.OnMessage(type, message))
        {
            return;
        }

        switch (type)
        {
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
