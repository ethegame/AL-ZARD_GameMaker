using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameMaker.Core;
using GameMaker.Views;
using Photino.NET;

namespace GameMaker;

// 앱 시작점 (2026-11-20 — WPF App.xaml + WebView2 컨트롤 대신 Photino 창 하나, Windows·맥 공용).
// 여기는 창의 수명(만들기 → 화면 준비 → 닫기)만, 기능 연결은 Views/MainWindow 가 한다
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var host = new AppHost();
        return host.Run();
    }
}

internal sealed class AppHost
{
    private const string Title = "AL-ZARD_GameMaker";
    private const int StartWidth = 1400;
    private const int StartHeight = 860;
    private const int MinWidth = 900;
    private const int MinHeight = 600;
    // Windows: 화면 주소 = https://gamemaker.local, 웹 저장소(작업대 열림·워크플로 입력값) = %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\WebView2.
    // http 도 가로채서 404 로 — 화면이 바깥 페이지로 바뀌지 않게 (WebView2 NavigationStarting 막기 자리). 맥 WKWebView 는 전용 스킴
    private static readonly string[] WindowsSchemes = { "https", "http" };
    private const string MacScheme = "app";

    private readonly string _appDataDir = GameMaker.Services.AppConfig.LocalAppDir;
    private readonly WebFiles _files = new(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
    private Dispatcher _dispatcher = null!;
    private PhotinoWindow _photino = null!;
    private HostWindow _host = null!;
    private MainWindow? _main;
    private int _isShutDown;

    public int Run()
    {
        string logPath = Path.Combine(_appDataDir, "app.log");
        try
        {
            MacPath.RefreshProcessPath();
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            _dispatcher = Dispatcher.CreateForCurrentThread();
            string scheme = OperatingSystem.IsWindows() ? WindowsSchemes[0] : MacScheme;
            string origin = $"{scheme}://{WebFiles.HostName}";
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _photino = BuildWindow();
            _host = new HostWindow(_photino, _dispatcher, _files, origin, token, logPath);
            _main = new MainWindow(_host);
            string os = OperatingSystem.IsWindows() ? "win" : "mac";
            _photino.Load(new Uri($"{origin}/index.html?os={os}&tok={token}"));
            _photino.WaitForClose();
        }
        catch (Exception ex)
        {
            // 창이 뜨기 전 실패 (설정·폴더·웹뷰 없음 등) — 기록 남기고, 아무 말 없이 꺼지지 않게 알림 창 한 번
            try
            {
                File.AppendAllText(EnsureDir(logPath), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 시작 실패: {ex}{Environment.NewLine}");
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"app.log 기록 실패: {logEx.Message}");
            }

            ShowStartFailure($"{Title} 를 시작하지 못했습니다 — 기록: {logPath}\n\n{ex.Message}");
            return 1;
        }

        Shutdown();
        return 0;
    }

    // 창이 없을 때 쓰는 Windows 기본 알림 창 (맥은 기록만)
    private static void ShowStartFailure(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, text, Title, MbIconError);
        }
    }

    private const uint MbIconError = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private PhotinoWindow BuildWindow()
    {
        var window = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle(Title)
            .SetUseOsDefaultSize(false)
            .SetSize(StartWidth, StartHeight)
            .SetMinSize(MinWidth, MinHeight)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(false)
            .SetGrantBrowserPermissions(false)
            .SetJavascriptClipboardAccessEnabled(true)
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .RegisterWindowCreatedHandler(OnWindowCreated)
            .RegisterWindowClosingHandler(OnWindowClosing)
            .RegisterMinimizedHandler(OnMinimized)
            .RegisterRestoredHandler(OnRestored)
            .RegisterMaximizedHandler(OnRestored);
        if (OperatingSystem.IsWindows())
        {
            foreach (string scheme in WindowsSchemes)
            {
                window.RegisterCustomSchemeHandler(scheme, OnScheme);
            }

            // WebView2 사용자 폴더 (웹 저장소)
            window.SetTemporaryFilesPath(Path.Combine(_appDataDir, "WebView2"));
        }
        else
        {
            window.RegisterCustomSchemeHandler(MacScheme, OnScheme);
        }

        window.Center();
        return window;
    }

    // 창이 만들어짐 (메시지 루프 직전, UI 스레드) — 이때부터 Dispatcher 가 돈다
    private void OnWindowCreated(object? sender, EventArgs e)
    {
        _dispatcher.Attach(InvokeOnWindow, OnUiException);
    }

    private void InvokeOnWindow(Action action)
    {
        _photino.Invoke(action);
    }

    // WPF 판은 UI 예외면 앱이 죽었다. 지금은 기록만 하고 계속 (터미널 Claude 세션이 같이 죽지 않게)
    private void OnUiException(Exception ex)
    {
        _host.Log($"UI 예외: {ex}");
    }

    // true = 닫기 취소. 맥은 마지막 창이 닫히면 WaitForClose 뒤가 안 돌 수 있어 여기서 정리
    private bool OnWindowClosing(object sender, EventArgs e)
    {
        Shutdown();
        return false;
    }

    // 최소화 중엔 GPU 미터 측정을 쉰다 (MainWindow.Gpu). 창이 다 만들어지기 전 알림은 버림
    private void OnMinimized(object? sender, EventArgs e)
    {
        _host?.SetMinimized(true);
    }

    private void OnRestored(object? sender, EventArgs e)
    {
        _host?.SetMinimized(false);
    }

    // 맥 Cmd+Q 등 창 닫기 없이 끝날 때의 마지막 정리
    private void OnProcessExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private void Shutdown()
    {
        if (1 == Interlocked.Exchange(ref _isShutDown, 1))
        {
            return;
        }

        if (null == _main)
        {
            return;
        }

        _host.MarkClosing();
        try
        {
            _main.Dispose();
        }
        catch (Exception ex)
        {
            _host.Log($"닫기 정리 실패: {ex}");
        }

        _dispatcher.Detach();
    }

    private void OnWebMessage(object? sender, string message)
    {
        _host.Receive(message);
    }

    private Stream OnScheme(object sender, string scheme, string url, out string contentType)
    {
        return _files.Serve(url, out contentType);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Debug.WriteLine($"관찰 안 된 작업 예외: {e.Exception.Message}");
        _host?.Log($"작업 예외: {e.Exception}");
        e.SetObserved();
    }
}
