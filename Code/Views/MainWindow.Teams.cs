using System.IO;
using System.Text.Json.Nodes;
using GameMaker.Core;

namespace GameMaker.Views;

// 사장님 화면의 4팀 현황판 (기획·프로그래밍·리소스·QA, 2×2 = wwwroot/teams.js).
// 각 팀 = 프로젝트 팀 문서(DataDir\projects\<id>\docs)\<팀>\현황.md (지금·최근 완료·다음 — 터미널 Claude 가 쓴다).
// 지금 일하는 팀 표시는 터미널 출력의 🏷️ 태그(terminal.js) + ComfyUI 실행(리소스)으로 JS 가 판정
public partial class MainWindow
{
    private sealed record Team(string Key, string Name, string Folder, string Tags);

    // Tags = 이 팀으로 보는 Claude 첫 줄 태그 (| 구분, claude_context.md 와 같은 표)
    private static readonly Team[] Teams =
    {
        new("plan", "기획", "기획", "Planner"),
        new("prog", "프로그래밍", "프로그래밍", "Builder|Developer"),
        new("res", "리소스", "리소스", "Resource"),
        new("qa", "QA", "QA", "Reviewer"),
    };
    private const string StatusFileName = "현황.md";
    private const string NotesFileName = "notes.md";

    private string _teamDocsDir = "";
    private FileSystemWatcher? _teamWatcher;
    private readonly DispatcherTimer _teamDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void InitializeTeams()
    {
        _teamDebounce.Tick += (_, _) =>
        {
            _teamDebounce.Stop();
            PostTeams();
        };
        WatchTeamDocs();
    }

    // 지금 프로젝트의 팀 문서 폴더를 지켜봄 (프로젝트를 바꾸면 다시 부름). 프로젝트가 없으면 지켜볼 곳 없음
    private void WatchTeamDocs()
    {
        _teamWatcher?.Dispose();
        _teamWatcher = null;
        _teamDocsDir = _terminal.TeamDocsPath;
        if (0 == _teamDocsDir.Length)
        {
            return;
        }

        foreach (Team team in Teams)
        {
            Directory.CreateDirectory(Path.Combine(_teamDocsDir, team.Folder));
        }

        // 터미널 Claude 가 현황을 고치면 다시 읽기
        _teamWatcher = new FileSystemWatcher(_teamDocsDir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _teamDebounce.Stop();
            _teamDebounce.Start();
        });
        _teamWatcher.Created += changed;
        _teamWatcher.Changed += changed;
        _teamWatcher.Deleted += changed;
        _teamWatcher.Renamed += (sender, e) => changed(sender, e);
    }

    private void DisposeTeams()
    {
        _teamWatcher?.Dispose();
    }

    private void OnTeamMessage(string type, JsonNode message)
    {
        if ("team-docs" != type || 0 == _teamDocsDir.Length)
        {
            return;
        }

        // team 이 있으면 그 팀 현황.md 를, 없으면 작업 기록 notes.md 를 골라 둔 채 탐색기로 (md 를 여는 프로그램이 없는 PC 도 있어서)
        string? key = message["team"]?.GetValue<string>();
        Team? team = Teams.FirstOrDefault(item => item.Key == key);
        string path = null != team ? Path.Combine(_teamDocsDir, team.Folder, StatusFileName) : Path.Combine(_teamDocsDir, NotesFileName);
        if (File.Exists(path))
        {
            Services.DesktopShell.Reveal(path);
            return;
        }

        OpenShell(Path.GetDirectoryName(path)!);
    }

    private void PostTeams()
    {
        // 프로젝트가 없으면 none = true (화면이 "프로젝트 없음 — [새 프로젝트]" 안내)
        bool none = 0 == _teamDocsDir.Length;
        var teams = Teams.Select(team =>
        {
            string status = none ? "" : Path.Combine(_teamDocsDir, team.Folder, StatusFileName);
            return new { key = team.Key, name = team.Name, tags = team.Tags, status = File.Exists(status) ? File.ReadAllText(status) : "" };
        }).ToList();
        Post(new { t = "teams", none, teams });
    }
}
