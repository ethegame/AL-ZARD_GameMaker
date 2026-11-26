namespace GameMaker.Services;

// 오른쪽 AI 칸에서 고를 수 있는 엔진 — 자동 · Claude (Claude Code) · GPT (OpenAI Codex CLI) · GEMINI (Google Antigravity CLI, agy).
// 고른 값은 %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json 의 AiEngine 에 남는다 (Services/AppConfig.cs).
// 자동 = 새 세션을 띄울 때 실제 엔진 하나를 고름 (Views/TerminalSession.cs PickAutoEngine) — 터미널이 실제로 도는 엔진은 늘 claude | gpt | gemini
public static class AiEngines
{
    public const string Auto = "auto";
    public const string Claude = "claude";
    public const string Gpt = "gpt";
    public const string Gemini = "gemini";

    // 자동이 보는 순서 (Claude 먼저 — 허브 앱의 허브 AI 자동과 같은 순서)
    public static readonly string[] AutoChain = { Claude, Gpt, Gemini };

    // 고른 값 (자동 포함). 없거나 모르는 값 = 자동
    public static string NormalizeChoice(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        if (Claude == text || Gpt == text || Gemini == text)
        {
            return text;
        }

        return Auto;
    }

    // 실제로 도는 엔진. 자동·모르는 값 = claude
    public static string Normalize(string? value)
    {
        string choice = NormalizeChoice(value);
        if (Auto == choice)
        {
            return Claude;
        }

        return choice;
    }

    // 화면에 보이는 이름 (탭·안내 글)
    public static string DisplayName(string engine)
    {
        if (Gpt == engine)
        {
            return "GPT";
        }

        if (Gemini == engine)
        {
            return "GEMINI";
        }

        if (Auto == engine)
        {
            return "자동";
        }

        return "Claude";
    }

    // 실제 프로그램 이름 (터미널 안내 글·AI 에게 주는 지침의 {Ai})
    public static string ToolName(string engine)
    {
        if (Gpt == engine)
        {
            return "Codex (GPT)";
        }

        if (Gemini == engine)
        {
            return "Antigravity (GEMINI)";
        }

        return "Claude Code";
    }
}
