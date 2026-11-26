using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GameMaker.Core;
using GameMaker.Services;

namespace GameMaker.Views;

// 오른쪽 AI 터미널 한 칸 (화면 = wwwroot/terminal.js, 띄우기 = Services/ClaudeTerminal.cs) — 띄우기·이어 하기·끝남 처리·출력 묶기 + 엔진 고르기.
// 엔진 고르기 [자동][CLAUDE][GPT][GEMINI]: 고른 값(자동 포함)은 %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json 의 AiEngine 에 남아 다음에 켜도 그대로.
//   바꾸면 터미널을 그 엔진으로 다시 띄운다 (확인창은 화면이 한 번 물음 — 진행 중인 대화가 끊기므로). 엔진마다 지난 대화가 따로라 다시 돌아오면 [이어 하기] 로 이어진다.
//   자동 = 새 세션을 띄울 때마다 실제 엔진 하나를 고름 (허브 앱 허브 AI 의 자동과 같은 규칙): Claude → GPT → GEMINI 순서로 깔려 있고
//   남은 % 가 10% 이상(모르면 씀)인 첫 엔진, 다 바닥이면 깔린 것 중 가장 많이 남은 것. 이어 하기는 고르지 않고 지금 엔진 그대로.
// 11/26 MainWindow partial(Dev.cs 터미널 절 + Ai.cs) → 이 클래스: 엔진 바꾸기·프로젝트 바꾸기가 터미널 상태를 각자 만지던 것을 Restart 하나로
public sealed class TerminalSession
{
    private const double MinAutoRemainingPercent = 10;
    private static readonly TimeSpan ContinueFailWindow = TimeSpan.FromSeconds(10);

    private readonly HostWindow _host;
    private readonly AppConfig _config;
    private readonly ClaudeTerminal _terminal;
    private readonly UsageMonitor _usage;
    private readonly Action<object> _post;
    private bool _startedOnce;
    private DateTime? _continueStartedUtc;            // --continue 로 띄운 시각 (곧바로 끝나면 새 대화로 다시)
    private DateTime _startedUtc = DateTime.MinValue; // 터미널을 띄운 시각 (Codex·agy 가 바로 끝나면 안내를 덧붙임)
    private (short Cols, short Rows) _lastSize = (80, 24);

    // 터미널 출력 합치기: PTY read 마다 보내지 않고 UI 틱 단위로 묶어서 보냄
    private readonly object _outputLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private bool _flushScheduled;

    public TerminalSession(HostWindow host, AppConfig config, ClaudeTerminal terminal, UsageMonitor usage, Action<object> post)
    {
        _host = host;
        _config = config;
        _terminal = terminal;
        _usage = usage;
        _post = post;
        _terminal.Output += QueueOutput;
        _terminal.Exited += code => _host.Dispatcher.BeginInvoke(() => OnExited(code));
    }

    // 터미널 메시지면 처리하고 true
    public bool OnMessage(string type, JsonNode message)
    {
        switch (type)
        {
            case "term-created": {
                // 앱 시작 후 첫 터미널 = 지난 대화 이어 하기
                if (false == _terminal.IsRunning)
                {
                    ChooseEngineForNewSession();
                    Start(ReadShort(message, "cols"), ReadShort(message, "rows"), false == _startedOnce && _terminal.HasPreviousConversation());
                }

                return true;
            }
            case "ai-engine-set": {
                SetEngine(message["engine"]?.GetValue<string>());
                return true;
            }
            case "term-start": {
                // [새 세션] = 자동이면 엔진을 다시 고름. 이어 하기는 지금 엔진 그대로 (그 엔진의 대화를 잇는 것이라)
                bool continueLast = true == message["continue"]?.GetValue<bool>();
                if (false == continueLast)
                {
                    ChooseEngineForNewSession();
                }

                Start(ReadShort(message, "cols"), ReadShort(message, "rows"), continueLast);
                return true;
            }
            case "term-in": {
                _terminal.Write(message["d"]?.GetValue<string>() ?? "");
                return true;
            }
            case "term-bin": {
                // xterm onBinary: 각 문자가 0~255 바이트 (마우스 리포트 등)
                _terminal.Write(Encoding.Latin1.GetBytes(message["d"]?.GetValue<string>() ?? ""));
                return true;
            }
            case "term-resize": {
                _terminal.Resize(ReadShort(message, "cols"), ReadShort(message, "rows"));
                return true;
            }
            case "term-copy": {
                string text = message["d"]?.GetValue<string>() ?? "";
                if (text.Length > 0)
                {
                    Clipboard.SetText(text);
                }

                return true;
            }
            case "term-paste": {
                PasteClipboard();
                return true;
            }
        }

        return false;
    }

    // 엔진·프로젝트를 바꿨을 때: 한 번이라도 띄운 터미널이면 지금 엔진·게임 폴더의 지난 대화로 다시 띄움 (없으면 새 대화).
    // 터미널 작업 폴더 = 프로젝트 게임 폴더라 --continue 는 그 프로젝트 대화만 잇는다
    public void Restart()
    {
        if (false == _startedOnce)
        {
            return;
        }

        _terminal.Stop();
        _post(new { t = "term-reset" });
        Start(_lastSize.Cols, _lastSize.Rows, continueLast: _terminal.HasPreviousConversation());
    }

    // engine = 실제로 도는 엔진, choice = 고른 값 (자동이면 화면이 [자동 · GPT] 처럼 보임)
    public void PostEngine()
    {
        _post(new { t = "ai-engine", engine = _terminal.Ai, choice = _config.AiEngine });
    }

    // 텍스트가 있으면 화면이 붙여 넣게 돌려줌. 텍스트 없이 이미지만 있으면 엔진의 이미지 붙이기 키를 대신 보냄:
    // Claude Code = Alt+V(ESC v, Windows — 맥은 terminal.js 가 Ctrl+V 를 그대로 보냄), Codex = Ctrl+V
    private void PasteClipboard()
    {
        if (Clipboard.ContainsText())
        {
            _post(new { t = "term-paste", d = Clipboard.GetText() });
            return;
        }

        if (Clipboard.ContainsImage())
        {
            _terminal.Write(AiEngines.Gpt == _terminal.Ai ? "\x16" : "\x1bv");
        }
    }

    private void Start(short cols, short rows, bool continueLast)
    {
        _startedOnce = true;
        _lastSize = (cols, rows);
        _continueStartedUtc = continueLast ? DateTime.UtcNow : null;
        _startedUtc = DateTime.UtcNow;
        string engine = _terminal.Ai;
        string? workDirError = _terminal.CheckWorkDir();
        if (null != workDirError)
        {
            _continueStartedUtc = null;
            PostError(workDirError);
            _post(new { t = "term-exit", code = -1, engine });
            return;
        }

        try
        {
            _terminal.Start(cols, rows, continueLast);
            _post(new { t = "term-started", continueLast, engine });
        }
        catch (Exception ex)
        {
            // Claude Code / Codex / agy 가 없거나 PATH 에 없음 — 화면 터미널 칸이 설치 안내를 띄움
            _continueStartedUtc = null;
            ReportMissingEngine(engine, ex);
        }
    }

    private void ReportMissingEngine(string engine, Exception ex)
    {
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
        _post(new { t = "term-exit", code = -1, missing = true, engine });
    }

    // --continue 가 곧바로 끝남 = 이어 갈 대화 없음 → 새 대화로
    private void OnExited(int code)
    {
        bool continueFailed = null != _continueStartedUtc && 0 != code && DateTime.UtcNow - _continueStartedUtc.Value < ContinueFailWindow;
        _continueStartedUtc = null;
        if (continueFailed)
        {
            QueueOutput("\r\n\x1b[33m[이어 갈 대화가 없어 새 대화로 시작합니다]\x1b[0m\r\n");
            Start(_lastSize.Cols, _lastSize.Rows, continueLast: false);
            return;
        }

        // Codex 가 시작하자마자 오류로 끝남 = 설치가 깨졌거나 로그인 전일 수 있음 — 화면에 남은 오류 글 아래에 다음 할 일을 한 줄
        bool endedRightAway = 0 != code && DateTime.UtcNow - _startedUtc < ContinueFailWindow;
        if (AiEngines.Gpt == _terminal.Ai && endedRightAway)
        {
            QueueOutput("\r\n\x1b[33m[Codex 가 바로 끝났어요 — 위 글을 확인하세요. 'Missing optional dependency' 라면 PowerShell 에서 " + CodexLauncher.InstallLine + " --include=optional 로 다시 설치하고, 아니면 PowerShell 에서 codex 를 직접 한 번 실행해 로그인 상태를 확인해 보세요]\x1b[0m\r\n");
        }

        if (AiEngines.Gemini == _terminal.Ai && endedRightAway)
        {
            QueueOutput("\r\n\x1b[33m[GEMINI(Antigravity) 가 바로 끝났어요 — 위 글을 확인하세요. PowerShell 에서 agy 를 직접 한 번 실행해 구글 계정 로그인을 끝내 보고, 그래도 안 되면 PowerShell 에서 " + AgyLauncher.InstallLine + " 로 다시 설치해 보세요]\x1b[0m\r\n");
        }

        _post(new { t = "term-exit", code, engine = _terminal.Ai });
    }

    // PTY 출력 스레드에서 호출됨
    private void QueueOutput(string text)
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

        _host.Dispatcher.BeginInvoke(FlushOutput, DispatcherPriority.Normal);
    }

    private void FlushOutput()
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
            _post(new { t = "term-out", d = data });
        }
    }

    private static short ReadShort(JsonNode message, string key)
    {
        int value = message[key]?.GetValue<int>() ?? 80;
        return (short)Math.Clamp(value, 2, short.MaxValue);
    }

    // ---------- 엔진 고르기 ----------

    private void SetEngine(string? value)
    {
        string choice = AiEngines.NormalizeChoice(value);
        if (choice == _config.AiEngine)
        {
            PostEngine();
            return;
        }

        try
        {
            AppConfig.SaveUserSetting("AiEngine", choice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PostError($"고른 엔진을 저장하지 못했습니다 — 이번 실행 동안만 적용돼요 ({ex.Message})");
        }

        _config.AiEngine = choice;
        string engine = choice;
        if (AiEngines.Auto == choice)
        {
            engine = PickAutoEngine();
        }

        // 자동으로 바꿨는데 고른 엔진이 지금 도는 것과 같으면 대화를 끊지 않는다
        bool sameEngine = engine == _terminal.Ai;
        ApplyEngine(engine);
        if (sameEngine && _terminal.IsRunning)
        {
            return;
        }

        Restart();
    }

    // 새 세션을 띄우기 직전: 자동이면 지금 형편으로 실제 엔진을 다시 고름
    private void ChooseEngineForNewSession()
    {
        if (AiEngines.Auto != _config.AiEngine)
        {
            _terminal.Ai = AiEngines.Normalize(_config.AiEngine);
            return;
        }

        string engine = PickAutoEngine();
        if (engine == _terminal.Ai)
        {
            return;
        }

        ApplyEngine(engine);
    }

    // 도는 엔진을 바꾸고 화면 탭·미터를 그 엔진으로
    private void ApplyEngine(string engine)
    {
        _terminal.Ai = engine;
        PostEngine();
        _usage.Refresh(engine);
        _usage.Post();
    }

    private string PickAutoEngine()
    {
        string best = AiEngines.Claude;
        double bestLeft = -1;
        foreach (string engine in AiEngines.AutoChain)
        {
            if (false == IsInstalled(engine))
            {
                continue;
            }

            double? left = _usage.RemainingOf(engine);
            if (null == left || left.Value >= MinAutoRemainingPercent)
            {
                return engine;
            }

            if (left.Value > bestLeft)
            {
                best = engine;
                bestLeft = left.Value;
            }
        }

        return best;
    }

    // Claude 는 늘 있다고 봄 (없으면 터미널 칸이 설치 안내를 띄움 — 자동이 다른 엔진으로 숨기지 않게)
    private bool IsInstalled(string engine)
    {
        if (AiEngines.Gpt == engine)
        {
            return CodexLauncher.Find(AppConfig.ExpandPath(_config.CodexExe)).Length > 0;
        }

        if (AiEngines.Gemini == engine)
        {
            return AgyLauncher.Find(AppConfig.ExpandPath(_config.AgyExe)).Length > 0;
        }

        return true;
    }

    private void PostError(string text)
    {
        _post(new { t = "error", text });
    }
}
