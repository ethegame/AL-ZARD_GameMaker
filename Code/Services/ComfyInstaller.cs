using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameMaker.Services;

// 그림 도구(ComfyUI) 포터블 자동 설치 — 화면 [설치 방법] 창의 [설치하기] (Windows 만).
// 자리 = <설치 폴더>\ComfyUI (godot 폴더와 같은 대우: 새 버전 설치 땐 그대로, 제거 땐 같이 지움 — Installer\AL-ZARD_GAMEMAKER.iss)
// 순서: 그래픽카드로 판 고르기(nvidia·amd·intel) → GitHub 최신 릴리스에서 그 판 .7z 받기(ComfyUI.download, SHA256 대조)
//       → 7-Zip 으로 풀기(ComfyUI.partial) → 안의 포터블 폴더를 ComfyUI 로 옮기기 → 모양 확인(ComfyServer.IsValidRoot)
// 이 클래스는 설치 절차만 맡는다. 화면 글·설정 저장·서버 시작은 Views/MainWindow.cs 가 한다.
public sealed class ComfyInstaller : IDisposable
{
    public const string FolderName = "ComfyUI";
    private const string ReleaseApi = "https://api.github.com/repos/Comfy-Org/ComfyUI/releases/latest";
    private const string SevenZipUrl = "https://www.7-zip.org/a/7zr.exe"; // 7-Zip 공식 단독 실행 파일 (.7z 전용, 설치 불필요)
    private const string UserAgent = "AL-ZARD_GameMaker";
    private const long GB = 1024L * 1024 * 1024;
    private const long RefuseFreeBytes = 15 * GB;
    private const long WarnFreeBytes = 30 * GB;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60); // 이만큼 한 바이트도 안 오면 받기 실패
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private int _busy; // 0 = 쉼, 1 = 설치 중 (동시 실행 막기)

    public ComfyInstaller()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static string InstallRoot => Path.Combine(AppContext.BaseDirectory, FolderName);
    private static string PartialDir => Path.Combine(AppContext.BaseDirectory, FolderName + ".partial");
    private static string DownloadDir => Path.Combine(AppContext.BaseDirectory, FolderName + ".download");

    public bool IsBusy => 1 == Volatile.Read(ref _busy);

    // 설치 전 미리 보기. Error 가 있으면 설치 불가(이유), Warning 은 알리기만
    public sealed record Plan(
        string Gpu,
        string Flavor,
        bool IsNvidia,
        string AssetName,
        string Url,
        long Size,
        string? Sha256,
        string Tag,
        long FreeBytes,
        bool AlreadyInstalled,
        string? Warning,
        string? Error);

    // Stage = download · extract · move · cleanup, Percent = 0~100 (모르면 -1)
    public sealed record Progress(string Stage, string Text, double Percent);

    public async Task<Plan> CheckAsync(CancellationToken cancel)
    {
        if (false == OperatingSystem.IsWindows())
        {
            return Fail("자동 설치는 Windows 에서만 돼요.");
        }

        string root = InstallRoot;
        long free = FreeBytes(root);
        if (ComfyServer.IsValidRoot(root))
        {
            return new Plan("", "", true, "", "", 0, null, "", free, true, null, null);
        }

        if (HasUserFiles(root))
        {
            return Fail($"설치 폴더에 ComfyUI 폴더가 이미 있는데 ComfyUI 모양이 아니에요: {root} — 안에 든 파일을 확인해서 다른 곳으로 옮기거나 지운 뒤 다시 눌러 주세요. (갖고 계신 파일일 수 있어 덮어쓰지 않아요)", free);
        }

        List<string> gpus = await ReadGpuNamesAsync(cancel);
        (string gpu, string flavor, bool isKnown) = PickFlavor(gpus);
        bool isNvidia = isKnown && "nvidia" == flavor;
        string assetName = $"ComfyUI_windows_portable_{flavor}.7z";

        string? error = null;
        List<string> warnings = new();
        if (free >= 0 && free < RefuseFreeBytes)
        {
            error = $"디스크 여유가 {ToGb(free)}GB 라 설치할 수 없어요 (15GB 이상 필요 — 프로그램 약 2GB 를 받아 풀면 훨씬 커져요).";
        }
        else if (free >= 0 && free < WarnFreeBytes)
        {
            warnings.Add($"디스크 여유가 {ToGb(free)}GB 예요. 설치는 되지만 그림 모델(한 개당 수 GB)을 받으면 모자랄 수 있어요 (30GB 이상 권장).");
        }

        if (false == isNvidia)
        {
            warnings.Add(false == isKnown
                ? "그래픽카드를 알아보지 못해 가장 흔한 NVIDIA 판으로 골랐어요. NVIDIA 가 아니면 그림 만들기가 느리거나 안 될 수 있어요."
                : "NVIDIA 그래픽카드가 아니라서 그림 만들기가 느리거나 안 될 수 있어요 (설치는 해 볼 수 있어요).");
        }

        string url = "";
        long size = 0;
        string? sha256 = null;
        string tag = "";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            JsonNode? release = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            tag = release?["tag_name"]?.GetValue<string>() ?? "";
            JsonNode? asset = (release?["assets"] as JsonArray)?
                .FirstOrDefault(node => assetName == node?["name"]?.GetValue<string>());
            if (null == asset)
            {
                error ??= $"ComfyUI 최신 판({tag})에 {assetName} 이 없어요. 잠시 뒤 다시 해 보거나 아래 '이미 ComfyUI 를 갖고 있다면' 으로 직접 받아 주세요.";
            }
            else
            {
                url = asset["browser_download_url"]?.GetValue<string>() ?? "";
                size = asset["size"]?.GetValue<long>() ?? 0;
                string? digest = asset["digest"]?.GetValue<string>();
                if (null != digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = digest["sha256:".Length..].ToLowerInvariant();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (cancel.IsCancellationRequested)
            {
                throw;
            }

            error ??= $"ComfyUI 배포 정보를 가져오지 못했어요 (인터넷 연결을 확인해 주세요): {ex.Message}";
        }

        string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        return new Plan(gpu, flavor, isNvidia, assetName, url, size, sha256, tag, free, false, warning, error);

        Plan Fail(string why, long freeBytes = -1)
        {
            return new Plan("", "", false, "", "", 0, null, "", freeBytes, false, null, why);
        }
    }

    // 성공 = null (InstallRoot 가 IsValidRoot), 실패 = 이유. 취소 = OperationCanceledException. 실패·취소 땐 반쯤 된 것(partial·download)을 지움
    public async Task<string?> InstallAsync(Plan plan, Action<Progress> report, CancellationToken cancel)
    {
        if (0 != Interlocked.Exchange(ref _busy, 1))
        {
            return "이미 설치 중이에요.";
        }

        try
        {
            if (plan.AlreadyInstalled && ComfyServer.IsValidRoot(InstallRoot))
            {
                return null;
            }

            if (null != plan.Error)
            {
                return plan.Error;
            }

            if (HasUserFiles(InstallRoot))
            {
                return $"설치 폴더에 ComfyUI 폴더가 이미 있는데 ComfyUI 모양이 아니에요: {InstallRoot} — 덮어쓰지 않았어요.";
            }

            // 지난번에 앱을 끄는 바람에 남은 반쪽부터 치움
            report(new Progress("cleanup", "지난번에 남은 파일을 정리하는 중…", -1));
            await DeleteLeftoversAsync();
            DeleteEmptyDir(InstallRoot);

            bool ok = false;
            try
            {
                Directory.CreateDirectory(DownloadDir);
                string archive = Path.Combine(DownloadDir, plan.AssetName);
                await DownloadAsync(plan.Url, archive, plan.Size, plan.Sha256, "download", $"ComfyUI ({plan.Flavor}) 받는 중", report, cancel);

                string sevenZip = FindInstalledSevenZip() ?? "";
                if (0 == sevenZip.Length)
                {
                    sevenZip = Path.Combine(DownloadDir, "7zr.exe");
                    await DownloadAsync(SevenZipUrl, sevenZip, 0, null, "extract", "압축 푸는 도구(7-Zip) 받는 중", report, cancel);
                }

                Directory.CreateDirectory(PartialDir);
                await ExtractAsync(sevenZip, archive, PartialDir, report, cancel);

                // 받은 압축 파일은 바로 지움 (2GB — 옮기기 전에 자리부터 비움)
                report(new Progress("move", "제자리에 놓는 중…", -1));
                await Task.Run(() => DeleteDir(DownloadDir));
                string? extracted = FindPortableRoot(PartialDir);
                if (null == extracted)
                {
                    return "압축은 풀었는데 안에서 ComfyUI(python_embeded 와 ComfyUI 폴더)를 찾지 못했어요. ComfyUI 배포 모양이 바뀌었을 수 있어요 — 아래 '이미 ComfyUI 를 갖고 있다면' 으로 직접 받아 주세요.";
                }

                await MoveWithRetryAsync(extracted, InstallRoot);
                if (false == ComfyServer.IsValidRoot(InstallRoot))
                {
                    return $"옮긴 뒤 ComfyUI 모양 확인에 실패했어요: {InstallRoot}";
                }

                ok = true;
                return null;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return ex.Message;
            }
            finally
            {
                // 성공이든 실패든 받은 파일·반쪽 폴더는 남기지 않음. 실패면 옮기다 만 ComfyUI 도 (모양이 아니면) 지움
                report(new Progress("cleanup", "정리하는 중…", -1));
                await DeleteLeftoversAsync();
                if (false == ok && Directory.Exists(InstallRoot) && false == ComfyServer.IsValidRoot(InstallRoot) && false == plan.AlreadyInstalled)
                {
                    await Task.Run(() => DeleteDir(InstallRoot));
                }
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task DownloadAsync(string url, string path, long expectedSize, string? sha256, string stage, string label, Action<Progress> report, CancellationToken cancel)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? expectedSize;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (FileStream target = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                byte[] buffer = new byte[1 << 20];
                long received = 0;
                Stopwatch clock = Stopwatch.StartNew();
                TimeSpan nextReport = TimeSpan.Zero;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read = await source.ReadAsync(buffer, stall.Token);
                    if (0 == read)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    received += read;
                    if (clock.Elapsed >= nextReport)
                    {
                        nextReport = clock.Elapsed + ReportInterval;
                        double percent = total > 0 ? 100.0 * received / total : -1;
                        string format = total > 0 && total < 100 * 1024 * 1024 ? "N1" : "N0"; // 7zr.exe(0.6MB) 같은 작은 파일은 소수 한 자리
                        string amount = total > 0
                            ? $"{ToMb(received).ToString(format)} / {ToMb(total).ToString(format)} MB ({percent:0}%)"
                            : $"{ToMb(received).ToString(format)} MB";
                        report(new Progress(stage, $"{label}… {amount}", percent));
                    }
                }

                if (expectedSize > 0 && received != expectedSize)
                {
                    throw new IOException($"받은 크기가 달라요 ({received:N0} / {expectedSize:N0} 바이트). 다시 눌러 주세요.");
                }
            }

            if (null != sha256)
            {
                string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != sha256)
                {
                    throw new IOException("받은 파일이 원본과 달라요 (SHA256 불일치). 다시 눌러 주세요.");
                }
            }
        }
        catch (OperationCanceledException) when (false == cancel.IsCancellationRequested)
        {
            throw new IOException($"인터넷이 {StallTimeout.TotalSeconds:0}초 동안 멈춰서 받기를 그만뒀어요. 연결을 확인하고 다시 눌러 주세요.");
        }
    }

    // 7-Zip: x = 폴더 구조대로 풀기, -bsp1 = 진행률(%)을 표준 출력으로 (줄바꿈 대신 \b 로 덮어쓰므로 글자 단위로 읽음)
    private static async Task ExtractAsync(string sevenZip, string archive, string destination, Action<Progress> report, CancellationToken cancel)
    {
        ProcessStartInfo info = new()
        {
            FileName = sevenZip,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("x");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-o" + destination);
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add("-bsp1");
        info.ArgumentList.Add("-bso0");
        info.ArgumentList.Add("-bse2");

        using Process process = new() { StartInfo = info };
        process.Start();
        using CancellationTokenRegistration kill = cancel.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 이미 끝남
            }
        });

        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Regex percentPattern = new(@"(\d{1,3})%");
        char[] buffer = new char[4096];
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.Zero;
        report(new Progress("extract", "압축 푸는 중… 0%", 0));
        while (true)
        {
            int read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None);
            if (0 == read)
            {
                break;
            }

            MatchCollection matches = percentPattern.Matches(new string(buffer, 0, read));
            if (matches.Count > 0 && clock.Elapsed >= nextReport)
            {
                nextReport = clock.Elapsed + ReportInterval;
                int percent = Math.Clamp(int.Parse(matches[^1].Groups[1].Value), 0, 100);
                report(new Progress("extract", $"압축 푸는 중… {percent}% (몇 분 걸려요)", percent));
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancel.ThrowIfCancellationRequested();
        string errorText = (await errors).Trim();
        // 0 = 성공, 1 = 경고(일부 파일 못 씀 등) — 경고도 모양 확인(IsValidRoot)에서 걸러지지만 여기서 실패로 봄
        if (0 != process.ExitCode)
        {
            string why = 2 == process.ExitCode && errorText.Contains("space", StringComparison.OrdinalIgnoreCase) ? "디스크 공간이 모자라요" : errorText;
            throw new IOException($"압축을 풀지 못했어요 (코드 {process.ExitCode}). {why}".Trim());
        }
    }

    // 압축 안의 최상위 폴더(ComfyUI_windows_portable 류) 또는 그 자리 자체
    private static string? FindPortableRoot(string dir)
    {
        if (ComfyServer.IsValidRoot(dir))
        {
            return dir;
        }

        return Directory.GetDirectories(dir).FirstOrDefault(ComfyServer.IsValidRoot);
    }

    // 백신이 방금 푼 파일을 잡고 있으면 이름 바꾸기가 잠깐 실패할 수 있어 몇 번 다시
    private static async Task MoveWithRetryAsync(string source, string target)
    {
        for (int attempt = 1; ; ++attempt)
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    // 7-Zip 이 이미 깔려 있으면 그걸 씀 (Program Files 기본 자리)
    private static string? FindInstalledSevenZip()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        };
        return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(path));
    }

    // 그래픽카드 이름들 (WMI Win32_VideoController — PowerShell 한 번). 못 읽으면 빈 목록
    private static async Task<List<string>> ReadGpuNamesAsync(CancellationToken cancel)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo info = new()
        {
            FileName = File.Exists(powershell) ? powershell : "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; (Get-CimInstance Win32_VideoController).Name");
        try
        {
            using Process process = new() { StartInfo = info };
            process.Start();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            cancel.ThrowIfCancellationRequested();
            return new List<string>();
        }
    }

    // NVIDIA > AMD > Intel 순 (내장 AMD·Intel + 외장 NVIDIA 가 같이 있는 PC 가 흔해서). 하나도 못 알아보면 가장 흔한 nvidia 판 + IsKnown false
    private static (string Gpu, string Flavor, bool IsKnown) PickFlavor(List<string> names)
    {
        (string Key, string Flavor)[] order =
        {
            ("nvidia", "nvidia"),
            ("geforce", "nvidia"),
            ("amd", "amd"),
            ("radeon", "amd"),
            ("intel", "intel"),
        };
        foreach ((string key, string flavor) in order)
        {
            string? match = names.FirstOrDefault(name => name.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (null != match)
            {
                return (match, flavor, true);
            }
        }

        return (0 == names.Count ? "알 수 없음" : string.Join(", ", names), "nvidia", false);
    }

    private static long FreeBytes(string path)
    {
        try
        {
            string? drive = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(drive) ? -1 : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    // 폴더가 있고 안에 뭔가 들어 있음 (= 사용자 파일일 수 있어 덮지 않음)
    private static bool HasUserFiles(string dir)
    {
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    private static void DeleteEmptyDir(string dir)
    {
        if (Directory.Exists(dir) && false == Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }

    private static Task DeleteLeftoversAsync()
    {
        return Task.Run(() =>
        {
            DeleteDir(PartialDir);
            DeleteDir(DownloadDir);
        });
    }

    // 지우기 실패(파일 잠김 등)는 넘어감 — 다음 설치 시작 때 다시 지움
    private static void DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // 읽기 전용 파일도 지우게
                }

                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다음에 다시
        }
    }

    private static double ToMb(long bytes)
    {
        return bytes / (1024.0 * 1024.0);
    }

    public static string ToGb(long bytes)
    {
        return (bytes / (double)GB).ToString("0.0");
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
