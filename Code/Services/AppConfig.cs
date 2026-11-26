using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameMaker.Services;

// 설정 = 설치 폴더의 appsettings.json (기본값, 새 버전 설치 때 덮어씀) 위에
//        %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json (앱 화면에서 바꾼 값 — 새 버전 설치해도 남음) 을 덮어 읽는다.
// 빈 값 = 자동: DataDir = 문서\AL-ZARD_GAMEMAKER, ComfyRoot = 설치 폴더\ComfyUI 가 있으면 그곳, 없으면 그림 도구 안 씀, GodotExe = 설치 폴더\godot\ 의 exe → PATH 의 godot
public sealed class AppConfig
{
    private const string SettingsFileName = "appsettings.json";
    private const string UserSettingsFileName = "settings.json";
    public const string AppFolderName = "AL-ZARD_GAMEMAKER";

    // %LOCALAPPDATA%\AL-ZARD_GAMEMAKER — 설정·화면 저장소·app.log (설치기의 "데이터도 지울까요" 와 같은 자리)
    public static string LocalAppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    // 시작할 때 기본값으로 대신한 것 (설정 파일 깨짐·데이터 폴더 못 씀) — 화면에 한 줄씩 보여 줌
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Notices { get; } = new();

    public string ComfyRoot { get; set; } = "";
    public string ComfyUrl { get; set; } = "http://127.0.0.1:8188";
    public string DataDir { get; set; } = "";
    public bool StopServerOnExit { get; set; } = true;
    public string ClaudeExe { get; set; } = "claude"; // Claude Code CLI (오른쪽 터미널, Services/ClaudeTerminal.cs). PATH 에 없으면 전체 경로
    public string ClaudeArgs { get; set; } = "";       // 터미널 claude 에 덧붙일 인자 (예: --permission-mode acceptEdits)
    public string AiEngine { get; set; } = AiEngines.Auto;   // 오른쪽 AI 칸 엔진: auto | claude | gpt | gemini (화면 [자동][CLAUDE][GPT][GEMINI] 에서 고르면 settings.json 에 저장 — 저장한 값이 없으면 자동)
    public string CodexExe { get; set; } = "codex";    // GPT 칸의 Codex CLI (Services/CodexLauncher.cs). PATH·npm 전역 폴더에 없으면 전체 경로
    public string CodexArgs { get; set; } = "-s workspace-write -a on-request"; // 터미널 codex 에 덧붙일 인자. 기본 = 작업 폴더 안에만 쓰기, 그 밖·인터넷 명령은 승인창
    public string AgyExe { get; set; } = "agy";        // GEMINI 칸의 Antigravity CLI (Services/AgyLauncher.cs). PATH·설치 스크립트 자리에 없으면 전체 경로
    public string AgyModel { get; set; } = AgyLauncher.DefaultModel; // GEMINI 칸 모델 — agy 기본 모델은 따로 세는 비싼 묶음이라 무료 Gemini 묶음으로 꼭 지정
    public string AgyArgs { get; set; } = "--mode accept-edits"; // 터미널 agy 에 덧붙일 인자. 기본 = 파일 고치기는 묻지 않고 셸 명령은 승인창
    public int ControlPort { get; set; }               // 터미널 claude → 앱 실행 요청 (Services/ControlServer.cs). 0 = 시작할 때 빈 포트를 고름
    public string GodotExe { get; set; } = "";         // 게임 실행·에디터. 비우면 설치 폴더\godot\ → PATH

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json — 화면에서 바꾼 설정 (설치 폴더 밖이라 새 버전 설치에도 남음)
    public static string UserSettingsPath => Path.Combine(LocalAppDir, UserSettingsFileName);

    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        AppConfig config;
        try
        {
            config = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 깨진 기본 설정 = 기본값으로 뜸 (안 뜨는 것보다 낫다)
            config = new AppConfig();
            config.Notices.Add($"설치 폴더의 appsettings.json 을 읽지 못해 기본 설정으로 켰습니다 ({ex.Message}) — 다시 설치하면 고쳐집니다.");
        }

        config.ApplyUserSettings();
        config.AiEngine = AiEngines.NormalizeChoice(config.AiEngine);
        return config;
    }

    // 사용자 설정 파일의 문자열 값만 덮어씀 (깨진 파일이면 무시 — 기본값으로 뜨는 게 안 뜨는 것보다 낫다)
    private void ApplyUserSettings()
    {
        JsonObject? user = ReadUserSettings();
        if (null == user)
        {
            return;
        }

        ComfyRoot = ReadString(user, nameof(ComfyRoot)) ?? ComfyRoot;
        ComfyUrl = ReadString(user, nameof(ComfyUrl)) ?? ComfyUrl;
        DataDir = ReadString(user, nameof(DataDir)) ?? DataDir;
        GodotExe = ReadString(user, nameof(GodotExe)) ?? GodotExe;
        ClaudeExe = ReadString(user, nameof(ClaudeExe)) ?? ClaudeExe;
        AiEngine = AiEngines.NormalizeChoice(ReadString(user, nameof(AiEngine)) ?? AiEngine);
        CodexExe = ReadString(user, nameof(CodexExe)) ?? CodexExe;
        AgyExe = ReadString(user, nameof(AgyExe)) ?? AgyExe;
        AgyModel = ReadString(user, nameof(AgyModel)) ?? AgyModel;
    }

    private static JsonObject? ReadUserSettings()
    {
        try
        {
            string path = UserSettingsPath;
            if (false == File.Exists(path))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject user, string key)
    {
        JsonNode? node = user[key];
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        return null;
    }

    // 화면에서 바꾼 값 하나를 사용자 설정 파일에 저장 (다른 키는 그대로)
    public static void SaveUserSetting(string key, string value)
    {
        JsonObject user = ReadUserSettings() ?? new JsonObject();
        user[key] = value;
        string path = UserSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, user.ToJsonString(JsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // 환경 변수(%X%) + 맨 앞 ~ (맥 홈) 펼치기
    public static string ExpandPath(string? path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path ?? "").Trim();
        bool isHome = "~" == expanded || expanded.StartsWith("~/", StringComparison.Ordinal);
        if (isHome)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = home + expanded[1..];
        }

        return expanded;
    }

    // 데이터 폴더 정하기: 설정값 → (비었거나 설치 폴더 안/같으면) 문서\AL-ZARD_GAMEMAKER → (만들거나 쓸 수 없으면) %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\data.
    // 설치 폴더 안은 거부 — 새 버전 설치·제거가 설치 폴더 파일을 지우므로 만든 게임이 같이 사라질 수 있음. 대신한 이유는 Notices 에
    public string ResolveDataDir()
    {
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppFolderName);
        string chosen = defaultDir;
        string configured = ExpandPath(DataDir);
        if (configured.Length > 0)
        {
            string full;
            try
            {
                full = Path.GetFullPath(configured);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                full = "";
                Notices.Add($"데이터 폴더 설정이 올바른 경로가 아니라 기본 폴더를 씁니다: {configured}");
            }

            if (full.Length > 0 && IsSameOrInside(full, AppContext.BaseDirectory))
            {
                Notices.Add($"데이터 폴더가 설치 폴더 안이라 쓰지 않고 기본 폴더를 씁니다 (새 버전 설치·제거 때 지워질 수 있음): {full}");
            }
            else if (full.Length > 0)
            {
                chosen = full;
            }
        }

        if (EnsureWritableDir(chosen))
        {
            return chosen;
        }

        string fallback = Path.Combine(LocalAppDir, "data");
        Notices.Add($"데이터 폴더를 만들거나 쓸 수 없어 대신 이곳을 씁니다: {fallback} (원래 자리: {chosen})");
        return fallback;
    }

    // 폴더를 (없으면) 만들고 파일 하나를 써 보고 지움 — 묻기만 하지 않고 폴더를 만드는 일까지 하므로 Ensure
    private static bool EnsureWritableDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // path 가 parent 와 같거나 그 안인지 (대소문자 무시)
    public static bool IsSameOrInside(string path, string parent)
    {
        char sep = Path.DirectorySeparatorChar;
        string a = Path.GetFullPath(path).TrimEnd(sep) + sep;
        string b = Path.GetFullPath(parent).TrimEnd(sep) + sep;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    // ComfyUI 폴더. 설정값 → (비었으면) [설치하기] 자리 설치 폴더\ComfyUI 가 있으면 그곳 (godot 와 같은 대우) → 없음 ""
    public string ResolveComfyRoot()
    {
        string root = ExpandPath(ComfyRoot);
        if (root.Length > 0)
        {
            return Path.GetFullPath(root);
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, ComfyInstaller.FolderName);
        return Directory.Exists(bundled) ? bundled : "";
    }

    // Godot 실행 파일. 설정값 → 설치 폴더\godot\ 의 Godot_*.exe (콘솔판 제외) → PATH 의 godot. 못 찾으면 ""
    public string ResolveGodotExe()
    {
        string exe = ExpandPath(GodotExe);
        if (exe.Length > 0)
        {
            bool isBundle = exe.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(exe);
            return isBundle ? Path.Combine(exe, "Contents", "MacOS", "Godot") : exe;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "godot");
        if (Directory.Exists(bundled))
        {
            string? found = Directory.GetFiles(bundled, "Godot*.exe")
                .Where(path => false == path.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (null != found)
            {
                return found;
            }
        }

        return FindOnPath(OperatingSystem.IsWindows() ? "godot.exe" : "godot") ?? "";
    }

    private static string? FindOnPath(string fileName)
    {
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string dir in dirs)
        {
            try
            {
                string path = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (ArgumentException)
            {
                // PATH 에 이상한 글자가 든 줄은 건너뜀
            }
        }

        return null;
    }
}
