using System.IO.Compression;
using System.Text.Json.Nodes;
using DayDeck.Core.Drawings.Rough;

namespace DayDeck.Core.Tests;

/// <summary>
/// rough.js 포팅이 원본과 같은 좌표를 내는지: tools/excalidraw-ref/rough-golden.mjs로 원본 rough.js 4.6.4에서 뽑은 결과와 비교한다.
/// </summary>
public sealed class RoughGoldenTests
{
    private static readonly Lazy<JsonArray> Cases = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Golden", "rough.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!.AsArray();
    });

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < 243; i++)
        {
            data.Add(i);
        }
        return data;
    }

    private static RoughOptions Options(JsonObject o)
    {
        var options = new RoughOptions();
        foreach (var (key, value) in o)
        {
            switch (key)
            {
                case "seed": options.Seed = (int)value!.GetValue<double>(); break;
                case "roughness": options.Roughness = value!.GetValue<double>(); break;
                case "strokeWidth": options.StrokeWidth = value!.GetValue<double>(); break;
                case "hachureGap": options.HachureGap = value!.GetValue<double>(); break;
                case "fillWeight": options.FillWeight = value!.GetValue<double>(); break;
                case "preserveVertices": options.PreserveVertices = value!.GetValue<bool>(); break;
                case "disableMultiStroke": options.DisableMultiStroke = value!.GetValue<bool>(); break;
                case "fill": options.Fill = value!.GetValue<string>(); break;
                case "fillStyle": options.FillStyle = value!.GetValue<string>(); break;
                default: throw new InvalidOperationException(key);
            }
        }
        return options;
    }

    private static double[] Pt(JsonNode n) => [n[0]!.GetValue<double>(), n[1]!.GetValue<double>()];

    private static List<double[]> Pts(JsonNode n) => [.. n.AsArray().Select(p => Pt(p!))];

    private static Drawable Run(string kind, JsonArray a, RoughOptions o) => kind switch
    {
        "rectangle" => RoughGenerator.Rectangle(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), o),
        "ellipse" => RoughGenerator.Ellipse(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), o),
        "circle" => RoughGenerator.Circle(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), o),
        "line" => RoughGenerator.Line(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>(), o),
        "polygon" => RoughGenerator.Polygon(Pts(a[0]!), o),
        "curve" => RoughGenerator.Curve(Pts(a[0]!), o),
        "linearPath" => RoughGenerator.LinearPath(Pts(a[0]!), o),
        "path" => RoughGenerator.Path(SvgPathParser.Parse(a[0]!.GetValue<string>()), o),
        _ => throw new InvalidOperationException(kind),
    };

    [Fact]
    public void GoldenFileHasAllCases() => Assert.Equal(243, Cases.Value.Count);

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void MatchesRoughJs(int index)
    {
        var cases = Cases.Value;
        if (index >= cases.Count)
        {
            return;
        }
        var c = cases[index]!.AsObject();
        var kind = c["kind"]!.GetValue<string>();
        var drawable = Run(kind, c["args"]!.AsArray(), Options(c["options"]!.AsObject()));
        var expected = c["sets"]!.AsArray();
        var label = $"{index} {kind} {c["options"]!.ToJsonString()}";
        Assert.True(expected.Count == drawable.Sets.Count, $"{label}: sets {drawable.Sets.Count} != {expected.Count}");
        for (var s = 0; s < expected.Count; s++)
        {
            var set = expected[s]!.AsObject();
            var actual = drawable.Sets[s];
            var type = set["type"]!.GetValue<string>();
            Assert.Equal(type, actual.Type switch { OpSetType.Path => "path", OpSetType.FillPath => "fillPath", _ => "fillSketch" });
            var ops = set["ops"]!.AsArray();
            Assert.True(ops.Count == actual.Ops.Count, $"{label}: set {s} ({type}) ops {actual.Ops.Count} != {ops.Count}");
            for (var i = 0; i < ops.Count; i++)
            {
                var data = ops[i]!["data"]!.AsArray();
                for (var k = 0; k < data.Count; k++)
                {
                    var e = data[k]!.GetValue<double>();
                    var v = actual.Ops[i].Data[k];
                    Assert.True(Math.Abs(e - v) < 1e-3, $"{label}: set {s} op {i}[{k}] {v} != {e}");
                }
            }
        }
    }
}
