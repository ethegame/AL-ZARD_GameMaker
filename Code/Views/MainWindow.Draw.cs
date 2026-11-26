using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameMaker.Services;

namespace GameMaker.Views;

// 그림 = GPT — 제어 창구 POST /draw (Services/ControlServer.cs) → Services/GptDrawer.cs 가 codex exec 로 그림 → 게임 폴더로 복사.
//   {"prompt": "…", "name": "파일 이름(확장자 없이)", "dir": "게임 폴더 기준 상대 경로, 기본 assets/sprites", "transparent": true(기본)} → 바로 {"run": id}.
//   GET /run/<id> 는 ComfyUI 실행과 같은 모양 ({state, error, files[절대 경로]}) — 실행 상태표(_runStates)를 같이 씀.
// 터미널 AI 는 ComfyUI /run 보다 이 창구를 먼저 부른다 (claude_context.md). GPT 를 못 쓰면 503 + 쉬운 이유 → AI 가 ComfyUI 로 예비.
// 그림 작업은 한 번에 하나 (ComfyUI 실행과도 겹치지 않게 — 리소스 칸 진행 줄이 하나라서). 결과는 리소스 칸 기록에도 남김 (outputs\<id>\, run.json Workflow = "GPT 그림")
public partial class MainWindow
{
    private const string GptDrawLabel = "GPT 그림";
    private const string DefaultDrawDir = "assets/sprites";
    private const int MaxDrawPromptChars = 4000;
    private const double MinDrawRemainingPercent = 10;

    private readonly GptDrawer _drawer = new();
    private CancellationTokenSource? _drawCancel; // null = 그리는 중 아님 (UI 스레드에서만 접근)

    // 고정 앞말 — 사장님 글 앞에 붙임
    private static string DrawPreamble(bool transparent)
    {
        string background = "배경은 단색으로 깔끔하게.";
        if (transparent)
        {
            background = "배경은 완전히 투명(알파 채널 있는 PNG)으로.";
        }

        return "2D 게임에 쓸 스프라이트 그림을 한 장 만든다. 캐릭터·물건이 잘리지 않게 가운데에 두고, 게임 화면에서 잘 보이게 또렷한 윤곽으로. "
            + background
            + " 그림 도구로 그림만 만들고 다른 파일은 만들거나 고치지 말 것. 끝나면 저장한 그림 경로만 한 줄로 답한다.\n\n그릴 것: ";
    }

    // 요청 스레드에서 불림 — 검사 중 Codex 실행 검사(몇 초)가 있어 UI 스레드를 잡지 않음
    private async Task<(int Status, object Body)> HandleDrawAsync(JsonNode? body)
    {
        string prompt = ReadText(body, "prompt").Trim();
        if (0 == prompt.Length)
        {
            return (400, new { error = "prompt 가 없습니다 — 무엇을 그릴지 적어 주세요." });
        }

        if (prompt.Length > MaxDrawPromptChars)
        {
            return (400, new { error = $"prompt 가 너무 깁니다 ({MaxDrawPromptChars}자 이하)." });
        }

        bool transparent = true;
        if (body?["transparent"] is JsonValue transparentValue && transparentValue.TryGetValue(out bool parsed))
        {
            transparent = parsed;
        }

        string codex = CodexLauncher.Find(AppConfig.ExpandPath(_config.CodexExe));
        if (0 == codex.Length)
        {
            return (503, new { error = "GPT(Codex)가 이 PC 에 없어서 GPT 로 그릴 수 없습니다. 그림 도구(ComfyUI)가 있으면 POST /run 으로 그리고, 없으면 사장님께 알려 주세요." });
        }

        bool healthy = await _drawer.CheckHealthAsync(codex);
        if (false == healthy)
        {
            return (503, new { error = "GPT(Codex)가 깔려 있지만 실행되지 않아서 GPT 로 그릴 수 없습니다 (다시 설치가 필요할 수 있음). 그림 도구(ComfyUI)가 있으면 POST /run 으로 그리세요." });
        }

        return await Dispatcher.InvokeAsync<(int, object)>(() => StartDraw(codex, prompt, ReadText(body, "name"), ReadText(body, "dir"), transparent));
    }

    // UI 스레드
    private (int Status, object Body) StartDraw(string codex, string prompt, string rawName, string rawDir, bool transparent)
    {
        string gameDir = _terminal.GameDir;
        if (0 == gameDir.Length || false == Directory.Exists(gameDir))
        {
            return (400, new { error = "게임 폴더가 없습니다 — 사장님이 맨 위 [새 프로젝트] 를 먼저 눌러야 합니다." });
        }

        (string? targetDir, string? dirError) = ResolveDrawDir(gameDir, rawDir);
        if (null == targetDir)
        {
            return (400, new { error = dirError });
        }

        string name = SafeDrawName(rawName);
        if (null != _drawCancel || null != _runCancel)
        {
            return (409, new { error = "다른 그림을 만드는 중입니다. 끝난 뒤 다시 요청하세요." });
        }

        double? left = MinRemaining(_codexSnapshot?.Windows ?? Array.Empty<UsageWindow>());
        if (null != left && left.Value < MinDrawRemainingPercent)
        {
            return (503, new { error = $"GPT 사용량이 거의 다 찼습니다 (남은 {left.Value:0}%). 그림 도구(ComfyUI)가 있으면 POST /run 으로 그리고, 없으면 다시 찰 때까지 기다려야 한다고 사장님께 알려 주세요." });
        }

        string runId = $"{DateTime.Now:yyyyMMdd_HHmmss}_GPT_{SafeName(name)}";
        CancellationTokenSource cancel = new();
        _drawCancel = cancel;
        _runStates[runId] = new RunState("running", null, new List<string>());
        Post(new { t = "run-start", run = runId, workflow = GptDrawLabel, external = true });
        Post(new { t = "progress", text = "GPT 가 그리는 중 (1~3분)", value = 0, max = 0 });
        string fullPrompt = "$imagegen " + DrawPreamble(transparent) + prompt;
        _ = DrawAsync(runId, codex, fullPrompt, prompt, targetDir, name, cancel);
        return (200, new { run = runId, note = "GPT 가 그리기 시작했습니다 (보통 1~3분, 최대 6분). 상태: GET /run/" + runId });
    }

    private async Task DrawAsync(string runId, string codex, string fullPrompt, string prompt, string targetDir, string name, CancellationTokenSource cancel)
    {
        DateTime started = DateTime.Now;
        try
        {
            GptDrawer.Result result = await Task.Run(() => GptDrawer.DrawAsync(codex, fullPrompt, cancel.Token));
            if (null != result.Error || 0 == result.Images.Count)
            {
                string error = result.Error ?? "그림이 나오지 않았어요";
                _runStates[runId] = new RunState("failed", error, new List<string>());
                Post(new { t = "run-fail", text = $"GPT 그림: {error}" });
                return;
            }

            // 게임 폴더로 복사 (있으면 덮지 않고 name-v2, -v3 …) + 리소스 칸 기록용 사본
            Directory.CreateDirectory(targetDir);
            string runDir = Path.Combine(_outputsDir, runId);
            Directory.CreateDirectory(runDir);
            List<string> saved = new();
            foreach (string image in result.Images)
            {
                string target = NextDrawPath(targetDir, name);
                File.Copy(image, target);
                saved.Add(target);
                File.Copy(image, UniquePath(runDir, Path.GetFileName(target)));
            }

            JsonObject values = new() { ["prompt"] = prompt, ["files"] = new JsonArray(saved.Select(path => (JsonNode)JsonValue.Create(path)!).ToArray()) };
            RunRecord record = new(runId, GptDrawLabel, started, (DateTime.Now - started).TotalSeconds, values);
            File.WriteAllText(Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(record, AppConfig.JsonOptions));
            _runStates[runId] = new RunState("done", null, saved);
            Post(new { t = "run-done", run = DescribeRun(runDir) });
        }
        catch (Exception ex)
        {
            _runStates[runId] = new RunState("failed", ex.Message, new List<string>());
            Post(new { t = "run-fail", text = $"GPT 그림: {ex.Message}" });
        }
        finally
        {
            cancel.Dispose();
            _drawCancel = null;
            ReadCodexUsage(force: true);   // 그림 한 장도 GPT 한도를 씀
        }
    }

    // dir = 게임 폴더 기준 상대 경로만 (절대 경로·.. 거부, 풀어 본 경로가 게임 폴더 안이어야 함)
    private static (string? Dir, string? Error) ResolveDrawDir(string gameDir, string rawDir)
    {
        string dir = rawDir.Trim().Replace('\\', '/').Trim('/');
        if (0 == dir.Length)
        {
            dir = DefaultDrawDir;
        }

        bool hasParent = dir.Split('/').Any(part => ".." == part.Trim());
        if (Path.IsPathRooted(dir) || dir.Contains(':') || hasParent)
        {
            return (null, "dir 는 게임 폴더 기준 상대 경로만 됩니다 (예: assets/sprites — 절대 경로·.. 은 안 됨).");
        }

        string full = Path.GetFullPath(Path.Combine(gameDir, dir.Replace('/', Path.DirectorySeparatorChar)));
        if (false == AppConfig.IsSameOrInside(full, gameDir))
        {
            return (null, "dir 가 게임 폴더 밖입니다.");
        }

        return (full, null);
    }

    private static string SafeDrawName(string rawName)
    {
        string name = Path.GetFileNameWithoutExtension(rawName.Trim().Replace('/', '_').Replace('\\', '_'));
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (0 == name.Length)
        {
            name = $"sprite_{DateTime.Now:HHmmss}";
        }

        return name;
    }

    private static string NextDrawPath(string dir, string name)
    {
        string path = Path.Combine(dir, name + ".png");
        for (int i = 2; File.Exists(path); ++i)
        {
            path = Path.Combine(dir, $"{name}-v{i}.png");
        }

        return path;
    }

    private static string ReadText(JsonNode? body, string key)
    {
        if (body?[key] is JsonValue value && value.TryGetValue(out string? text))
        {
            return text ?? "";
        }

        return "";
    }
}
