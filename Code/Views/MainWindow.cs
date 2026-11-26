using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameMaker.Core;
using GameMaker.Services;

namespace GameMaker.Views;

// 화면 = wwwroot (Photino 창 — Windows WebView2 · 맥 WKWebView. 창·파일 공급 = Program.cs · Core/HostWindow.cs · Core/WebFiles.cs).
// C# 은 ComfyUI 서버·API·파일 접근만 맡는다.
// 터미널(Claude Code)·게임(Godot) = MainWindow.Dev.cs, 4팀 탭·팀 문서 = MainWindow.Teams.cs, 프로젝트 = MainWindow.Projects.cs
// JS → C# : Host.post({t, ...})  (wwwroot/bridge.js)
// C# → JS : Post({t, ...}) → JSON 글자 하나 → Host.on
// 2026-11-20 WPF Window → 일반 클래스. 이름은 MainWindow 그대로 (기능 코드 변경을 줄이려고)
public partial class MainWindow : IDisposable
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web); // record 속성도 camelCase 로

    private readonly HostWindow _host;
    private readonly AppConfig _config;
    private readonly string _dataDir;
    private readonly string _outputsDir;
    private readonly string _pickedDir;
    private readonly ComfyServer _server;
    private readonly ComfyClient _client;
    private readonly WorkflowStore _workflows;
    private readonly ControlServer _control;
    private readonly string? _controlError;
    private readonly Dictionary<string, RunState> _runStates = new(); // UI 스레드에서만 접근
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool? _lastAlive;
    private bool _starting;
    private string? _selectedWorkflow; // 화면에서 지금 고른 워크플로 (GET /status — 터미널 claude 의 "이거 고쳐줘" 대상)
    private CancellationTokenSource? _runCancel;

    public MainWindow(HostWindow host)
    {
        _host = host;
        _config = AppConfig.Load();
        _dataDir = _config.ResolveDataDir();
        _outputsDir = Path.Combine(_dataDir, "outputs");
        _pickedDir = Path.Combine(_dataDir, "picked");
        Directory.CreateDirectory(_outputsDir);
        Directory.CreateDirectory(_pickedDir);
        _server = new ComfyServer(_config, _dataDir);
        _client = new ComfyClient(_config.ComfyUrl);
        _workflows = new WorkflowStore(_dataDir);
        _host.Files.DataRoot = _dataDir; // 화면의 <origin>/_data/… = DataDir (결과·선택한 이미지 미리보기)
        _terminal = new ClaudeTerminal(_config, _dataDir);
        InitializeDev();

        // 포트 0 = 빈 포트 자동. 실제 포트를 설정에 적어 두면 터미널 context 의 {ControlPort} 가 그 값으로 채워짐
        _control = new ControlServer(_config.ControlPort, HandleControlAsync);
        _controlError = _control.Start();
        _config.ControlPort = _control.Port;

        InitializeUpdate();
        InitializeUsage();
        InitializeGpu();

        _host.MessageReceived += OnHostMessage;
        _statusTimer.Tick += async (_, _) => await PushStatusAsync(force: false);
    }

    private Dispatcher Dispatcher => _host.Dispatcher;

    // 창이 닫힐 때 (Program 의 WindowClosing). 예전 WPF Closed 와 같은 순서
    public void Dispose()
    {
        _statusTimer.Stop();
        _control.Dispose();
        DisposeDev();
        DisposeUpdate();
        DisposeUsage();
        DisposeGpu();
        DisposeComfyInstall();
        _runCancel?.Cancel();
        _drawCancel?.Cancel();
        _server.Dispose();
        _client.Dispose();
    }

    // HostWindow 가 UI 줄에 세운 뒤 부름 (UI 스레드)
    private void OnHostMessage(string json)
    {
        _ = OnMessageAsync(json);
    }

    private async Task OnMessageAsync(string json)
    {
        JsonNode? message = JsonNode.Parse(json);
        string type = message?["t"]?.GetValue<string>() ?? "";
        try
        {
            switch (type)
            {
                case "ready": {
                    PostWorkflowList(null);
                    PostHistory();
                    PostProjects();
                    PostGames();
                    PostTeams();
                    _updater?.PostState();
                    PostAiEngine();
                    PostUsage();
                    Post(new { t = "folders", data = _dataDir, workflows = _workflows.Folder });
                    // 제어 포트 실패 + 시작할 때 기본값으로 대신한 것(설정 깨짐·데이터 폴더 대체). 알림 칸이 하나라 한 번에 묶어 보냄
                    List<string> notices = new(_config.Notices);
                    if (null != _controlError)
                    {
                        notices.Add(_controlError);
                    }

                    if (notices.Count > 0)
                    {
                        PostError(string.Join(" / ", notices));
                    }

                    await PushStatusAsync(force: true);
                    _statusTimer.Start();
                    // 그림 도구(ComfyUI)가 설정돼 있을 때만 자동 시작 — 없으면 화면이 [설치 방법] 을 보여 줌
                    if (false == _lastAlive && _server.IsInstalled)
                    {
                        await StartServerAsync();
                    }

                    break;
                }
                case "comfy-pick-root": {
                    string[] picked = _host.ShowOpenFolder("ComfyUI 포터블 폴더 고르기 (run_nvidia_gpu.bat 이 있는 폴더)");
                    if (picked.Length > 0)
                    {
                        Post(new { t = "comfy-root-picked", path = picked[0] });
                    }

                    break;
                }
                case "comfy-set-root": {
                    await SetComfyRootAsync(message!["path"]?.GetValue<string>() ?? "", null);
                    break;
                }
                case "comfy-install-check": {
                    await CheckComfyInstallAsync();
                    break;
                }
                case "comfy-install": {
                    await InstallComfyAsync();
                    break;
                }
                case "comfy-install-cancel": {
                    _comfyInstallCancel?.Cancel();
                    break;
                }
                case "server-start": {
                    await StartServerAsync();
                    break;
                }
                case "server-stop": {
                    _server.Stop();
                    await PushStatusAsync(force: true);
                    break;
                }
                case "open-comfy": {
                    OpenShell(_config.ComfyUrl);
                    break;
                }
                case "open-folder": {
                    OpenFolder(message!["which"]?.GetValue<string>() ?? "", message["run"]?.GetValue<string>());
                    break;
                }
                case "wf-list": {
                    PostWorkflowList(null);
                    break;
                }
                case "wf-load": {
                    LoadWorkflow(message!["name"]!.GetValue<string>());
                    break;
                }
                case "wf-import": {
                    ImportWorkflow();
                    break;
                }
                case "pick-image": {
                    await PickImageAsync(message!["node"]!.GetValue<string>(), message["key"]!.GetValue<string>());
                    break;
                }
                case "run": {
                    StartRun(message!["name"]!.GetValue<string>(), message["values"] as JsonObject ?? new JsonObject());
                    break;
                }
                case "cancel": {
                    // 리소스 칸 [중단] — GPT 그림이 도는 중이면 그것을, 아니면 ComfyUI 를 멈춤
                    if (null != _drawCancel)
                    {
                        _drawCancel.Cancel();
                        break;
                    }

                    await _client.InterruptAsync();
                    break;
                }
                case "update-start": {
                    // update.js 가 확인을 받은 뒤 보냄
                    if (null != _updater)
                    {
                        await _updater.StartAsync();
                    }

                    break;
                }
                case "openurl": {
                    // bridge.js: 화면 밖 링크 = 기본 브라우저 (http·https 만)
                    OpenExternalUrl(message!["d"]?.GetValue<string>() ?? "");
                    break;
                }
                case "jserror": {
                    _host.Log("화면 스크립트 오류: " + (message!["d"]?.GetValue<string>() ?? ""));
                    break;
                }
                default: {
                    OnDevMessage(type, message!);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            PostError(ex.Message);
        }
    }

    private async Task StartServerAsync()
    {
        if (_starting)
        {
            return;
        }

        _starting = true;
        Post(new { t = "status", state = "starting", url = _config.ComfyUrl });
        try
        {
            string? error = await _server.StartAsync(ServerStartTimeout);
            if (null != error)
            {
                PostError(error);
            }
        }
        finally
        {
            _starting = false;
        }

        await PushStatusAsync(force: true);
    }

    private async Task PushStatusAsync(bool force)
    {
        if (_starting)
        {
            return;
        }

        bool alive = await _server.IsAliveAsync();
        if (false == force && alive == _lastAlive)
        {
            return;
        }

        _lastAlive = alive;
        // installed = 설정된 ComfyUI 폴더가 있음. 꺼져 있고 installed 도 아니면 화면이 "그림 도구 없음 — [설치 방법]"
        Post(new { t = "status", state = alive ? "on" : "off", url = _config.ComfyUrl, owned = _server.IsOwned, installed = _server.IsInstalled });
    }

    // 화면 [설치 방법] 안내에서 고른 ComfyUI 폴더(또는 [설치하기] 가 깐 폴더)를 저장하고 바로 서버를 띄움 (모양이 아니면 저장하지 않음).
    // installedText = 자동 설치 끝 문구 (null = 손으로 고른 폴더 → 화면이 잠시 뒤 창을 닫음)
    private async Task SetComfyRootAsync(string path, string? installedText)
    {
        string root = AppConfig.ExpandPath(path);
        root = root.Length > 0 ? Path.GetFullPath(root) : "";
        if (false == ComfyServer.IsValidRoot(root))
        {
            Post(new { t = "comfy-root-result", ok = false, text = "이 폴더에서 ComfyUI 를 찾지 못했습니다. 압축을 푼 폴더(안에 python_embeded 와 ComfyUI 폴더가 있는 곳)를 골라 주세요." });
            return;
        }

        AppConfig.SaveUserSetting(nameof(AppConfig.ComfyRoot), root);
        _config.ComfyRoot = root;
        Post(new { t = "comfy-root-result", ok = true, stay = null != installedText, text = installedText ?? "저장했습니다. 그림 도구를 켜는 중입니다 (처음엔 몇 분 걸릴 수 있어요)." });
        await PushStatusAsync(force: true);
        await StartServerAsync();
    }

    private void PostWorkflowList(string? select)
    {
        Post(new { t = "wf-list", items = _workflows.List(), select });
    }

    private void LoadWorkflow(string name)
    {
        JsonObject prompt = _workflows.Load(name);
        _selectedWorkflow = name;
        Post(new { t = "wf", name, nodes = WorkflowStore.Describe(prompt) });
    }

    private void ImportWorkflow()
    {
        string[] picked = _host.ShowOpenFile("ComfyUI API 형식 워크플로 (Export (API))", true, "Workflow JSON", new[] { ".json" });
        if (0 == picked.Length)
        {
            return;
        }

        string? last = null;
        foreach (string path in picked)
        {
            try
            {
                last = _workflows.Import(path);
            }
            catch (Exception ex)
            {
                PostError($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        PostWorkflowList(last);
        if (null != last)
        {
            LoadWorkflow(last);
        }
    }

    private async Task PickImageAsync(string node, string key)
    {
        string[] picked = _host.ShowOpenFile("입력 이미지", false, "Image", ImageExtensions);
        if (0 == picked.Length)
        {
            return;
        }

        string fileName = picked[0];
        string value = await _client.UploadImageAsync(fileName);
        // 미리보기용 사본 (ComfyUI /view 를 페이지에서 직접 부르지 않으려고 — 앱 화면 주소 ↔ http 서버)
        string copyName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(fileName)}";
        File.Copy(fileName, Path.Combine(_pickedDir, copyName), overwrite: true);
        Post(new { t = "image-picked", node, key, value, url = FileUrl(Path.Combine(_pickedDir, copyName)) });
    }

    // 화면 [생성] 과 채팅(ControlServer POST /run) 공용. 실행은 뒤에서 진행, runId 바로 반환. UI 스레드에서 호출
    private string StartRun(string name, JsonObject values)
    {
        if (null != _runCancel || null != _drawCancel)
        {
            throw new InvalidOperationException("이미 실행 중입니다.");
        }

        JsonObject prompt = WorkflowStore.Apply(_workflows.Load(name), values);
        string runId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeName(name)}";
        _runCancel = new CancellationTokenSource();
        _runStates[runId] = new RunState("running", null, new List<string>());
        _ = RunAsync(runId, name, prompt, values, _runCancel);
        return runId;
    }

    private async Task RunAsync(string runId, string name, JsonObject prompt, JsonObject values, CancellationTokenSource cancel)
    {
        string runDir = Path.Combine(_outputsDir, runId);
        DateTime started = DateTime.Now;
        Post(new { t = "run-start", run = runId, workflow = name });
        try
        {
            List<ComfyClient.OutputFile> outputs = await _client.RunAsync(prompt, progress =>
                Dispatcher.BeginInvoke(() => Post(new { t = "progress", text = progress.Text, value = progress.Value, max = progress.Max })),
                cancel.Token);

            Directory.CreateDirectory(runDir);
            List<string> saved = new();
            foreach (ComfyClient.OutputFile file in outputs)
            {
                string path = UniquePath(runDir, file.FileName);
                await _client.DownloadAsync(file, path);
                saved.Add(path);
            }

            RunRecord record = new(runId, name, started, (DateTime.Now - started).TotalSeconds, values);
            File.WriteAllText(Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(record, AppConfig.JsonOptions));
            _runStates[runId] = new RunState("done", null, saved);
            Post(new { t = "run-done", run = DescribeRun(runDir) });
        }
        catch (OperationCanceledException)
        {
            _runStates[runId] = new RunState("failed", "중단됨", new List<string>());
            Post(new { t = "run-fail", text = "중단됨" });
        }
        catch (Exception ex)
        {
            _runStates[runId] = new RunState("failed", ex.Message, new List<string>());
            Post(new { t = "run-fail", text = ex.Message });
        }
        finally
        {
            cancel.Dispose();
            _runCancel = null;
        }
    }

    private sealed record RunState(string State, string? Error, List<string> Files);

    // ControlServer 요청 처리 (요청 스레드 → UI 스레드)
    private async Task<(int Status, object Body)> HandleControlAsync(string method, string path, JsonNode? body)
    {
        // 그림 = GPT (MainWindow.Draw.cs) — Codex 실행 검사가 몇 초 걸릴 수 있어 UI 스레드 밖에서 시작
        if ("POST" == method && "/draw" == path)
        {
            return await HandleDrawAsync(body);
        }

        return await Dispatcher.InvokeAsync<(int, object)>(() =>
        {
            if ("POST" == method && "/run" == path)
            {
                string workflow = body?["workflow"]?.GetValue<string>() ?? throw new InvalidOperationException("workflow 가 없습니다.");
                if (false == _workflows.List().Contains(workflow))
                {
                    return (404, new { error = $"워크플로 없음: {workflow}", workflows = _workflows.List() });
                }

                if (null != _runCancel || null != _drawCancel)
                {
                    return (409, new { error = "이미 실행 중입니다. 끝난 뒤 다시 요청하세요." });
                }

                if (true != _lastAlive)
                {
                    string why = _server.IsInstalled
                        ? "ComfyUI 서버가 꺼져 있습니다. 리소스 칸 [작업대] 의 [시작] 을 눌러 달라고 사용자에게 안내하세요."
                        : "그림 도구(ComfyUI)가 이 PC 에 없습니다. 리소스 칸 [설치 방법] 창의 [설치하기] 를 누르면 자동으로 깔린다고 사용자에게 안내하세요.";
                    return (503, new { error = why });
                }

                JsonObject values = body["values"]?.DeepClone() as JsonObject ?? new JsonObject();
                string runId = StartRun(workflow, values);
                return (200, new { run = runId, note = "실행 시작. 진행률과 결과는 앱 화면에 표시됩니다. 상태: GET /run/" + runId });
            }

            if ("GET" == method && path.StartsWith("/run/", StringComparison.Ordinal))
            {
                string runId = Uri.UnescapeDataString(path["/run/".Length..]);
                return _runStates.TryGetValue(runId, out RunState? state)
                    ? (200, new { state = state.State, error = state.Error, files = state.Files })
                    : (404, new { error = $"실행 기록 없음: {runId}" });
            }

            if ("GET" == method && "/status" == path)
            {
                return (200, new { server = true == _lastAlive, busy = null != _runCancel || null != _drawCancel, selected = _selectedWorkflow, workflows = _workflows.List() });
            }

            return (404, new { error = "POST /draw, POST /run, GET /run/<id>, GET /status 만 있습니다." });
        });
    }

    private sealed record RunRecord(string Id, string Workflow, DateTime Started, double Seconds, JsonObject Values);

    private void PostHistory()
    {
        List<object> runs = Directory.GetDirectories(_outputsDir)
            .OrderByDescending(dir => dir, StringComparer.Ordinal)
            .Select(DescribeRun)
            .ToList();
        Post(new { t = "history", runs });
    }

    private object DescribeRun(string runDir)
    {
        string id = Path.GetFileName(runDir);
        string workflow = "";
        double seconds = 0;
        string recordPath = Path.Combine(runDir, "run.json");
        if (File.Exists(recordPath))
        {
            JsonNode? record = JsonNode.Parse(File.ReadAllText(recordPath));
            workflow = record?["Workflow"]?.GetValue<string>() ?? "";
            seconds = record?["Seconds"]?.GetValue<double>() ?? 0;
        }

        var files = Directory.GetFiles(runDir)
            .Where(path => false == path.EndsWith("run.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { name = Path.GetFileName(path), url = FileUrl(path), ext = Path.GetExtension(path).ToLowerInvariant() })
            .ToList();
        return new { id, workflow, seconds, files };
    }

    private void OpenFolder(string which, string? run)
    {
        string path = which switch
        {
            "workflows" => _workflows.Folder,
            "log" => _server.LogPath,
            "run" when null != run => Path.Combine(_outputsDir, Path.GetFileName(run)),
            _ => _outputsDir,
        };
        if (File.Exists(path))
        {
            DesktopShell.Reveal(path);
            return;
        }

        OpenShell(path);
    }

    // DataDir 아래 파일 → 화면 주소 (<origin>/_data/<상대 경로>, Core/WebFiles.cs 가 공급)
    private string FileUrl(string path)
    {
        string relative = Path.GetRelativePath(_dataDir, path).Replace('\\', '/');
        return $"{_host.Origin}/{WebFiles.DataPrefix}/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static string UniquePath(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        for (int i = 1; File.Exists(path); ++i)
        {
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)}_{i}{Path.GetExtension(fileName)}");
        }

        return path;
    }

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || ' ' == c ? '_' : c).ToArray());
    }

    private static void OpenShell(string target)
    {
        DesktopShell.Open(target);
    }

    private static void OpenExternalUrl(string url)
    {
        bool isWeb = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (Uri.UriSchemeHttp == uri.Scheme || Uri.UriSchemeHttps == uri.Scheme);
        if (isWeb)
        {
            DesktopShell.Open(url);
        }
    }

    private void PostError(string text)
    {
        Post(new { t = "error", text });
    }

    private void Post(object message)
    {
        _host.Send(JsonSerializer.Serialize(message, WebJson));
    }
}
