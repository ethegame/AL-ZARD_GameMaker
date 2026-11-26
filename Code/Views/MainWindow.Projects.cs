using System.IO;
using System.Text.Json.Nodes;
using GameMaker.Services;

namespace GameMaker.Views;

// 프로젝트 고르기·[새 프로젝트]·[삭제] — 메시지 연결만 (일은 Services/ProjectStore.cs, 화면 = wwwroot/projects.js).
// 프로젝트를 바꾸면 현황판·게임 칸·터미널 context 가 그 프로젝트로 (터미널은 그 프로젝트 게임 폴더에서 열려 프로젝트별 대화를 잇는다)
public partial class MainWindow
{
    private ProjectStore _projects = null!; // InitializeDev 에서 만듦

    private void InitializeProjects()
    {
        _projects = new ProjectStore(_dataDir, Path.Combine(AppContext.BaseDirectory, "template"), Path.Combine(AppContext.BaseDirectory, "webskel"));
        ApplyProjectToTerminal();
    }

    // 지금 프로젝트의 경로를 터미널(context 치환 {TeamDocs}·{GameDir})에 알림
    private void ApplyProjectToTerminal()
    {
        Project? current = _projects.Current;
        _terminal.TeamDocsPath = null != current ? _projects.ResolveTeamDocs(current) : "";
        _terminal.GameDir = null != current ? _projects.ResolveGameDir(current) : "";
        _terminal.Engine = current?.Engine ?? Project.EngineGodot;
    }

    private void OnProjectMessage(string type, JsonNode message)
    {
        switch (type)
        {
            case "projects": {
                PostProjects();
                break;
            }
            case "project-select": {
                string id = message["id"]?.GetValue<string>() ?? "";
                if (id != _projects.Current?.Id)
                {
                    _projects.Select(id);
                    SwitchProject();
                }

                break;
            }
            case "project-new": {
                _projects.Create(message["name"]?.GetValue<string>() ?? "", message["folder"]?.GetValue<string>() ?? "", message["engine"]?.GetValue<string>() ?? Project.EngineGodot);
                SwitchProject();
                break;
            }
            case "project-delete": {
                string id = message["id"]?.GetValue<string>() ?? "";
                bool wasCurrent = id == _projects.Current?.Id;
                DeleteResult result = _projects.Delete(id);
                string note = DeleteNote(result);
                if (wasCurrent)
                {
                    SwitchProject(note);
                }
                else
                {
                    PostProjects(note);
                }

                break;
            }
        }
    }

    // 실제로 된 것만 말한다 (휴지통은 폴더마다 실패할 수 있음)
    private static string DeleteNote(DeleteResult result)
    {
        string head = $"'{result.Project.Name}' 삭제됨";
        if (0 == result.FoldersLeft.Count)
        {
            return head + " — 게임·문서 폴더는 휴지통에 있습니다";
        }

        return head + " — 남은 폴더는 직접 지워 주세요: " + string.Join(", ", result.FoldersLeft);
    }

    // 현황판·게임 칸·터미널을 지금 프로젝트로 다시 맞춤
    private void SwitchProject(string? note = null)
    {
        ApplyProjectToTerminal();
        WatchTeamDocs();
        PostProjects(note);
        PostTeams();
        PostGames();
        _session.Restart();   // 터미널을 띄운 적 있으면 새 프로젝트 게임 폴더에서 다시 (그 프로젝트 대화를 잇거나 새로)
    }

    private void PostProjects(string? note = null)
    {
        var items = _projects.Projects.Select(item => new { id = item.Id, name = item.Name, engine = item.Engine }).ToList();
        Post(new { t = "projects", items, current = _projects.Current?.Id, note });
    }
}
