using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameMaker.Services;

// 게임 프로젝트 하나 = 팀 문서(DataDir\projects\<id>\docs) + 게임 폴더(DataDir\games\<폴더>, Godot 프로젝트 또는 웹 게임)
public sealed class Project
{
    public const string EngineGodot = "godot";
    public const string EngineWeb = "web";

    // 엔진 이름 정규화: godot | web 만 인정, 비었거나 모르는 값(옛 projects.json 포함)은 godot
    public static string NormalizeEngine(string? engine)
    {
        return string.Equals(engine?.Trim(), EngineWeb, StringComparison.OrdinalIgnoreCase) ? EngineWeb : EngineGodot;
    }

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string GameDir { get; set; } = "";   // 게임 폴더 이름만 (경로는 매번 DataDir\games\<이름> — 데이터 폴더를 옮겨도 따라감)
    public string Created { get; set; } = "";
    public string Engine { get; set; } = EngineGodot; // "godot" | "web" (옛 파일에 없으면 godot)

    public bool IsWeb => EngineWeb == Engine;
}

// 삭제 결과: 휴지통에 못 넣어 남은 폴더들 (없으면 빈 목록)
public sealed record DeleteResult(Project Project, IReadOnlyList<string> FoldersLeft);

// 프로젝트 목록·지금 고른 것 = DataDir\projects.json.
// 만들기 = template\ 을 DataDir\projects\<id>\docs 로 복사 + 빈 게임 폴더. 삭제 = 목록에서 뺌 → 게임 폴더·프로젝트 폴더(문서) 휴지통.
public sealed class ProjectStore
{
    private const string FileName = "projects.json";
    private const string GamesFolder = "games";
    private const string ProjectsFolder = "projects";
    private const string DocsFolder = "docs";
    private const int MaxNameLength = 40;
    private static readonly Regex FolderPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$");
    private static readonly Regex ReservedFolder = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);
    private static readonly Regex IdPattern = new(@"^[0-9a-f]{8}$");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class FileModel
    {
        public string? Current { get; set; }
        public List<Project> Projects { get; set; } = new();
    }

    private readonly string _dataDir;
    private readonly string _templateDir;
    private readonly string _webSkeletonDir;
    private FileModel _model = new();
    private bool _readFailed; // projects.json 을 못 읽음 — 저장하면 원본을 덮으니 막는다

    private string FilePath => Path.Combine(_dataDir, FileName);

    public ProjectStore(string dataDir, string templateDir, string webSkeletonDir)
    {
        _dataDir = dataDir;
        _templateDir = templateDir;
        _webSkeletonDir = webSkeletonDir;
        Load();
        RefreshAppKnowledge();
    }

    // 앱이 들고 있는 지식 (template 안, 프로젝트마다 덮어씀) — 일반 개념·팀 정의라 게임마다 다를 게 없다.
    // 게임별 기록(현황·배운_것·밸런스·숫자_지도·결정_기록·notes …)은 여기 넣지 않는다 = 프로젝트 것, 안 건드림.
    private static readonly string[] AppKnowledge =
    {
        "agents",
        Path.Combine("기획", "기획팀_작업법.md"),
        Path.Combine("참고", "godot"),
    };

    // 앱을 켤 때 모든 프로젝트 팀 문서의 AppKnowledge 를 지금 설치판 template 으로 맞춘다 — 앱 업데이트가 옛 프로젝트에도 닿게.
    // 내용이 같은 파일은 안 씀, 프로젝트에만 있는 파일은 안 지움. 실패는 그 프로젝트만 건너뜀 (켜기를 막지 않음)
    private void RefreshAppKnowledge()
    {
        if (false == Directory.Exists(_templateDir))
        {
            return;
        }

        foreach (Project project in _model.Projects)
        {
            string docs = ResolveTeamDocs(project);
            if (false == Directory.Exists(docs))
            {
                continue;
            }

            try
            {
                foreach (string entry in AppKnowledge)
                {
                    bool godotOnly = SamePath(entry, Path.Combine("참고", "godot"));
                    if (godotOnly && project.IsWeb)
                    {
                        continue;
                    }

                    string source = Path.Combine(_templateDir, entry);
                    string target = Path.Combine(docs, entry);
                    if (File.Exists(source))
                    {
                        SyncFile(source, target);
                    }
                    else if (Directory.Exists(source))
                    {
                        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                        {
                            SyncFile(file, Path.Combine(target, Path.GetRelativePath(source, file)));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 다음 켜기에 다시 — 문서가 열려 있어 잠긴 경우 등
            }
        }
    }

    private static void SyncFile(string source, string target)
    {
        byte[] content = File.ReadAllBytes(source);
        if (File.Exists(target) && content.AsSpan().SequenceEqual(File.ReadAllBytes(target)))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(target);
        if (null != dir)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(target, content);
    }

    public IReadOnlyList<Project> Projects => _model.Projects;

    public Project? Current => _model.Projects.FirstOrDefault(item => item.Id == _model.Current);

    // 프로젝트 폴더 (DataDir\projects\<id>) — 팀 문서는 그 안 docs
    private string ProjectDir(Project project)
    {
        return Path.Combine(_dataDir, ProjectsFolder, project.Id);
    }

    // 팀 문서 폴더의 실제 경로
    public string ResolveTeamDocs(Project project)
    {
        return Path.Combine(ProjectDir(project), DocsFolder);
    }

    // 게임 폴더의 실제 경로 = DataDir\games\<이름>. 이름이 비었거나 이상하면 "" (어디도 가리키지 않음)
    public string ResolveGameDir(Project project)
    {
        string folder = FolderName(project.GameDir);
        return 0 == folder.Length ? "" : Path.Combine(_dataDir, GamesFolder, folder);
    }

    // 저장값 → 게임 폴더 이름. 옛 값(절대 경로)이면 마지막 폴더 이름만 쓴다. 폴더 이름 규칙에 안 맞으면 ""
    private static string FolderName(string? stored)
    {
        char[] separators = { '\\', '/' };
        string value = (stored ?? "").Trim().TrimEnd(separators);
        string name = value[(value.LastIndexOfAny(separators) + 1)..];
        return FolderPattern.IsMatch(name) && false == ReservedFolder.IsMatch(name) ? name : "";
    }

    public void Select(string id)
    {
        if (_model.Projects.All(item => item.Id != id))
        {
            throw new InvalidOperationException("없는 프로젝트입니다.");
        }

        EnsureWritable();
        _model.Current = id;
        Save();
    }

    // 이름 + 영문 폴더 이름(비우면 game1·game2…) + 엔진 → 템플릿 복사·게임 폴더(웹 = 최소 뼈대, Godot = 빈 폴더)·지금 것으로 고름
    public Project Create(string name, string folder, string engine)
    {
        EnsureWritable();
        name = name.Trim();
        ValidateName(name);
        engine = engine.Trim().ToLowerInvariant();
        if (Project.EngineGodot != engine && Project.EngineWeb != engine)
        {
            throw new InvalidOperationException("엔진은 godot 또는 web 만 됩니다.");
        }

        folder = folder.Trim();
        if (0 == folder.Length)
        {
            folder = NextFolderName();
        }
        else if (false == FolderPattern.IsMatch(folder) || ReservedFolder.IsMatch(folder))
        {
            throw new InvalidOperationException("게임 폴더 이름은 영문·숫자·_·- 만 됩니다 (31자 이내, CON·NUL·COM1 같은 윈도우 예약어 불가).");
        }

        string gameDir = Path.Combine(_dataDir, GamesFolder, folder);
        bool gameDirTaken = Directory.Exists(gameDir) || _model.Projects.Any(item => string.Equals(item.GameDir, folder, StringComparison.OrdinalIgnoreCase));
        if (gameDirTaken)
        {
            throw new InvalidOperationException($"게임 폴더가 이미 있습니다: {folder}");
        }

        if (false == Directory.Exists(_templateDir))
        {
            throw new InvalidOperationException($"템플릿 폴더가 없습니다 (다시 설치해 주세요): {_templateDir}");
        }

        if (Project.EngineWeb == engine && false == Directory.Exists(_webSkeletonDir))
        {
            throw new InvalidOperationException($"웹 뼈대 폴더가 없습니다 (다시 설치해 주세요): {_webSkeletonDir}");
        }

        DateTime now = DateTime.Now;
        Project project = new()
        {
            Id = NewId(),
            Name = name,
            GameDir = folder,
            Created = now.ToString("yyyy-MM-dd"),
            Engine = engine,
        };
        string projectDir = ProjectDir(project);
        string? previousCurrent = _model.Current;
        bool added = false;
        try
        {
            CopyTemplate(ResolveTeamDocs(project), project);
            Directory.CreateDirectory(gameDir);
            if (project.IsWeb)
            {
                CopyDirectory(_webSkeletonDir, gameDir); // index.html·js/main.js — 외부 CDN 없이 오프라인으로 돈다
            }

            _model.Projects.Add(project);
            added = true;
            _model.Current = project.Id;
            Save();
            return project;
        }
        catch
        {
            // 위에서 "없다" 고 확인한 경로에 이번에 방금 만든 빈 템플릿 사본·빈 게임 폴더만 되돌림 (잃을 것 없음)
            if (added)
            {
                _model.Projects.Remove(project);
                _model.Current = previousCurrent;
            }

            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(gameDir);
            throw;
        }
    }

    // ① 지울 두 폴더 검사 → ② 목록에서 빼고 저장 → ③ 게임 폴더·프로젝트 폴더(문서) 휴지통. 휴지통이 안 되면 남은 폴더를 결과에 담음
    public DeleteResult Delete(string id)
    {
        EnsureWritable();
        Project project = _model.Projects.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("없는 프로젝트입니다.");
        // 검사한 이 경로로만 지운다
        string gameDir = EnsureDeletableGameDir(project);
        string projectDir = EnsureDeletableProjectDir(project);
        _model.Projects.Remove(project);
        if (_model.Current == id)
        {
            _model.Current = null;
        }

        Save();
        List<string> left = new();
        foreach (string dir in new[] { gameDir, projectDir })
        {
            if (0 == dir.Length || false == Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                TrashBin.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or PlatformNotSupportedException)
            {
                left.Add(dir);
            }
        }

        return new DeleteResult(project, left);
    }

    // ---------- 읽기·쓰기 ----------

    private void Load()
    {
        if (false == File.Exists(FilePath))
        {
            // 처음 실행 = 프로젝트 없음 (화면이 "프로젝트 없음 — [새 프로젝트]")
            _model = new FileModel();
            return;
        }

        try
        {
            _model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), JsonOptions) ?? new FileModel();
            _model.Projects ??= new();
            foreach (Project project in _model.Projects)
            {
                project.GameDir = FolderName(project.GameDir);
                project.Engine = Project.NormalizeEngine(project.Engine); // 옛 파일(engine 없음) = godot
            }
        }
        catch (JsonException)
        {
            // 깨진 파일은 옆에 날짜 붙여 남겨 두고 빈 목록으로 (앱이 안 뜨는 것보다 낫다)
            try
            {
                File.Copy(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readFailed = true; // 백업도 못 하면 덮어쓰지 않는다
            }

            _model = new FileModel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 잠겼거나 권한 없음 — 앱은 뜨되 원본을 덮지 않게 저장 금지
            _readFailed = true;
            _model = new FileModel();
        }
    }

    private void EnsureWritable()
    {
        if (_readFailed)
        {
            throw new InvalidOperationException($"projects.json 을 읽지 못해 프로젝트를 바꿀 수 없습니다 (원본 보호). 파일을 확인하고 앱을 다시 켜 주세요: {FilePath}");
        }
    }

    private void Save()
    {
        EnsureWritable();
        Directory.CreateDirectory(_dataDir);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_model, JsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }

    // ---------- 만들기 도우미 ----------

    private void ValidateName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '"' }).ToArray();
        if (0 == name.Length || name.Length > MaxNameLength || name.IndexOfAny(bad) >= 0)
        {
            throw new InvalidOperationException($"프로젝트 이름은 1~{MaxNameLength}자, 파일 이름에 못 쓰는 글자(\\ / : \" 등) 없이 적어 주세요.");
        }

        bool duplicated = _model.Projects.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicated)
        {
            throw new InvalidOperationException($"같은 이름의 프로젝트가 이미 있습니다: {name}");
        }
    }

    // 8자리 id — 목록·폴더 어디에도 없는 것
    private string NewId()
    {
        while (true)
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            bool taken = _model.Projects.Any(item => item.Id == id) || Directory.Exists(Path.Combine(_dataDir, ProjectsFolder, id));
            if (false == taken)
            {
                return id;
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 되돌리기 실패는 원래 오류를 가리지 않게 삼킴
        }
    }

    private string NextFolderName()
    {
        for (int number = 1; ; number++)
        {
            string candidate = $"game{number}";
            string path = Path.Combine(_dataDir, GamesFolder, candidate);
            if (false == Directory.Exists(path) && _model.Projects.All(item => false == string.Equals(item.GameDir, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    // template\ 복사 (웹이면 참고\godot 은 뺌). notes.md·_프로젝트_안내.md·프로그래밍\현황.md 의 자리표시자만 채움
    private void CopyTemplate(string target, Project project)
    {
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"팀 문서 폴더가 이미 있습니다: {target}");
        }

        CopyDirectory(_templateDir, target, skip: project.IsWeb ? Path.Combine(_templateDir, "참고", "godot") : null);
        foreach (string name in new[] { "notes.md", "_프로젝트_안내.md", Path.Combine("프로그래밍", "현황.md") })
        {
            string path = Path.Combine(target, name);
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path)
                    .Replace("{ProjectName}", project.Name)
                    .Replace("{Created}", project.Created)
                    .Replace("{GameDir}", ResolveGameDir(project))
                    .Replace("{Engine}", project.IsWeb ? "웹 (HTML5 + JS)" : "Godot")
                    .Replace("{EngineFirstFile}", project.IsWeb ? "`index.html`" : "`project.godot`");
                text = ApplyEngineLines(text, project.Engine);
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            }
        }
    }

    // 줄 맨 앞 {godot} / {web} 표시: 맞는 엔진이면 표시만 지우고, 아니면 그 줄을 뺌
    private static string ApplyEngineLines(string text, string engine)
    {
        string keep = "{" + engine + "}";
        List<string> lines = new();
        foreach (string line in text.Split('\n'))
        {
            bool isEngineLine = line.StartsWith("{godot}", StringComparison.Ordinal) || line.StartsWith("{web}", StringComparison.Ordinal);
            if (false == isEngineLine)
            {
                lines.Add(line);
            }
            else if (line.StartsWith(keep, StringComparison.Ordinal))
            {
                lines.Add(line[keep.Length..]);
            }
        }

        return string.Join('\n', lines);
    }

    private static void CopyDirectory(string source, string target, string? skip = null)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            if (null != skip && SamePath(dir, skip))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), skip);
        }
    }

    // ---------- 삭제 안전장치 ----------

    // 게임 폴더로 지울 수 있는 건 DataDir\games\<폴더> 정확히 그 자리뿐 (공용 자산·DataDir 자체 보호)
    private string EnsureDeletableGameDir(Project project)
    {
        string gameDir = ResolveGameDir(project);
        if (0 == gameDir.Length)
        {
            return ""; // 이름이 깨진 기록 — 게임 폴더는 건드리지 않고 목록·문서만 정리
        }

        string full = EnsureDirectChild(gameDir, GamesFolder, "게임 폴더");
        bool shared = _model.Projects.Any(item => item.Id != project.Id && string.Equals(item.GameDir, project.GameDir, StringComparison.OrdinalIgnoreCase));
        if (shared)
        {
            throw new InvalidOperationException($"다른 프로젝트와 게임 폴더가 겹쳐 지우지 않습니다: {full}");
        }

        return full;
    }

    // 프로젝트 폴더(문서)로 지울 수 있는 건 DataDir\projects\<id> 정확히 그 자리뿐
    private string EnsureDeletableProjectDir(Project project)
    {
        if (false == IdPattern.IsMatch(project.Id))
        {
            throw new InvalidOperationException($"프로젝트 id 가 이상해서 문서 폴더를 지우지 않습니다: {project.Id}");
        }

        return EnsureDirectChild(ProjectDir(project), ProjectsFolder, "프로젝트 폴더");
    }

    // path 가 DataDir\<parentName>\<이름> 바로 그 자리이고, DataDir 아래 어디에도 정션·심볼릭 링크가 끼지 않았는지. 정규화 경로를 돌려줌
    private string EnsureDirectChild(string path, string parentName, string label)
    {
        char sep = Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(_dataDir).TrimEnd(sep);
        string full = Path.GetFullPath(path).TrimEnd(sep);
        if (false == full.StartsWith(root + sep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label}가 데이터 폴더 밖이라 지우지 않습니다: {full}");
        }

        string parent = Path.GetDirectoryName(full) ?? "";
        if (false == SamePath(parent, Path.Combine(root, parentName)))
        {
            throw new InvalidOperationException($"{label} 자리({parentName}\\<이름>)가 아니라 지우지 않습니다: {full}");
        }

        // 자기 자신과 DataDir 아래 모든 조상에 정션·심볼릭 링크가 있으면 링크 너머를 지울 수 있어 거부
        for (string? current = full; null != current && current.Length > root.Length; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"바로가기(링크) 폴더가 끼어 있어 지우지 않습니다: {current}");
            }
        }

        return full;
    }

    private static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
