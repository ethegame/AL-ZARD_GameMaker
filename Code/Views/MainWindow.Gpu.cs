using GameMaker.Services;

namespace GameMaker.Views;

// 맨 위 프로젝트 줄 오른쪽 끝 GPU · VRAM 미터 (화면 = wwwroot/gpu.js). 측정 = Services/GpuMonitor.cs (Windows 성능 카운터).
// 자리 이유: AI 칸 일이 아니라 PC 전체 — 게임 플레이·그림 도구(ComfyUI)가 그래픽카드를 얼마나 쓰는지 한눈에.
// 맥은 측정 길이 없어 화면이 미터를 숨긴다 (gpu 메시지를 안 보냄).
// 3초 · 최소화 중 멈춤 = 친구판 GpuFeature 와 같게 (11/26 — 2초·상시로 옮겨 와 점검기 빠른 주기 1,800회/시에 걸림.
// PDH \GPU Engine(*) 수집은 프로세스·엔진 수만큼 인스턴스를 읽어 가볍지 않고, 미터 숫자는 3초 늦어도 차이가 작다)
public partial class MainWindow
{
    private static readonly TimeSpan GpuInterval = TimeSpan.FromSeconds(3);

    private GpuMonitor? _gpu;

    private void InitializeGpu()
    {
        if (OperatingSystem.IsWindows())
        {
            StartGpu();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartGpu()
    {
        var monitor = new GpuMonitor();
        bool started = monitor.Start(GpuInterval, sample => Dispatcher.BeginInvoke(() => PostGpu(sample)));
        if (false == started)
        {
            _host.Log($"GPU 미터 끔: {monitor.Error}");
            monitor.Dispose();
            return;
        }

        _gpu = monitor;
        monitor.SetPaused(_host.IsMinimized);
        _host.MinimizedChanged += OnGpuMinimizedChanged;
    }

    private void OnGpuMinimizedChanged(bool minimized)
    {
        if (OperatingSystem.IsWindows())
        {
            _gpu?.SetPaused(minimized);
        }
    }

    private void DisposeGpu()
    {
        _host.MinimizedChanged -= OnGpuMinimizedChanged;
        if (OperatingSystem.IsWindows())
        {
            _gpu?.Dispose();
        }

        _gpu = null;
    }

    private void PostGpu(GpuSample sample)
    {
        Post(new
        {
            t = "gpu",
            percent = sample.Total,
            name = sample.AdapterName,
            used = sample.DedicatedBytes,
            total = sample.DedicatedTotalBytes,
        });
    }
}
