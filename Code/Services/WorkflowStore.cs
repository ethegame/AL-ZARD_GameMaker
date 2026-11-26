using System.IO;
using System.Text.Json.Nodes;

namespace GameMaker.Services;

// <DataDir>/workflows/*.json = ComfyUI "Export (API)" 형식 워크플로.
// 노드 입력 중 링크([노드id, 슬롯])가 아닌 값(문자열·숫자·불)을 편집 칸으로 꺼낸다.
public sealed class WorkflowStore
{
    public sealed record Field(string Key, string Kind, JsonNode? Value);
    // Main = _meta.gm == "main" — 노드가 많은 워크플로(3D 등)에서 기본으로 보일 칸만 표시. 하나도 없으면 전부 표시
    public sealed record NodeView(string Id, string ClassType, string Title, List<Field> Fields, bool Main);

    private readonly string _dir;

    public WorkflowStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "workflows");
        Directory.CreateDirectory(_dir);
    }

    public string Folder => _dir;

    public List<string> List()
    {
        return Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // 다른 곳의 json 을 workflows 폴더로 복사. 형식 오류면 예외 (복사 안 함)
    public string Import(string sourcePath)
    {
        Parse(File.ReadAllText(sourcePath));
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        File.Copy(sourcePath, Path.Combine(_dir, name + ".json"), overwrite: true);
        return name;
    }

    public JsonObject Load(string name)
    {
        string path = Path.Combine(_dir, Path.GetFileName(name) + ".json");
        return Parse(File.ReadAllText(path));
    }

    public static List<NodeView> Describe(JsonObject prompt)
    {
        List<NodeView> nodes = new();
        foreach ((string id, JsonNode? node) in prompt.OrderBy(pair => int.TryParse(pair.Key, out int n) ? n : int.MaxValue))
        {
            string classType = node?["class_type"]?.GetValue<string>() ?? "";
            string title = node?["_meta"]?["title"]?.GetValue<string>() ?? classType;
            List<Field> fields = new();
            if (node?["inputs"] is JsonObject inputs)
            {
                foreach ((string key, JsonNode? value) in inputs)
                {
                    string? kind = KindOf(classType, key, value);
                    if (null != kind)
                    {
                        fields.Add(new Field(key, kind, value?.DeepClone()));
                    }
                }
            }

            bool main = "main" == node?["_meta"]?["gm"]?.GetValue<string>();
            nodes.Add(new NodeView(id, classType, title, fields, main));
        }

        return nodes;
    }

    // values = { nodeId: { inputKey: value } } 를 워크플로 사본에 덮어씀
    public static JsonObject Apply(JsonObject prompt, JsonObject values)
    {
        JsonObject copy = prompt.DeepClone().AsObject();
        foreach ((string id, JsonNode? inputs) in values)
        {
            if (copy[id]?["inputs"] is not JsonObject target || inputs is not JsonObject source)
            {
                continue;
            }

            foreach ((string key, JsonNode? value) in source)
            {
                // 링크 입력은 덮어쓰지 않는다 (편집 칸으로 내보낸 적 없음)
                if (target[key] is JsonArray)
                {
                    continue;
                }

                target[key] = value?.DeepClone();
            }
        }

        return copy;
    }

    private static JsonObject Parse(string json)
    {
        JsonNode? root = JsonNode.Parse(json);
        if (root is not JsonObject obj)
        {
            throw new InvalidDataException("JSON 객체가 아닙니다.");
        }

        // UI 형식(저장 버튼) = { nodes:[...], links:[...] } — API 형식이 아님
        if (obj["nodes"] is JsonArray && obj["links"] is JsonArray)
        {
            throw new InvalidDataException("ComfyUI UI 형식 파일입니다. ComfyUI 메뉴 Workflow → Export (API) 로 저장한 파일을 넣어 주세요.");
        }

        foreach ((string id, JsonNode? node) in obj)
        {
            if (null == node?["class_type"])
            {
                throw new InvalidDataException($"노드 {id} 에 class_type 이 없습니다. API 형식 워크플로가 아닙니다.");
            }
        }

        return obj;
    }

    private static string? KindOf(string classType, string key, JsonNode? value)
    {
        if (value is JsonArray || value is JsonObject || null == value)
        {
            return null;
        }

        JsonValue scalar = value.AsValue();
        if (scalar.TryGetValue(out bool _))
        {
            return "bool";
        }

        if (scalar.TryGetValue(out string? _))
        {
            // LoadImage 계열의 image 입력 = 파일 선택
            if ("image" == key && classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase))
            {
                return "image";
            }

            return "string";
        }

        return "number";
    }
}
