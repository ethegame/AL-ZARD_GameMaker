using System.Diagnostics;
using System.IO;
using System.Text;

namespace GameMaker.Core;

// 화면 파일 공급 (Photino 사용자 스킴 처리기 — 예전 WebView2 SetVirtualHostNameToFolderMapping 자리).
//   <origin>/…         = 출력 폴더 wwwroot
//   <origin>/_data/…   = DataDir (결과·선택한 이미지 미리보기 — 예전 https://files.gamemaker.local)
// DataDir 을 다른 호스트로 두면 three.js 의 fetch 가 교차 출처가 되는데 사용자 스킴 응답엔 CORS 머리글을 붙일 수 없어 같은 호스트의 경로로 나눔.
// ../ · %5C · 절대 경로로 두 폴더 밖을 읽지 못하게 한다. 없는 파일·남의 주소도 null 대신 404 글을 직접 돌려준다
// (null 이면 웹뷰가 진짜 네트워크로 보내 버림 — 바깥 페이지로 화면이 바뀌는 길도 Windows 에선 이걸로 막힘)
public sealed class WebFiles
{
    public const string HostName = "gamemaker.local";
    public const string DataPrefix = "_data";

    private readonly string _wwwroot;

    public WebFiles(string wwwroot)
    {
        _wwwroot = wwwroot;
    }

    // MainWindow 가 DataDir 을 정한 뒤 넣음
    public string? DataRoot { get; set; }

    // Photino 처리기 (UI 스레드). 우리 것이 아니거나 없는 파일 = 404 글
    public Stream Serve(string url, out string contentType)
    {
        if (false == Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return NotFound(out contentType);
        }

        if (false == string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(out contentType);
        }

        string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (0 == relative.Length)
        {
            relative = "index.html";
        }

        string root = _wwwroot;
        string dataPrefix = DataPrefix + "/";
        if (relative.StartsWith(dataPrefix, StringComparison.Ordinal))
        {
            if (null == DataRoot)
            {
                return NotFound(out contentType);
            }

            root = DataRoot;
            relative = relative[dataPrefix.Length..];
        }

        string? full = Inside(root, relative);
        if (null == full || false == File.Exists(full))
        {
            return NotFound(out contentType);
        }

        try
        {
            // 생성 중인 결과 파일도 읽히게 쓰기 공유
            using FileStream file = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            MemoryStream memory = new();
            file.CopyTo(memory);
            memory.Position = 0;
            contentType = ContentTypeOf(full);
            return memory;
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"화면 파일 읽기 실패: {full} — {ex.Message}");
            return NotFound(out contentType);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound(out contentType);
        }
    }

    // root 안의 전체 경로, 밖이면 null
    private static string? Inside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (false == full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full;
    }

    // Photino 사용자 스킴은 상태 코드를 줄 수 없어 글로 알림
    private static Stream NotFound(out string contentType)
    {
        contentType = "text/plain; charset=utf-8";
        return new MemoryStream(Encoding.UTF8.GetBytes("404 Not Found"));
    }

    private static string ContentTypeOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".html":
                return "text/html; charset=utf-8";
            case ".js":
            case ".mjs":
                return "text/javascript; charset=utf-8";
            case ".css":
                return "text/css; charset=utf-8";
            case ".json":
                return "application/json; charset=utf-8";
            case ".md":
            case ".txt":
            case ".log":
                return "text/plain; charset=utf-8";
            case ".svg":
                return "image/svg+xml";
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".gif":
                return "image/gif";
            case ".webp":
                return "image/webp";
            case ".bmp":
                return "image/bmp";
            case ".ico":
                return "image/x-icon";
            case ".glb":
                return "model/gltf-binary";
            case ".gltf":
                return "model/gltf+json";
            case ".mp3":
                return "audio/mpeg";
            case ".wav":
                return "audio/wav";
            case ".ogg":
            case ".opus":
                return "audio/ogg";
            case ".flac":
                return "audio/flac";
            case ".ttf":
                return "font/ttf";
            case ".woff":
                return "font/woff";
            case ".woff2":
                return "font/woff2";
        }

        return "application/octet-stream";
    }
}
