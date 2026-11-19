using System.Globalization;
using System.Text.Json.Nodes;

namespace DayDeck.Core.Drawings;

/// <summary>그림 좌표 (Excalidraw 좌표계, 화면 단위).</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>사각 영역.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Pt Center => new(X + Width / 2, Y + Height / 2);
    public bool Contains(Pt p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool Contains(Box b) => b.X >= X && b.Right <= Right && b.Y >= Y && b.Bottom <= Bottom;
    public bool Intersects(Box b) => b.X <= Right && b.Right >= X && b.Y <= Bottom && b.Bottom >= Y;

    public Box Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public static Box FromPoints(Pt a, Pt b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static Box Union(IEnumerable<Box> boxes)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var b in boxes)
        {
            minX = Math.Min(minX, b.X);
            minY = Math.Min(minY, b.Y);
            maxX = Math.Max(maxX, b.Right);
            maxY = Math.Max(maxY, b.Bottom);
        }
        return minX == double.MaxValue ? default : new Box(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>화살표 끝이 붙은 도형.</summary>
/// <summary>
/// 화살표 끝이 붙은 도형. <paramref name="FixedPoint"/>는 도형 상자 안의 비율 위치(0~1, Excalidraw의 fixedPoint)로,
/// 있으면 화살표 끝이 그 연결점에 붙고 없으면 도형 가운데를 향해 테두리에 붙는다.
/// </summary>
public sealed record ArrowBinding(string ElementId, double Focus = 0, double Gap = 6, Pt? FixedPoint = null);

/// <summary>모서리 둥글기 (Excalidraw roundness: 1 = 예전 방식, 2 = 비율(선·마름모), 3 = 고정 반지름(사각형)).</summary>
public sealed record Roundness(int Type, double? Value = null);

/// <summary>도형이 기억하는 연결 (화살표 또는 안쪽 글자).</summary>
public sealed record BoundRef(string Id, string Type);

/// <summary>
/// Excalidraw 요소 하나. JSON 객체를 그대로 들고 아는 속성만 읽고 쓴다.
/// 모르는 속성(다른 버전·플러그인이 쓴 값)은 건드리지 않아 excalidraw.com·Obsidian Excalidraw와 오가도 남는다.
/// </summary>
public sealed class ExcalidrawElement
{
    public const string Rectangle = "rectangle";
    public const string Diamond = "diamond";
    public const string Ellipse = "ellipse";
    public const string Arrow = "arrow";
    public const string Line = "line";
    public const string FreeDraw = "freedraw";
    public const string Text = "text";

    public const string DefaultStroke = "#1e1e1e";
    public const string Transparent = "transparent";

    public ExcalidrawElement(JsonObject node) => Node = node;

    public JsonObject Node { get; }

    public string Id { get => Str("id") ?? ""; set => Node["id"] = value; }
    public string Type { get => Str("type") ?? ""; set => Node["type"] = value; }
    public double X { get => Num("x"); set => Node["x"] = value; }
    public double Y { get => Num("y"); set => Node["y"] = value; }
    public double Width { get => Num("width"); set => Node["width"] = value; }
    public double Height { get => Num("height"); set => Node["height"] = value; }
    public double Angle { get => Num("angle"); set => Node["angle"] = value; }
    public string StrokeColor { get => Str("strokeColor") ?? DefaultStroke; set => Node["strokeColor"] = value; }
    public string BackgroundColor { get => Str("backgroundColor") ?? Transparent; set => Node["backgroundColor"] = value; }
    public string FillStyle { get => Str("fillStyle") ?? "solid"; set => Node["fillStyle"] = value; }
    public double StrokeWidth { get => Num("strokeWidth", 2); set => Node["strokeWidth"] = value; }
    public string StrokeStyle { get => Str("strokeStyle") ?? "solid"; set => Node["strokeStyle"] = value; }
    public double Roughness { get => Num("roughness", 1); set => Node["roughness"] = value; }
    public double Opacity { get => Num("opacity", 100); set => Node["opacity"] = value; }
    public int Seed { get => (int)Num("seed", 1); set => Node["seed"] = value; }
    public int Version { get => (int)Num("version", 1); set => Node["version"] = value; }
    public int VersionNonce { get => (int)Num("versionNonce"); set => Node["versionNonce"] = value; }
    public bool IsDeleted { get => Node["isDeleted"] is JsonValue v && v.TryGetValue(out bool b) && b; set => Node["isDeleted"] = value; }
    public long Updated { get => (long)Num("updated"); set => Node["updated"] = value; }

    /// <summary>글자 요소의 내용 (상자 안 글자는 줄바꿈된 모양 그대로).</summary>
    public string TextContent { get => Str("text") ?? ""; set => Node["text"] = value; }
    public string OriginalText { get => Str("originalText") ?? TextContent; set => Node["originalText"] = value; }
    public double FontSize { get => Num("fontSize", 20); set => Node["fontSize"] = value; }
    public int FontFamily { get => (int)Num("fontFamily", 1); set => Node["fontFamily"] = value; }
    public string TextAlign { get => Str("textAlign") ?? "left"; set => Node["textAlign"] = value; }
    public string VerticalAlign { get => Str("verticalAlign") ?? "top"; set => Node["verticalAlign"] = value; }
    public string? ContainerId { get => Str("containerId"); set => Node["containerId"] = value; }
    public double LineHeight { get => Num("lineHeight", 1.25); set => Node["lineHeight"] = value; }

    public string? StartArrowhead { get => Str("startArrowhead"); set => Node["startArrowhead"] = value; }
    public string? EndArrowhead { get => Str("endArrowhead"); set => Node["endArrowhead"] = value; }

    public Roundness? Roundness
    {
        get => Node["roundness"] is JsonObject o ? new Roundness((int)ToDouble(o["type"]), o["value"] is JsonValue v && v.TryGetValue(out double d) ? d : null) : null;
        set => Node["roundness"] = value is null ? null
            : value.Value is { } radius ? new JsonObject { ["type"] = value.Type, ["value"] = radius } : new JsonObject { ["type"] = value.Type };
    }

    /// <summary>펜 획의 압력 (점마다 0~1). 비어 있으면 속도로 흉내 낸다 (<see cref="SimulatePressure"/>).</summary>
    public IReadOnlyList<double> Pressures => Node["pressures"] is JsonArray a ? [.. a.Select(n => ToDouble(n))] : [];

    public bool SimulatePressure => Node["simulatePressure"] is JsonValue v && v.TryGetValue(out bool b) && b;

    public bool HasLastCommittedPoint => Node["lastCommittedPoint"] is JsonArray;

    public bool IsLinear => Type is Arrow or Line;
    public bool IsShape => Type is Rectangle or Diamond or Ellipse;

    /// <summary>화살표를 붙일 수 있는 도형 (사각형·마름모·원, 상자 밖 글자).</summary>
    public bool IsBindable => IsShape || (Type == Text && ContainerId is null);

    public IReadOnlyList<string> GroupIds
    {
        get => Node["groupIds"] is JsonArray a ? [.. a.OfType<JsonValue>().Select(n => n.TryGetValue(out string? s) ? s : "")] : [];
        set => Node["groupIds"] = new JsonArray([.. value.Select(v => (JsonNode?)JsonValue.Create(v))]);
    }

    public IReadOnlyList<BoundRef> BoundElements
    {
        get => Node["boundElements"] is JsonArray a
            ? [.. a.OfType<JsonObject>().Select(o => new BoundRef(StrOf(o["id"]) ?? "", StrOf(o["type"]) ?? ""))]
            : [];
        set => Node["boundElements"] = value.Count == 0 ? null
            : new JsonArray([.. value.Select(b => (JsonNode?)new JsonObject { ["id"] = b.Id, ["type"] = b.Type })]);
    }

    public void AddBound(string id, string type)
    {
        var list = BoundElements;
        if (!list.Any(b => b.Id == id))
        {
            BoundElements = [.. list, new BoundRef(id, type)];
        }
    }

    public void RemoveBound(string id)
    {
        var list = BoundElements;
        if (list.Any(b => b.Id == id))
        {
            BoundElements = [.. list.Where(b => b.Id != id)];
        }
    }

    /// <summary>선·화살표·펜의 점 (요소 X,Y 기준 상대 좌표).</summary>
    public IReadOnlyList<Pt> Points
    {
        get => Node["points"] is JsonArray a
            ? [.. a.OfType<JsonArray>().Where(p => p.Count >= 2).Select(p => new Pt(ToDouble(p[0]), ToDouble(p[1])))]
            : [];
        set => Node["points"] = new JsonArray([.. value.Select(p => (JsonNode?)new JsonArray(Round(p.X), Round(p.Y)))]);
    }

    public ArrowBinding? StartBinding { get => ReadBinding("startBinding"); set => WriteBinding("startBinding", value); }
    public ArrowBinding? EndBinding { get => ReadBinding("endBinding"); set => WriteBinding("endBinding", value); }

    /// <summary>바꿀 때마다 부른다 (Excalidraw의 버전 규칙: 같이 고친 내용을 합칠 때 쓰인다).</summary>
    public void Touch(Random random)
    {
        Version += 1;
        VersionNonce = random.Next();
        Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public ExcalidrawElement Clone() => new((JsonObject)Node.DeepClone());

    /// <summary>새 요소. Excalidraw가 새로 그릴 때 넣는 기본값과 같다.</summary>
    public static ExcalidrawElement Create(string type, double x, double y, double width, double height, Random random)
    {
        var node = new JsonObject
        {
            ["id"] = NewId(random),
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["angle"] = 0,
            ["strokeColor"] = DefaultStroke,
            ["backgroundColor"] = Transparent,
            ["fillStyle"] = "solid",
            ["strokeWidth"] = 2,
            ["strokeStyle"] = "solid",
            ["roughness"] = 1,
            ["opacity"] = 100,
            ["groupIds"] = new JsonArray(),
            ["frameId"] = null,
            ["roundness"] = type is Rectangle ? new JsonObject { ["type"] = 3 } : type is Diamond or Arrow or Line ? new JsonObject { ["type"] = 2 } : null,
            ["seed"] = random.Next(1, int.MaxValue),
            ["version"] = 1,
            ["versionNonce"] = random.Next(),
            ["isDeleted"] = false,
            ["boundElements"] = null,
            ["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["link"] = null,
            ["locked"] = false,
        };
        var element = new ExcalidrawElement(node);
        switch (type)
        {
            case Arrow:
            case Line:
                element.Points = [new Pt(0, 0), new Pt(width, height)];
                node["lastCommittedPoint"] = null;
                node["startBinding"] = null;
                node["endBinding"] = null;
                node["startArrowhead"] = null;
                node["endArrowhead"] = type == Arrow ? "arrow" : null;
                break;
            case FreeDraw:
                element.Points = [new Pt(0, 0)];
                node["pressures"] = new JsonArray();
                node["simulatePressure"] = true;
                node["lastCommittedPoint"] = null;
                break;
            case Text:
                node["text"] = "";
                node["fontSize"] = 20;
                node["fontFamily"] = 5;
                node["textAlign"] = "left";
                node["verticalAlign"] = "top";
                node["containerId"] = null;
                node["originalText"] = "";
                node["lineHeight"] = 1.25;
                node["autoResize"] = true;
                break;
        }
        return element;
    }

    public static string NewId(Random random)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-";
        return string.Create(21, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[r.Next(chars.Length)];
            }
        });
    }

    private ArrowBinding? ReadBinding(string key) =>
        Node[key] is JsonObject o && StrOf(o["elementId"]) is { } id
            ? new ArrowBinding(id, ToDouble(o["focus"]), ToDouble(o["gap"]),
                o["fixedPoint"] is JsonArray fp && fp.Count >= 2 ? new Pt(ToDouble(fp[0]), ToDouble(fp[1])) : null)
            : null;

    private void WriteBinding(string key, ArrowBinding? value)
    {
        if (value is null)
        {
            Node[key] = null;
            return;
        }
        // 다른 속성(fixedPoint 등)은 같은 도형이면 남기고 아는 값만 바꾼다.
        var o = Node[key] as JsonObject;
        if (o is null || StrOf(o["elementId"]) != value.ElementId)
        {
            o = new JsonObject();
        }
        o["elementId"] = value.ElementId;
        o["focus"] = value.Focus;
        o["gap"] = value.Gap;
        o["fixedPoint"] = value.FixedPoint is { } fixedPoint ? new JsonArray(fixedPoint.X, fixedPoint.Y) : null;
        Node[key] = o;
    }

    private string? Str(string key) => StrOf(Node[key]);

    private static string? StrOf(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private double Num(string key, double fallback = 0) => ToDouble(Node[key], fallback);

    private static double ToDouble(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue v)
        {
            return fallback;
        }
        if (v.TryGetValue(out double d))
        {
            return d;
        }
        if (v.TryGetValue(out long l))
        {
            return l;
        }
        if (v.TryGetValue(out int i))
        {
            return i;
        }
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static double Round(double v) => Math.Round(v, 2);
}
