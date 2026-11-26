using System.IO;
using GameMaker.Services;

namespace GameMaker.Views;

// 오른쪽 AI 칸 엔진 고르기 [자동][CLAUDE][GPT][GEMINI] — 연결만 (화면 = wwwroot/terminal.js, 터미널 띄우기 = Services/ClaudeTerminal.cs).
// 고른 값(자동 포함)은 %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json 의 AiEngine 에 남아 다음에 켜도 그대로. 바꾸면 터미널을 그 엔진으로 다시 띄운다
// (확인창은 화면이 한 번 물음 — 진행 중인 대화가 끊기므로). 엔진마다 지난 대화가 따로라 다시 돌아오면 [이어 하기] 로 이어진다.
// 자동 = 새 세션을 띄울 때마다 실제 엔진 하나를 고름 (허브 앱 허브 AI 의 자동과 같은 규칙): Claude → GPT → GEMINI 순서로 깔려 있고
//   남은 % 가 10% 이상(모르면 씀)인 첫 엔진, 다 바닥이면 깔린 것 중 가장 많이 남은 것. 이어 하기는 고르지 않고 지금 엔진 그대로
public partial class MainWindow
{
    private const double MinAutoRemainingPercent = 10;

    // engine = 실제로 도는 엔진, choice = 고른 값 (자동이면 화면이 [자동 · GPT] 처럼 보임)
    private void PostAiEngine()
    {
        Post(new { t = "ai-engine", engine = _terminal.Ai, choice = _config.AiEngine });
    }

    private void SetAiEngine(string? value)
    {
        string choice = AiEngines.NormalizeChoice(value);
        if (choice == _config.AiEngine)
        {
            PostAiEngine();
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
        _terminal.Ai = engine;
        PostAiEngine();
        RefreshUsageOf(engine);
        PostUsage();
        if (sameEngine && _terminal.IsRunning)
        {
            return;
        }

        if (_terminalStartedOnce)
        {
            _terminal.Stop();
            Post(new { t = "term-reset" });
            StartTerminal(_lastTerminalSize.Cols, _lastTerminalSize.Rows, continueLast: _terminal.HasPreviousConversation());
        }
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

        _terminal.Ai = engine;
        PostAiEngine();
        RefreshUsageOf(engine);
        PostUsage();
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

            double? left = RemainingOf(engine);
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

    // 그 엔진 한도 창들 중 가장 적게 남은 % (모름·다시 차는 시각 지남 = null)
    private double? RemainingOf(string engine)
    {
        IEnumerable<UsageWindow> windows;
        if (AiEngines.Gpt == engine)
        {
            windows = _codexSnapshot?.Windows ?? Array.Empty<UsageWindow>();
        }
        else if (AiEngines.Gemini == engine)
        {
            windows = _geminiSnapshot?.Windows ?? Array.Empty<UsageWindow>();
        }
        else
        {
            windows = _planUsage?.Windows() ?? new List<UsageWindow>();
        }

        return MinRemaining(windows);
    }

    private static double? MinRemaining(IEnumerable<UsageWindow> windows)
    {
        double? min = null;
        DateTime now = DateTime.UtcNow;
        foreach (UsageWindow window in windows)
        {
            if (false == window.IsLive(now))
            {
                continue;
            }

            double left = 100 - window.UsedPercent;
            if (null == min || left < min.Value)
            {
                min = left;
            }
        }

        return min;
    }
}
