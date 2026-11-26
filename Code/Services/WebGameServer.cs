using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GameMaker.Services;

// 웹 게임 폴더를 127.0.0.1 포트로 내주는 작은 정적 파일 서버.
// file:// 에서는 ES 모듈·fetch 가 막혀서 필요하다. [▶ 플레이] 를 누를 때 켜고 앱이 끝날 때 끈다.
// 게임 폴더 밖 경로·숨김 점 경로·링크 폴더는 거부한다 (읽기 전용 GET/HEAD 만).
public sealed class WebGameServer : IDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".wasm"] = "application/wasm",
    };

    private readonly object _lock = new();
    private HttpListener? _listener;
    private string _root = "";
    private int _port;

    // 프로젝트마다 정한 고정 포트 범위 — 브라우저 저장(localStorage·IndexedDB)은 출처(포트)별이라, 포트가 바뀌면 저장이 갈린다
    private const int PortBase = 49200;
    private const int PortCount = 800;
    private const int PortTries = 8;

    // true 면 고정 포트가 모두 사용 중이어서 임의 포트로 열렸다 (이번엔 브라우저 저장이 따로 보일 수 있음)
    public bool UsedFallbackPort { get; private set; }

    // 게임 폴더를 내주고 그 폴더의 주소(끝에 /)를 돌려줌. 같은 폴더면 서버를 다시 켜지 않는다
    public string Serve(string gameRoot, string projectId)
    {
        lock (_lock)
        {
            string full = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (null == _listener || false == string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
            {
                StopListener();
                _root = full;
                _port = StartListener(projectId);
            }

            return $"http://127.0.0.1:{_port}/";
        }
    }

    // 프로젝트 id 로 정한 포트부터 차례로 시도하고, 다 사용 중일 때만 임의 포트로 listener 를 켠다
    private int StartListener(string projectId)
    {
        UsedFallbackPort = false;
        int start = StablePortOffset(projectId);
        for (int i = 0; i < PortTries; i++)
        {
            int port = PortBase + (start + i) % PortCount;
            HttpListener? listener = TryStart(port);
            if (null != listener)
            {
                return Run(listener, port);
            }
        }

        UsedFallbackPort = true;
        for (int attempt = 0; ; attempt++)
        {
            int port = FindFreePort();
            HttpListener? listener = TryStart(port);
            if (null != listener)
            {
                return Run(listener, port);
            }

            if (attempt >= 5)
            {
                throw new IOException("웹 게임 서버를 열 포트를 찾지 못했습니다");
            }
        }
    }

    // 실행마다 같아야 해서 string.GetHashCode(실행마다 달라짐) 대신 SHA256 앞 4바이트
    public static int StablePortOffset(string projectId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId));
        return (int)(BitConverter.ToUInt32(hash, 0) % PortCount);
    }

    private static HttpListener? TryStart(int port)
    {
        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException)
        {
            listener.Close();
            return null;
        }
    }

    private int Run(HttpListener listener, int port)
    {
        _listener = listener;
        string root = _root;
        _ = Task.Run(() => Loop(listener, root, port));
        return port;
    }

    private static int FindFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Loop(HttpListener listener, string root, int port)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context, root, port));
        }
    }

    private static void Handle(HttpListenerContext context, string root, int port)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            bool hostOk = request.Headers["Host"] == $"127.0.0.1:{port}"; // 다른 호스트 이름으로 들어오는 요청(DNS 리바인딩) 거부
            bool methodOk = request.HttpMethod is "GET" or "HEAD";
            string? file = hostOk && methodOk ? ResolveFile(root, request.Url?.AbsolutePath ?? "/") : null;
            if (null == file)
            {
                Reply(response, hostOk && methodOk ? 404 : 403, "text/plain; charset=utf-8", "not found"u8.ToArray(), headOnly: false);
                return;
            }

            string type = ContentTypes.TryGetValue(Path.GetExtension(file), out string? known) ? known : "application/octet-stream";
            Reply(response, 200, type, File.ReadAllBytes(file), headOnly: "HEAD" == request.HttpMethod);
        }
        catch (Exception)
        {
            // 어떤 예외든(NotSupported·Argument 포함) 연결이 매달리지 않게 응답을 닫는다
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
                // 이미 끊긴 연결
            }
        }
    }

    private static void Reply(HttpListenerResponse response, int status, string type, byte[] body, bool headOnly)
    {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        if (false == headOnly)
        {
            response.OutputStream.Write(body, 0, body.Length);
        }

        response.Close();
    }

    // URL 경로 → 게임 폴더 안의 실제 파일. 폴더 밖·숨김(.으로 시작)·링크·없는 파일이면 null
    public static string? ResolveFile(string root, string urlPath)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(urlPath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (decoded.Contains('\\') || decoded.Contains('\0') || decoded.Contains(':'))
        {
            return null;
        }

        string[] segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.StartsWith('.')))
        {
            return null; // .. 와 숨김(.git 등) 모두
        }

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(new[] { rootFull }.Concat(segments).ToArray()));
        if (decoded.EndsWith('/') || string.Equals(target, rootFull, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target))
        {
            target = Path.Combine(target, "index.html");
        }

        bool inside = target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (false == inside || false == File.Exists(target))
        {
            return null;
        }

        // 게임 폴더 안의 링크(정션·심볼릭)는 밖으로 나갈 수 있어 거부
        for (string? path = target; null != path && path.Length > rootFull.Length; path = Path.GetDirectoryName(path))
        {
            if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
        }

        return target;
    }

    private void StopListener()
    {
        HttpListener? listener = _listener;
        _listener = null;
        try
        {
            listener?.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // 이미 닫힘
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopListener();
        }
    }
}
