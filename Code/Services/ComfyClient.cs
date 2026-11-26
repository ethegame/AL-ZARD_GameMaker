using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameMaker.Services;

// ComfyUI HTTP/WebSocket API.
//   POST /prompt {prompt, client_id} → {prompt_id}
//   WS   /ws?clientId=  → progress / executing(node=null 이면 끝) / execution_error
//   GET  /history/{id}  → outputs[nodeId] = { images:[{filename,subfolder,type}], "3d":[...], ... }
//   GET  /view?filename=&subfolder=&type=
//   POST /upload/image (multipart image) → {name, subfolder, type}
public sealed class ComfyClient : IDisposable
{
    public sealed record Progress(string Text, int Value, int Max);
    public sealed record OutputFile(string FileName, string SubFolder, string Type, string Key);

    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _clientId = Guid.NewGuid().ToString("N");

    public ComfyClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    // LoadImage 노드에 넣을 값 (하위 폴더 포함 이름)
    public async Task<string> UploadImageAsync(string path)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", Path.GetFileName(path));
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("input"), "type");

        using HttpResponseMessage response = await _http.PostAsync(_baseUrl + "/upload/image", form);
        string body = await response.Content.ReadAsStringAsync();
        if (false == response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"이미지 업로드 실패 ({(int)response.StatusCode}): {body}");
        }

        JsonNode result = JsonNode.Parse(body)!;
        string name = result["name"]?.GetValue<string>() ?? Path.GetFileName(path);
        string sub = result["subfolder"]?.GetValue<string>() ?? "";
        return sub.Length > 0 ? $"{sub}/{name}" : name;
    }

    // 큐에 넣고 끝날 때까지 진행률을 흘린 뒤 결과 파일 목록 반환
    public async Task<List<OutputFile>> RunAsync(JsonObject prompt, Action<Progress> onProgress, CancellationToken token)
    {
        using ClientWebSocket socket = new();
        Uri wsUri = new((_baseUrl.Replace("http://", "ws://").Replace("https://", "wss://")) + "/ws?clientId=" + _clientId);
        await socket.ConnectAsync(wsUri, token);

        JsonObject request = new()
        {
            ["prompt"] = prompt,
            ["client_id"] = _clientId,
        };
        using StringContent content = new(request.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync(_baseUrl + "/prompt", content, token);
        string body = await response.Content.ReadAsStringAsync(token);
        if (false == response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribePromptError(body));
        }

        string promptId = JsonNode.Parse(body)!["prompt_id"]!.GetValue<string>();
        onProgress(new Progress("대기열 등록", 0, 0));

        // 진행 문구에 노드 번호 대신 _meta.title (3D 처럼 노드가 많으면 어디쯤인지 보이게)
        string Title(string nodeId) => prompt[nodeId]?["_meta"]?["title"]?.GetValue<string>() ?? $"노드 {nodeId}";
        await WaitDoneAsync(socket, promptId, Title, onProgress, token);
        return await GetOutputsAsync(promptId, token);
    }

    public async Task InterruptAsync()
    {
        using StringContent content = new("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage _ = await _http.PostAsync(_baseUrl + "/interrupt", content);
    }

    public async Task DownloadAsync(OutputFile file, string destination)
    {
        string url = $"{_baseUrl}/view?filename={Uri.EscapeDataString(file.FileName)}&subfolder={Uri.EscapeDataString(file.SubFolder)}&type={Uri.EscapeDataString(file.Type)}";
        using HttpResponseMessage response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using FileStream stream = File.Create(destination);
        await response.Content.CopyToAsync(stream);
    }

    private static async Task WaitDoneAsync(ClientWebSocket socket, string promptId, Func<string, string> title, Action<Progress> onProgress, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        using MemoryStream message = new();
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("ComfyUI 연결이 끊겼습니다.");
            }

            message.Write(buffer, 0, result.Count);
            if (false == result.EndOfMessage)
            {
                continue;
            }

            // 바이너리 = 미리보기 이미지 프레임 → 무시
            if (result.MessageType != WebSocketMessageType.Text)
            {
                message.SetLength(0);
                continue;
            }

            JsonNode? node = JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
            string type = node?["type"]?.GetValue<string>() ?? "";
            JsonNode? data = node?["data"];
            if (promptId != data?["prompt_id"]?.GetValue<string>())
            {
                continue;
            }

            switch (type)
            {
                case "progress": {
                    onProgress(new Progress(title(data!["node"]?.ToString() ?? ""), data["value"]!.GetValue<int>(), data["max"]!.GetValue<int>()));
                    break;
                }
                case "executing": {
                    JsonNode? executing = data!["node"];
                    if (null == executing)
                    {
                        return;
                    }

                    onProgress(new Progress(title(executing.ToString()), 0, 0));
                    break;
                }
                case "execution_success": {
                    return;
                }
                case "execution_error": {
                    string errorText = data!["exception_message"]?.GetValue<string>() ?? "알 수 없는 오류";
                    string nodeType = data["node_type"]?.GetValue<string>() ?? "";
                    throw new InvalidOperationException($"실행 오류 ({nodeType}): {errorText.Trim()}");
                }
                case "execution_interrupted": {
                    throw new OperationCanceledException("중단됨");
                }
            }
        }
    }

    // 완료 신호(executing null) 직후엔 /history 가 아직 비어 있을 수 있다 (2026-10-26 9장 연속 생성 중 5장 결과 누락)
    // → 빈 결과면 잠깐 기다렸다 다시 읽는다
    private async Task<List<OutputFile>> GetOutputsAsync(string promptId, CancellationToken token)
    {
        const int MaxAttempts = 20;
        const int RetryDelayMs = 250;
        List<OutputFile> files = new();
        for (int attempt = 0; attempt < MaxAttempts; ++attempt)
        {
            files = await ReadOutputsAsync(promptId, token);
            if (files.Count > 0)
            {
                break;
            }

            await Task.Delay(RetryDelayMs, token);
        }

        return files;
    }

    private async Task<List<OutputFile>> ReadOutputsAsync(string promptId, CancellationToken token)
    {
        string body = await _http.GetStringAsync(_baseUrl + "/history/" + promptId, token);
        List<OutputFile> files = new();
        JsonObject? outputs = JsonNode.Parse(body)?[promptId]?["outputs"]?.AsObject();
        if (null == outputs)
        {
            return files;
        }

        // 노드 종류와 무관하게 {filename, subfolder, type} 모양이면 전부 결과 파일로 본다 (images / "3d" / gifs ...)
        foreach ((string _, JsonNode? nodeOutput) in outputs)
        {
            if (nodeOutput is not JsonObject byKey)
            {
                continue;
            }

            foreach ((string key, JsonNode? list) in byKey)
            {
                if (list is not JsonArray array)
                {
                    continue;
                }

                foreach (JsonNode? item in array)
                {
                    string? fileName = (item as JsonObject)?["filename"]?.GetValue<string>();
                    if (null == fileName)
                    {
                        continue;
                    }

                    string sub = item!["subfolder"]?.GetValue<string>() ?? "";
                    string type = item["type"]?.GetValue<string>() ?? "output";
                    files.Add(new OutputFile(fileName, sub, type, key));
                }
            }
        }

        return files;
    }

    // /prompt 400 응답 = {error:{message}, node_errors:{id:{class_type, errors:[{message, details}]}}}
    private static string DescribePromptError(string body)
    {
        try
        {
            JsonNode root = JsonNode.Parse(body)!;
            StringBuilder text = new(root["error"]?["message"]?.GetValue<string>() ?? "프롬프트 거부");
            if (root["node_errors"] is JsonObject nodeErrors)
            {
                foreach ((string id, JsonNode? info) in nodeErrors)
                {
                    foreach (JsonNode? error in info?["errors"]?.AsArray() ?? new JsonArray())
                    {
                        text.Append($"\n- 노드 {id} ({info!["class_type"]}): {error?["message"]} {error?["details"]}");
                    }
                }
            }

            return text.ToString();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
