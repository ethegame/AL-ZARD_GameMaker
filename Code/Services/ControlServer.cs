using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameMaker.Services;

// 터미널 claude 가 curl 로 앱을 조작하는 로컬 창구 (이 PC 안에서만).
//   POST /run        {"workflow": "텍스트로스프라이트", "values": {nodeId: {input: value}}} → {"run": id}  (바로 반환, 실행은 화면에서 진행)
//   POST /draw       {"prompt": "…", "name": "파일 이름", "dir": "assets/sprites", "transparent": true} → {"run": id}  (그림 = GPT, Views/MainWindow.Draw.cs. GPT 를 못 쓰면 503)
//   GET  /run/<id>   → {"state": "running"|"done"|"failed", "error", "files": [절대 경로]}  (/run · /draw 공용)
//   GET  /status     → {"server": bool, "busy": bool, "workflows": [...]}
// 처리 함수는 MainWindow 가 넘긴다 (UI 스레드 전환은 그쪽 책임).
// 127.0.0.1 에만 TcpListener 로 듣는다 (HttpListener/http.sys 는 모든 주소에 열고 Host 머리로 거르는 방식이라 쓰지 않음).
// 포트 0 = 운영체제가 빈 포트를 줌 → Port 로 알리고 claude_context.md {ControlPort} 에 넣는다 (고정 포트를 다른 프로그램과 다투지 않게).
// 웹 페이지가 이 창구를 부르지 못하게: Origin 머리가 있으면 거부(브라우저만 붙임), POST 는 Content-Type application/json 만, Host 는 127.0.0.1·localhost 만.
public sealed class ControlServer : IDisposable
{
    public delegate Task<(int Status, object Body)> Handler(string method, string path, JsonNode? body);

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TcpListener _listener;
    private readonly Handler _handler;
    private readonly CancellationTokenSource _stop = new();

    public ControlServer(int port, Handler handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, Math.Max(0, port));
    }

    // 실제로 듣는 포트 (Start 성공 뒤)
    public int Port { get; private set; }

    public string? Start()
    {
        try
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            return $"제어 포트를 열지 못했습니다 ({ex.Message}) — 오른쪽 AI 가 그림 만들기를 요청할 수 없습니다. 앱을 다시 켜 보세요.";
        }

        _ = Task.Run(LoopAsync);
        return null;
    }

    private async Task LoopAsync()
    {
        while (false == _stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return; // 종료
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                NetworkStream stream = client.GetStream();
                (int status, object body) = await HandleAsync(stream, timeout.Token);
                await WriteResponseAsync(stream, status, body, timeout.Token);
            }
            catch (Exception)
            {
                // 연결이 끊겼거나 시간 초과 — 답할 곳이 없음
            }
        }
    }

    private async Task<(int Status, object Body)> HandleAsync(NetworkStream stream, CancellationToken token)
    {
        Request? request = await ReadRequestAsync(stream, token);
        if (null == request)
        {
            return (400, new { error = "요청을 읽지 못했습니다." });
        }

        if (request.Headers.ContainsKey("origin"))
        {
            return (403, new { error = "웹 페이지에서 온 요청은 받지 않습니다." });
        }

        string host = request.Headers.GetValueOrDefault("host", "");
        bool isLocalHost = host == $"127.0.0.1:{Port}" || host == $"localhost:{Port}";
        if (false == isLocalHost)
        {
            return (403, new { error = "127.0.0.1 로만 요청할 수 있습니다." });
        }

        JsonNode? body = null;
        if ("POST" == request.Method)
        {
            string contentType = request.Headers.GetValueOrDefault("content-type", "");
            bool isJson = contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase);
            if (false == isJson)
            {
                return (415, new { error = "POST 는 Content-Type: application/json 만 받습니다." });
            }

            try
            {
                body = request.Body.Trim().Length > 0 ? JsonNode.Parse(request.Body) : null;
            }
            catch (JsonException ex)
            {
                return (400, new { error = $"JSON 이 아닙니다: {ex.Message}" });
            }
        }

        try
        {
            return await _handler(request.Method, request.Path, body);
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    // HTTP/1.x 요청 하나: 요청 줄 + 머리 + (Content-Length 만큼) 몸통
    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        List<byte> head = new();
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, token);
            if (0 == read || head.Count >= MaxHeaderBytes)
            {
                return null;
            }

            head.Add(one[0]);
            int count = head.Count;
            bool ended = count >= 4 && '\r' == head[count - 4] && '\n' == head[count - 3] && '\r' == head[count - 2] && '\n' == head[count - 1];
            if (ended)
            {
                break;
            }
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 3)
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }

        int length = 0;
        if (headers.TryGetValue("content-length", out string? lengthText) && (false == int.TryParse(lengthText, out length) || length < 0 || length > MaxBodyBytes))
        {
            return null;
        }

        // curl 은 큰 몸통 앞에 Expect: 100-continue 를 붙이고 답을 기다림 — 바로 계속하라고 답함
        bool expectsContinue = headers.TryGetValue("expect", out string? expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
        if (expectsContinue && length > 0)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token);
        }

        byte[] bodyBytes = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(bodyBytes.AsMemory(offset), token);
            if (0 == read)
            {
                return null;
            }

            offset += read;
        }

        string target = first[1];
        int query = target.IndexOf('?');
        string path = query >= 0 ? target[..query] : target;
        return new Request(first[0].ToUpperInvariant(), path, headers, Encoding.UTF8.GetString(bodyBytes));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, object body, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, ResponseJson));
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            415 => "Unsupported Media Type",
            503 => "Service Unavailable",
            _ => "Status",
        };
        string header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        await stream.WriteAsync(bytes, token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 이미 닫힘
        }
    }
}
