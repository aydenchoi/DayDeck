using System.Globalization;
using System.Text.RegularExpressions;
using DayDeck.Core.Drawings;
using DayDeck.Core.Drawings.Rough;

namespace DayDeck.Core.Tests;

/// <summary>
/// 요소 모양이 진짜 Excalidraw와 같은지: tools/excalidraw-ref의 샘플을 Excalidraw 0.18.1로 SVG 내보내기 한 결과의 경로 숫자와
/// <see cref="ExcalidrawShapes"/>가 만든 경로 숫자를 차례대로 비교한다 (SVG는 소수 둘째 자리로 반올림됨).
/// </summary>
public sealed partial class ExcalidrawShapeGoldenTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Golden", "excalidraw");

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Dir, "*.excalidraw").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }
        return data;
    }

    [GeneratedRegex(@"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"<mask[\s\S]*?</mask>|<defs>[\s\S]*?</defs>")]
    private static partial Regex SkipRegex();

    [GeneratedRegex(@"<path[^>]*?\sd=""([^""]*)""")]
    private static partial Regex PathRegex();

    private static List<double> Numbers(string d) =>
        [.. NumberRegex().Matches(d).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))];

    /// <summary>Excalidraw SVG 순서: 요소 배열 순서, 단 상자 안 글자는 상자 바로 뒤 (글자는 경로가 없음).</summary>
    private static List<string> OurPaths(ExcalidrawDocument doc)
    {
        var paths = new List<string>();
        foreach (var e in doc.Visible)
        {
            foreach (var drawable in ExcalidrawShapes.Generate(e))
            {
                foreach (var set in drawable.Sets)
                {
                    paths.Add(RoughGenerator.OpsToPath(set));
                }
            }
            if (e.Type == "freedraw")
            {
                paths.Add(string.Join(" ", ExcalidrawShapes.FreedrawOutline(e).SelectMany(s => s.Data)
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            }
        }
        return paths;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void PathsMatchExcalidraw(string name)
    {
        var doc = ExcalidrawDocument.Parse(File.ReadAllText(Path.Combine(Dir, name + ".excalidraw")));
        var svg = SkipRegex().Replace(File.ReadAllText(Path.Combine(Dir, name + ".svg")), "");
        var expected = PathRegex().Matches(svg).Select(m => m.Groups[1].Value).ToList();
        var actual = OurPaths(doc);
        Assert.True(expected.Count == actual.Count, $"{name}: 경로 수 {actual.Count} != {expected.Count}");
        for (var i = 0; i < expected.Count; i++)
        {
            var e = Numbers(expected[i]);
            var a = Numbers(actual[i]);
            Assert.True(e.Count == a.Count, $"{name} 경로 {i}: 숫자 수 {a.Count} != {e.Count}\n기대: {expected[i][..Math.Min(200, expected[i].Length)]}\n실제: {actual[i][..Math.Min(200, actual[i].Length)]}");
            for (var k = 0; k < e.Count; k++)
            {
                Assert.True(Math.Abs(e[k] - a[k]) <= 0.0101, $"{name} 경로 {i} 숫자 {k}: {a[k]} != {e[k]}");
            }
        }
    }
}
