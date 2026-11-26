using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Photino.NET;

namespace GameMaker.Core;

// 앱 창 하나 (Photino: Windows = WebView2, 맥 = WKWebView) — MainWindow 가 쓰는 것만 둔다.
// 보내기(Send) · 받기(MessageReceived) · 닫기 · 파일 고르기 창 · 화면 파일 공급(Files) · 오류 기록(Log).
// 받은 메시지는 바로 처리하지 않고 UI 줄(Dispatcher)에 세운다 — 웹뷰 이벤트 안에서 파일 창을 띄우면 멈출 수 있어서.
// 출처 확인: 시작할 때마다 만든 난수 토큰을 화면 주소(?tok=)로 넘기고 bridge.js 가 모든 메시지에 붙인다 →
// 토큰이 없거나 다른 메시지는 버린다 (바깥 페이지로 화면이 바뀌어도 그 페이지가 터미널에 글을 칠 수 없게)
public sealed class HostWindow
{
    private const string TokenField = "tok";

    private readonly PhotinoWindow _window;
    private readonly string _logPath;
    private bool _isPageReady;
    private bool _isClosing;

    public HostWindow(PhotinoWindow window, Dispatcher dispatcher, WebFiles files, string origin, string token, string logPath)
    {
        _window = window;
        Dispatcher = dispatcher;
        Files = files;
        Origin = origin;
        Token = token;
        _logPath = logPath;
    }

    public Dispatcher Dispatcher { get; }
    public WebFiles Files { get; }
    public string Origin { get; }   // 화면 주소 앞부분 (https://gamemaker.local · 맥 app://gamemaker.local)
    public string Token { get; }

    // 화면에서 온 메시지 (UI 스레드, 토큰 확인 뒤)
    public event Action<string>? MessageReceived;

    // 창 최소화 들어감(true)·나옴(false) — 같은 값이 거듭 와도 한 번만. 맥은 복원 알림이 없어 Windows 일에만 쓴다 (GPU 미터)
    public event Action<bool>? MinimizedChanged;

    public bool IsMinimized { get; private set; }

    // Photino 최소화·복원·최대화 알림 (Program.AppHost, UI 스레드)
    internal void SetMinimized(bool minimized)
    {
        if (minimized == IsMinimized)
        {
            return;
        }

        IsMinimized = minimized;
        MinimizedChanged?.Invoke(minimized);
    }

    // Photino 가 UI 스레드에서 부름 — 줄에 세우고 바로 돌아감
    internal void Receive(string json)
    {
        if (false == HasToken(json))
        {
            return;
        }

        // 화면이 메시지를 보냈다 = 받을 준비도 됨 (그 전에 보낸 것은 버림 — 예전 CoreWebView2 null 때와 같음)
        _isPageReady = true;
        var item = new Incoming(this, json);
        Dispatcher.BeginInvoke(item.Run);
    }

    private bool HasToken(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (JsonValueKind.Object != doc.RootElement.ValueKind)
            {
                return false;
            }

            if (false == doc.RootElement.TryGetProperty(TokenField, out JsonElement value) || JsonValueKind.String != value.ValueKind)
            {
                return false;
            }

            return string.Equals(value.GetString(), Token, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Send(string json)
    {
        if (false == _isPageReady || true == _isClosing)
        {
            return;
        }

        _window.SendWebMessage(json);
    }

    // 창 닫기 (닫기 정리는 Program 의 WindowClosing 에서)
    public void Close()
    {
        if (true == _isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(CloseNow);
    }

    // 파일 고르기 (취소 = 빈 배열). extensions = ".json" 모양
    public string[] ShowOpenFile(string title, bool multiSelect, string filterName, IEnumerable<string> extensions)
    {
        string[] patterns = extensions.Select(ext => "*" + ext).ToArray();
        string[]? picked = _window.ShowOpenFile(title, null, multiSelect, new[] { (filterName, patterns) });
        return picked?.Where(path => false == string.IsNullOrEmpty(path)).ToArray() ?? Array.Empty<string>();
    }

    // 폴더 고르기 (취소 = 빈 배열)
    public string[] ShowOpenFolder(string title)
    {
        string[]? picked = _window.ShowOpenFolder(title, null, false);
        return picked?.Where(path => false == string.IsNullOrEmpty(path)).ToArray() ?? Array.Empty<string>();
    }

    // <LocalAppData>/AL-ZARD_GAMEMAKER/app.log 한 줄 (UI 예외·화면 스크립트 오류)
    public void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"app.log 기록 실패: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"app.log 기록 실패: {ex.Message}");
        }
    }

    internal void MarkClosing()
    {
        _isClosing = true;
    }

    private void CloseNow()
    {
        if (true == _isClosing)
        {
            return;
        }

        _window.Close();
    }

    private void Dispatch(string json)
    {
        MessageReceived?.Invoke(json);
    }

    // 화면에서 온 메시지 하나 (줄에 세울 때 글자를 들고 있음)
    private sealed class Incoming
    {
        private readonly HostWindow _owner;
        private readonly string _json;

        public Incoming(HostWindow owner, string json)
        {
            _owner = owner;
            _json = json;
        }

        public void Run()
        {
            _owner.Dispatch(_json);
        }
    }
}
