using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameMaker.Core;

namespace GameMaker.Services;

// [⬆ 업데이트 <버전>] — 새 판을 GitHub 릴리스(공개 저장소)에서 받아 설치까지 한 번에. Windows 만 (맥이면 아무것도 보이지 않고 확인도 안 함).
// 자리 = 화면 오른쪽 아래 [새 소식] 옆 (index.html #update-row, wwwroot/update.js). 새 소식을 찾는 곳이라 같은 곳에 둠.
// 확인 = 켜고 20초 뒤 + 6시간마다 로그인 없이 GET releases/latest (한도 = 시간당 60번). 실패·한도 초과·저장소 없음(404)은 app.log 에만 남기고 화면은 조용히.
// 누르면(화면에서 확인 한 번) → 설치 파일을 데이터 폴더 updates 에 받기(진행 %, 크기·해시 대조) →
//   updates\run-update.ps1 이 앱이 꺼지길 기다렸다 설치 파일을 조용한 모드(/SILENT, 지난 설치 위치 /DIR)로 → 끝나면(실패해도) 앱을 다시 켬 → 그다음 이 앱을 닫음.
// 설치 결과 = updates\result.txt ("버전<TAB>코드"). 다음에 켤 때 실패였으면 버튼 자리에 한 줄.
// 만든 게임·문서(문서\AL-ZARD_GAMEMAKER)와 설정(%LOCALAPPDATA%\AL-ZARD_GAMEMAKER)은 설치 폴더 밖이라 설치가 건드리지 않음
public sealed class AppUpdater : IDisposable
{
    private const string LatestUrl = "https://api.github.com/repos/ethegame/AL-ZARD_GameMaker/releases/latest";
    private const string UpdatesDirName = "updates";
    private const string ResultFileName = "result.txt";
    private const string RunnerName = "run-update.ps1";
    private const string UninstallerName = "unins000.exe";
    private const string AppExeName = "GameMaker.exe";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan OldFileAge = TimeSpan.FromMinutes(10);

    private readonly string _dataDir;
    private readonly Action<object> _post;   // 화면으로 보내기 (UI 스레드)
    private readonly Action<string> _log;    // app.log 한 줄
    private readonly Action _closeApp;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };   // 요청마다 따로 시간 제한
    private readonly DispatcherTimer _timer = new() { Interval = FirstCheckDelay };
    private ReleaseInfo? _latest;       // 지금보다 새 판 (없으면 null — 버튼 숨김)
    private string _announced = "";     // app.log 에 이미 적은 새 판 (6시간마다 같은 줄이 쌓이지 않게)
    private string _note = "";          // 버튼 아래 한 줄 (지난 업데이트 실패 · 진행 · 실패)
    private bool _noteIsError;
    private int _percent = -1;
    private bool _isChecking;
    private bool _isBusy;               // 받는 중 · 넘기는 중

    private sealed class ReleaseInfo
    {
        public string VersionText { get; init; } = "";
        public string AssetName { get; init; } = "";
        public string AssetUrl { get; init; } = "";
        public long AssetSize { get; init; }
        public string Sha256 { get; init; } = "";   // 릴리스가 알려 주면 받은 파일과 대조 (없으면 크기만)
    }

    public AppUpdater(string dataDir, Action<object> post, Action<string> log, Action closeApp)
    {
        _dataDir = dataDir;
        _post = post;
        _log = log;
        _closeApp = closeApp;
    }

    // 지금 앱 버전 (어셈블리 버전 = csproj <Version>, 1.2.3 모양으로)
    public static Version CurrentVersion()
    {
        Version? assembly = Assembly.GetExecutingAssembly().GetName().Version;
        if (null == assembly)
        {
            return new Version(0, 0, 0);
        }
        return new Version(assembly.Major, assembly.Minor, Math.Max(assembly.Build, 0));
    }

    public void Start()
    {
        if (false == OperatingSystem.IsWindows())
        {
            return;
        }
        // GitHub API 는 User-Agent 가 없으면 거절
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AL-ZARD_GameMaker/{CurrentVersion()}");
        ReadLastResult();
        CleanOldFiles();
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }

    // 화면이 준비됐을 때(또는 상태가 바뀔 때) 지금 상태를 보냄
    public void PostState()
    {
        if (false == OperatingSystem.IsWindows())
        {
            return;
        }
        string state = "none";
        string version = "";
        if (null != _latest)
        {
            version = _latest.VersionText;
            state = true == _isBusy ? "busy" : "available";
        }
        _post(new { t = "update-state", state, version, current = CurrentVersion().ToString(), note = _note, noteIsError = _noteIsError, percent = _percent });
    }

    // ---------------- 확인 ----------------

    private void OnTimer()
    {
        // 첫 번째(켜고 20초 뒤) 다음부터는 6시간마다
        if (CheckInterval != _timer.Interval)
        {
            _timer.Interval = CheckInterval;
        }
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (true == _isChecking || true == _isBusy)
        {
            return;
        }
        _isChecking = true;
        try
        {
            ReleaseInfo? found = await FetchLatestAsync();
            _latest = found;
            if (null != found && _announced != found.VersionText)
            {
                _announced = found.VersionText;
                _log($"새 버전 있음: {found.VersionText} (지금 {CurrentVersion()})");
            }
            PostState();
        }
        catch (Exception ex)
        {
            // 인터넷 끊김 · 한도 초과 · 저장소 없음(404) 등 — 화면은 그대로, 다음에 다시
            _log($"새 버전 확인 실패: {ex.Message}");
        }
        _isChecking = false;
    }

    // 지금보다 높은 판이고 설치 파일이 올라와 있으면 그 정보, 아니면 null. 받기 실패 = 예외
    private async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var cts = new CancellationTokenSource(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _http.SendAsync(request, cts.Token);
        if (false == response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }
        string json = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string tag = ReadString(root, "tag_name");
        string versionText = tag.Trim().TrimStart('v', 'V');
        if (false == Version.TryParse(versionText, out Version? latest))
        {
            throw new FormatException($"릴리스 태그를 못 읽음: {tag}");
        }
        if (latest <= CurrentVersion())
        {
            return null;
        }
        // build-installer.ps1 이 만드는 이름 그대로
        string assetName = $"AL-ZARD_GAMEMAKER-Setup-{versionText}.exe";
        if (false == root.TryGetProperty("assets", out JsonElement assets))
        {
            return null;
        }
        if (JsonValueKind.Array != assets.ValueKind)
        {
            return null;
        }
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = ReadString(asset, "name");
            if (assetName != name)
            {
                continue;
            }
            long size = 0;
            if (true == asset.TryGetProperty("size", out JsonElement sizeElement))
            {
                if (JsonValueKind.Number == sizeElement.ValueKind)
                {
                    size = sizeElement.GetInt64();
                }
            }
            string sha256 = "";
            string digest = ReadString(asset, "digest");
            if (true == digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest.Substring("sha256:".Length).ToLowerInvariant();
            }
            return new ReleaseInfo
            {
                VersionText = versionText,
                AssetName = name,
                AssetUrl = ReadString(asset, "browser_download_url"),
                AssetSize = size,
                Sha256 = sha256,
            };
        }
        // 릴리스는 있는데 설치 파일이 아직 안 올라옴 — 다음 확인 때 다시
        return null;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (false == element.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }
        if (JsonValueKind.String != value.ValueKind)
        {
            return "";
        }
        return value.GetString() ?? "";
    }

    // ---------------- 누르기 ----------------

    // 화면에서 확인을 받은 뒤 부름. 받기 → 설치에 넘기기 → 앱 닫기
    public async Task StartAsync()
    {
        ReleaseInfo? release = _latest;
        if (null == release || true == _isBusy || false == OperatingSystem.IsWindows())
        {
            return;
        }
        string notInstalled = CheckInstalledCopy();
        if (0 < notInstalled.Length)
        {
            // 설치 파일로 깐 앱이 아니면 엉뚱한 곳에 깔리지 않게 손으로 받게 함
            _log($"자동 업데이트 못 함: {notInstalled}");
            SetNote(notInstalled, true);
            PostState();
            return;
        }
        _isBusy = true;
        SetNote("", false);
        PostState();
        _log($"자동 업데이트 시작: {CurrentVersion()} -> {release.VersionText}");
        string updatesDir = Path.Combine(_dataDir, UpdatesDirName);
        string assetPath = Path.Combine(updatesDir, release.AssetName);
        string error = await DownloadAsync(release, updatesDir, assetPath);
        if (0 == error.Length)
        {
            SetNote("설치를 넘기는 중…", false);
            PostState();
            error = StartInstall(release, updatesDir, assetPath);
        }
        _percent = -1;
        if (0 < error.Length)
        {
            // 실패해도 버튼은 그대로 (다시 누르면 처음부터)
            _log($"자동 업데이트 실패: {error}");
            SetNote($"업데이트 실패 — {error}", true);
            _isBusy = false;
            PostState();
            return;
        }
        _log("자동 업데이트: 설치에 넘기고 앱 종료");
        _closeApp();
    }

    private void SetNote(string text, bool isError)
    {
        _note = text;
        _noteIsError = isError;
    }

    // 설치 파일로 깐 앱인지 (제거 프로그램이 같은 폴더에 있음). 맞으면 "", 아니면 이유
    private static string CheckInstalledCopy()
    {
        string uninstaller = Path.Combine(AppContext.BaseDirectory, UninstallerName);
        if (false == File.Exists(uninstaller))
        {
            return "이 앱은 설치 파일로 깐 것이 아니라서 자동으로 바꿀 수 없어요. 새 설치 파일을 받아 실행해 주세요.";
        }
        return "";
    }

    // 받기 (.part 에 쓰고 크기·해시가 맞으면 이름 바꿈). 성공 = ""
    private async Task<string> DownloadAsync(ReleaseInfo release, string updatesDir, string target)
    {
        string part = target + ".part";
        try
        {
            Directory.CreateDirectory(updatesDir);
            SetNote("새 버전 받는 중…", false);
            PostState();
            using var cts = new CancellationTokenSource(DownloadTimeout);
            using HttpResponseMessage response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (false == response.IsSuccessStatusCode)
            {
                return $"받기 실패 (HTTP {(int)response.StatusCode})";
            }
            long total = release.AssetSize;
            if (0 >= total)
            {
                total = response.Content.Headers.ContentLength ?? 0;
            }
            long done = 0;
            await using (Stream input = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int lastPercent = -1;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cts.Token);
                    if (0 == read)
                    {
                        break;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    done += read;
                    if (0 < total)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            _percent = percent;
                            SetNote($"새 버전 받는 중… {percent}%", false);
                            PostState();
                        }
                    }
                }
            }
            if (0 < release.AssetSize && done != release.AssetSize)
            {
                TryDelete(part);
                return $"받은 크기가 달라요 ({done} / {release.AssetSize})";
            }
            if (0 < release.Sha256.Length)
            {
                string hash = await HashFileAsync(part);
                if (hash != release.Sha256)
                {
                    TryDelete(part);
                    return "받은 파일 검사(해시)가 맞지 않아요";
                }
            }
            File.Move(part, target, true);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return ex.Message;
        }
        return "";
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---------------- 설치에 넘기기 ----------------

    // 앱이 꺼지길 기다렸다 설치 → 다시 켜는 스크립트를 앱과 떨어진 채로 띄움. 성공 = ""
    private string StartInstall(ReleaseInfo release, string updatesDir, string setupPath)
    {
        try
        {
            string runner = Path.Combine(updatesDir, RunnerName);
            // PowerShell 5.1 은 BOM 이 있어야 한글을 바로 읽음
            File.WriteAllText(runner, RunnerScript, new UTF8Encoding(true));
            string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var args = new StringBuilder();
            args.Append("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ").Append(Quote(runner));
            args.Append($" -WaitPid {Environment.ProcessId}");
            args.Append(" -Setup ").Append(Quote(setupPath));
            args.Append(" -InstallDir ").Append(Quote(installDir));
            args.Append(" -DataDir ").Append(Quote(_dataDir));
            args.Append(" -Version ").Append(Quote(release.VersionText));
            var info = new ProcessStartInfo(powershell, args.ToString())
            {
                UseShellExecute = true,   // 입출력을 이어받지 않아 이 앱이 꺼져도 남음
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = updatesDir,
            };
            using Process? process = Process.Start(info);
            if (null == process)
            {
                return "설치를 시작하지 못했어요";
            }
        }
        catch (Exception ex)
        {
            return $"설치를 시작하지 못했어요 ({ex.Message})";
        }
        return "";
    }

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    // ---------------- 지난 결과 · 정리 ----------------

    // 지난 자동 업데이트 결과 (run-update.ps1 이 씀). 읽고 지움
    private void ReadLastResult()
    {
        string path = Path.Combine(_dataDir, UpdatesDirName, ResultFileName);
        try
        {
            if (false == File.Exists(path))
            {
                return;
            }
            string line = File.ReadLines(path).FirstOrDefault() ?? "";
            File.Delete(path);
            string[] parts = line.Trim().Split('\t');
            string version = 1 <= parts.Length ? parts[0].Trim() : "";
            string code = 2 <= parts.Length ? parts[1].Trim() : "";
            // 코드가 비어도(설치기 끝 코드를 못 읽은 경우) 지금 버전이 그 판 이상이면 설치는 된 것
            bool installed = Version.TryParse(version, out Version? recorded) && CurrentVersion() >= recorded;
            if ("0" == code || installed)
            {
                _log($"자동 업데이트 끝: {version} (지금 {CurrentVersion()})");
                return;
            }
            _log($"자동 업데이트 실패 기록: {version} 코드 {code}");
            SetNote($"지난 업데이트({version})가 끝나지 않았어요 (코드 {code}). 다시 누르거나 데이터 폴더 update.log 를 봐 주세요.", true);
        }
        catch (Exception ex)
        {
            _log($"자동 업데이트 결과 읽기 실패: {ex.Message}");
        }
    }

    // 지난번에 받은 설치 파일 (10분 넘은 것만 — 방금 설치를 끝낸 스크립트가 아직 쥐고 있을 수 있어서)
    private void CleanOldFiles()
    {
        string dir = Path.Combine(_dataDir, UpdatesDirName);
        if (false == Directory.Exists(dir))
        {
            return;
        }
        DateTime limit = DateTime.Now - OldFileAge;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"자동 업데이트 옛 파일 정리 실패: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 다음에 켤 때 다시 정리
        }
    }

    // 설치 진행 스크립트 (데이터 폴더 updates\run-update.ps1 로 써서 실행). 앱과 떨어져 돌아 앱이 꺼져도 남음.
    // Inno Setup: /SILENT = 진행 창만 · /SUPPRESSMSGBOXES = 묻는 창 없이 기본 답 · /DIR = 지난 설치 위치 그대로 (같은 AppId 라 원래도 지난 위치를 쓰지만 못 박아 둠).
    // 설치기는 같은 폴더의 앱 파일만 새 것으로 바꾸고 데이터·설정·godot·ComfyUI 는 건드리지 않음. 끝나면 설치 파일의 [바로 켜기] 는 조용한 모드라 안 뜨므로 여기서 다시 켬
    private const string RunnerScript = """
        param([int]$WaitPid, [string]$Setup, [string]$InstallDir, [string]$DataDir, [string]$Version)
        # AL-ZARD_GameMaker 자동 업데이트 (앱이 만들고 띄움) — 앱이 꺼지길 기다림 → 설치 → 다시 켬
        $log = Join-Path $DataDir 'update.log'
        $updates = Join-Path $DataDir 'updates'
        function Write-UpdateLog([string]$text) {
            try { Add-Content -LiteralPath $log -Value ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] 자동 업데이트: ' + $text) -Encoding UTF8 } catch { }
        }
        try { Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction Stop } catch { }
        Write-UpdateLog ('설치 시작 ' + $Version + ' -> ' + $InstallDir)
        $code = -1
        try {
            $setupLog = Join-Path $updates 'setup.log'
            $setupArgs = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + $InstallDir + '" /LOG="' + $setupLog + '"'
            $process = Start-Process -FilePath $Setup -ArgumentList $setupArgs -PassThru
            $null = $process.Handle   # 핸들을 먼저 잡아야 끝난 뒤 ExitCode 를 읽을 수 있음 (Windows PowerShell 5.1)
            $process.WaitForExit()
            $code = $process.ExitCode
        }
        catch {
            Write-UpdateLog ('설치 파일 실행 실패: ' + $_.Exception.Message)
        }
        if ($null -eq $code) { $code = -1 }
        Write-UpdateLog ('설치 끝 (코드 ' + $code + ')')
        $exe = Join-Path $InstallDir 'GameMaker.exe'
        $wizardDone = $false
        # 조용한 설치가 실패·취소되면 설치기가 지운 앱 파일이 안 돌아와 앱이 없어질 수 있음 → 같은 설치 파일을 화면 있는 설치로 다시 띄워 사람이 끝내게 함
        if ((0 -ne $code -or -not (Test-Path -LiteralPath $exe)) -and (Test-Path -LiteralPath $Setup)) {
            Write-UpdateLog '조용한 설치가 끝나지 않아 설치 화면으로 다시 띄움'
            try {
                $wizard = Start-Process -FilePath $Setup -ArgumentList ('/DIR="' + $InstallDir + '"') -PassThru
                $null = $wizard.Handle
                $wizard.WaitForExit()
                $code = $wizard.ExitCode
                if ($null -eq $code) { $code = -1 }
                Write-UpdateLog ('설치 화면 끝 (코드 ' + $code + ')')
                $wizardDone = (0 -eq $code)   # 설치 화면 마지막의 [켜기] 를 사람이 고름 — 여기서 또 켜면 두 개가 뜸
            }
            catch {
                Write-UpdateLog ('설치 화면 실행 실패: ' + $_.Exception.Message)
            }
        }
        try { Set-Content -LiteralPath (Join-Path $updates 'result.txt') -Value ($Version + "`t" + $code) -Encoding UTF8 } catch { }
        # 성공이든 실패든 다시 켬 (설치 화면으로 끝냈으면 그쪽 [켜기] 에 맡김)
        if ($wizardDone) { }
        elseif (Test-Path -LiteralPath $exe) {
            Start-Process -FilePath $exe -WorkingDirectory $InstallDir
        }
        else {
            Write-UpdateLog ('앱을 다시 켜지 못함 (파일 없음): ' + $exe)
            Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $updates + '"')   # 받은 설치 파일이 있는 폴더를 열어 손으로 다시 깔 수 있게
        }
        """;
}
