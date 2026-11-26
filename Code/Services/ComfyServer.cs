using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace GameMaker.Services;

// ComfyUI 포터블 서버 프로세스. 이미 떠 있으면 붙기만 하고(소유 X), 없으면 직접 띄운다(소유 O).
// 소유한 프로세스만 Stop 대상 — 사용자가 따로 띄운 ComfyUI 는 건드리지 않는다.
// Windows = 포터블(python_embeded\python.exe + ComfyUI\main.py), 맥 = 직접 설치(main.py 옆 venv/.venv 의 python, 없으면 python3)
public sealed class ComfyServer : IDisposable
{
    private readonly AppConfig _config;
    private readonly string _logPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _process;
    private StreamWriter? _log;

    public ComfyServer(AppConfig config, string dataDir)
    {
        _config = config;
        string logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "comfyui.log");
    }

    public string LogPath => _logPath;
    public bool IsOwned => null != _process && false == _process.HasExited;

    public async Task<bool> IsAliveAsync()
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(_config.ComfyUrl.TrimEnd('/') + "/system_stats");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 실패 사유는 error 로. 기동 후 응답까지 최대 timeout 대기 (첫 기동은 커스텀 노드 로드로 수십 초 걸릴 수 있음)
    public async Task<string?> StartAsync(TimeSpan timeout)
    {
        if (await IsAliveAsync())
        {
            return null;
        }

        string root = _config.ResolveComfyRoot();
        if (false == IsValidRoot(root))
        {
            return 0 == root.Length
                ? "그림 도구(ComfyUI) 가 없습니다 — 리소스 칸 [설치 방법] 창의 [설치하기] 를 눌러 주세요."
                : $"ComfyUI 를 찾지 못했습니다: {root} — 리소스 칸의 [설치 방법] 에서 폴더를 다시 정해 주세요.";
        }

        (string python, string main) = FindComfy(root);

        Uri uri = new(_config.ComfyUrl);
        ProcessStartInfo info = new()
        {
            FileName = python,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // run_nvidia_gpu.bat 과 같은 인자 + 브라우저 자동 열기 끔 + 주소 고정
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add(main);
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("--windows-standalone-build");
        }

        info.ArgumentList.Add("--disable-auto-launch");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add(uri.Host);
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(uri.Port.ToString());
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        _log?.Dispose();
        _log = new StreamWriter(_logPath, append: false) { AutoFlush = true };
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                return $"ComfyUI 가 바로 종료됐습니다 (exit {_process.ExitCode}). 로그: {_logPath}";
            }

            if (await IsAliveAsync())
            {
                return null;
            }

            await Task.Delay(1000);
        }

        return $"ComfyUI 응답 대기 시간 초과. 로그: {_logPath}";
    }

    // 설정된 ComfyUI 폴더가 쓸 수 있는 모양인지 (설정 없음 = false → 서버 자동 시작을 시도하지 않음)
    public bool IsInstalled => IsValidRoot(_config.ResolveComfyRoot());

    // 포터블 모양 (python_embeded\python.exe + ComfyUI\main.py) 이 있는 폴더인지 — 화면에서 폴더를 정할 때도 씀
    public static bool IsValidRoot(string root)
    {
        if (0 == root.Length || false == Directory.Exists(root))
        {
            return false;
        }

        (string python, string main) = FindComfy(root);
        return File.Exists(python) && File.Exists(main);
    }

    // (python, main.py). 못 찾으면 없는 경로 그대로 (부른 쪽이 File.Exists 로 판정)
    private static (string Python, string Main) FindComfy(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return (Path.Combine(root, "python_embeded", "python.exe"), Path.Combine(root, "ComfyUI", "main.py"));
        }

        string main = Path.Combine(root, "ComfyUI", "main.py");
        if (false == File.Exists(main))
        {
            main = Path.Combine(root, "main.py");
        }

        string mainDir = Path.GetDirectoryName(main) ?? root;
        string[] candidates =
        {
            Path.Combine(mainDir, "venv", "bin", "python"),
            Path.Combine(mainDir, ".venv", "bin", "python"),
            Path.Combine(root, "venv", "bin", "python"),
            Path.Combine(root, ".venv", "bin", "python"),
            "/opt/homebrew/bin/python3",
            "/usr/local/bin/python3",
            "/usr/bin/python3",
        };
        string python = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        return (python, main);
    }

    public void Stop()
    {
        if (false == IsOwned)
        {
            return;
        }

        try
        {
            _process!.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // 이미 종료됨
        }

        _process = null;
    }

    private void WriteLog(string? line)
    {
        if (null == line)
        {
            return;
        }

        lock (_logPath)
        {
            _log?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        if (_config.StopServerOnExit)
        {
            Stop();
        }

        _log?.Dispose();
        _http.Dispose();
    }
}
