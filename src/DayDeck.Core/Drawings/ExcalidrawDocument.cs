using System.Text.Json;
using System.Text.Json.Nodes;

namespace DayDeck.Core.Drawings;

/// <summary>
/// <c>.excalidraw</c> 파일 하나 (excalidraw.com·Obsidian Excalidraw와 같은 JSON).
/// 최상위의 모르는 값(appState의 다른 설정, files의 그림 등)은 읽은 그대로 다시 쓴다.
/// </summary>
public sealed class ExcalidrawDocument
{
    public const string Source = "https://github.com/aydenchoi/daydeck";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly JsonObject _root;

    private ExcalidrawDocument(JsonObject root, List<ExcalidrawElement> elements)
    {
        _root = root;
        Elements = elements;
    }

    /// <summary>모든 요소 (지운 것 포함, 그리는 순서 = 뒤에 있을수록 위).</summary>
    public List<ExcalidrawElement> Elements { get; }

    /// <summary>지우지 않은 요소.</summary>
    public IEnumerable<ExcalidrawElement> Visible => Elements.Where(e => !e.IsDeleted);

    public string ViewBackgroundColor
    {
        get => _root["appState"]?["viewBackgroundColor"] is JsonValue v && v.TryGetValue(out string? s) ? s : "#ffffff";
        set => AppState["viewBackgroundColor"] = value;
    }

    private JsonObject AppState
    {
        get
        {
            if (_root["appState"] is not JsonObject state)
            {
                state = new JsonObject();
                _root["appState"] = state;
            }
            return state;
        }
    }

    public ExcalidrawElement? Find(string? id) =>
        id is null ? null : Elements.FirstOrDefault(e => e.Id == id && !e.IsDeleted);

    public static ExcalidrawDocument CreateEmpty() => Parse("""
        {"type":"excalidraw","version":2,"source":"","elements":[],"appState":{"gridSize":null,"viewBackgroundColor":"#ffffff"},"files":{}}
        """);

    /// <summary>JSON을 읽는다. Excalidraw 파일이 아니면 <see cref="FormatException"/>.</summary>
    public static ExcalidrawDocument Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException("Excalidraw 파일을 읽을 수 없습니다: " + ex.Message, ex);
        }
        if (node is not JsonObject root)
        {
            throw new FormatException("Excalidraw 파일이 아닙니다.");
        }
        if (root["type"] is JsonValue type && type.TryGetValue(out string? t) && t != "excalidraw")
        {
            throw new FormatException($"Excalidraw 파일이 아닙니다 (type: {t}).");
        }

        var elements = new List<ExcalidrawElement>();
        if (root["elements"] is JsonArray array)
        {
            var nodes = array.OfType<JsonObject>().ToList();
            array.Clear(); // 요소 노드를 배열에서 떼어 각자 들고 있는다 (쓸 때 다시 붙임).
            elements.AddRange(nodes.Select(n => new ExcalidrawElement(n)));
        }
        return new ExcalidrawDocument(root, elements);
    }

    public string Serialize()
    {
        var copy = (JsonObject)_root.DeepClone();
        copy["type"] = "excalidraw";
        copy["version"] = 2;
        copy["source"] = Source;
        copy["elements"] = new JsonArray([.. Elements.Select(e => (JsonNode?)e.Node.DeepClone())]);
        if (copy["appState"] is null)
        {
            copy["appState"] = new JsonObject { ["viewBackgroundColor"] = "#ffffff", ["gridSize"] = null };
        }
        if (copy["files"] is null)
        {
            copy["files"] = new JsonObject();
        }
        return copy.ToJsonString(WriteOptions);
    }

    /// <summary>되돌리기용 스냅숏 (요소만).</summary>
    public string SnapshotElements() =>
        new JsonArray([.. Elements.Select(e => (JsonNode?)e.Node.DeepClone())]).ToJsonString();

    public void RestoreElements(string snapshot)
    {
        Elements.Clear();
        if (JsonNode.Parse(snapshot) is JsonArray array)
        {
            var nodes = array.OfType<JsonObject>().ToList();
            array.Clear();
            Elements.AddRange(nodes.Select(n => new ExcalidrawElement(n)));
        }
    }
}
