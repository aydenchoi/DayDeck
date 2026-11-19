using System.Text.Json.Nodes;
using DayDeck.Core.Drawings;

namespace DayDeck.Core.Tests;

public class DrawingTests
{
    private const string Sample = """
        {
          "type": "excalidraw",
          "version": 2,
          "source": "https://excalidraw.com",
          "elements": [
            {
              "id": "box1", "type": "rectangle", "x": 100, "y": 100, "width": 200, "height": 100,
              "angle": 0, "strokeColor": "#1e1e1e", "backgroundColor": "#a5d8ff", "fillStyle": "hachure",
              "strokeWidth": 2, "strokeStyle": "solid", "roughness": 1, "opacity": 100, "groupIds": [],
              "frameId": null, "roundness": { "type": 3 }, "seed": 42, "version": 3, "versionNonce": 7,
              "isDeleted": false, "boundElements": [{ "id": "t1", "type": "text" }], "updated": 1, "link": null,
              "locked": false, "customData": { "plugin": "keep-me" }
            },
            {
              "id": "t1", "type": "text", "x": 150, "y": 137, "width": 100, "height": 25, "text": "시작",
              "originalText": "시작", "fontSize": 20, "fontFamily": 5, "textAlign": "center", "verticalAlign": "middle",
              "containerId": "box1", "seed": 3, "version": 1, "versionNonce": 1, "isDeleted": false
            },
            {
              "id": "old", "type": "ellipse", "x": 0, "y": 0, "width": 10, "height": 10, "isDeleted": true
            }
          ],
          "appState": { "gridSize": 20, "viewBackgroundColor": "#fffce8", "theme": "dark" },
          "files": { "img1": { "mimeType": "image/png", "id": "img1", "dataURL": "data:image/png;base64,AAAA" } }
        }
        """;

    [Fact]
    public void RoundTripKeepsUnknownFields()
    {
        var doc = ExcalidrawDocument.Parse(Sample);
        Assert.Equal(3, doc.Elements.Count);
        Assert.Equal(2, doc.Visible.Count());
        var box = doc.Elements[0];
        Assert.Equal("rectangle", box.Type);
        Assert.Equal(200, box.Width);
        Assert.Equal("#a5d8ff", box.BackgroundColor);
        Assert.Equal("hachure", box.FillStyle);
        Assert.Equal(42, box.Seed);
        Assert.Single(box.BoundElements);
        Assert.Equal("box1", doc.Elements[1].ContainerId);
        Assert.Equal("시작", doc.Elements[1].TextContent);
        Assert.Equal("#fffce8", doc.ViewBackgroundColor);

        box.X = 120;
        box.Touch(new Random(1));
        var json = JsonNode.Parse(doc.Serialize())!.AsObject();

        Assert.Equal("excalidraw", (string?)json["type"]);
        Assert.Equal(2, (int?)json["version"]);
        Assert.Equal("dark", (string?)json["appState"]!["theme"]);
        Assert.Equal(20, (int?)json["appState"]!["gridSize"]);
        Assert.Equal("image/png", (string?)json["files"]!["img1"]!["mimeType"]);
        var elements = json["elements"]!.AsArray();
        Assert.Equal(3, elements.Count);
        Assert.Equal("keep-me", (string?)elements[0]!["customData"]!["plugin"]);
        Assert.Equal(120, (double)elements[0]!["x"]!);
        Assert.Equal(4, (int?)elements[0]!["version"]);
        Assert.Equal(5, (int?)elements[1]!["fontFamily"]);
        Assert.True((bool)elements[2]!["isDeleted"]!);

        // 다시 읽어도 같다.
        var again = ExcalidrawDocument.Parse(doc.Serialize());
        Assert.Equal(doc.Serialize(), again.Serialize());
    }

    [Fact]
    public void RejectsNonExcalidraw()
    {
        Assert.Throws<FormatException>(() => ExcalidrawDocument.Parse("""{"type":"tldraw"}"""));
        Assert.Throws<FormatException>(() => ExcalidrawDocument.Parse("not json"));
    }

    [Fact]
    public void NewElementsHaveExcalidrawDefaults()
    {
        var random = new Random(5);
        var arrow = ExcalidrawElement.Create(ExcalidrawElement.Arrow, 10, 20, 30, 40, random);
        Assert.Equal(21, arrow.Id.Length);
        Assert.Equal("arrow", arrow.EndArrowhead);
        Assert.Equal([new Pt(0, 0), new Pt(30, 40)], arrow.Points);
        Assert.Null(arrow.StartBinding);
        var text = ExcalidrawElement.Create(ExcalidrawElement.Text, 0, 0, 0, 0, random);
        Assert.Equal(20, text.FontSize);
        Assert.Null(text.ContainerId);
    }

    [Fact]
    public void HitTestPerShape()
    {
        var random = new Random(1);
        var rect = ExcalidrawElement.Create(ExcalidrawElement.Rectangle, 0, 0, 100, 50, random);
        Assert.True(DrawingGeometry.HitTest(rect, new Pt(50, 25)));
        Assert.True(DrawingGeometry.HitTest(rect, new Pt(103, 25)));
        Assert.False(DrawingGeometry.HitTest(rect, new Pt(130, 25)));

        var diamond = ExcalidrawElement.Create(ExcalidrawElement.Diamond, 0, 0, 100, 100, random);
        Assert.True(DrawingGeometry.HitTest(diamond, new Pt(50, 50)));
        Assert.False(DrawingGeometry.HitTest(diamond, new Pt(5, 5))); // 모서리 바깥

        var ellipse = ExcalidrawElement.Create(ExcalidrawElement.Ellipse, 0, 0, 100, 100, random);
        Assert.True(DrawingGeometry.HitTest(ellipse, new Pt(50, 95)));
        Assert.False(DrawingGeometry.HitTest(ellipse, new Pt(3, 3)));

        var line = ExcalidrawElement.Create(ExcalidrawElement.Line, 0, 0, 100, 0, random);
        Assert.True(DrawingGeometry.HitTest(line, new Pt(50, 3)));
        Assert.False(DrawingGeometry.HitTest(line, new Pt(50, 30)));
    }

    [Fact]
    public void ArrowFollowsBoundShape()
    {
        var random = new Random(2);
        var doc = ExcalidrawDocument.CreateEmpty();
        var a = ExcalidrawElement.Create(ExcalidrawElement.Rectangle, 0, 0, 100, 100, random);
        var b = ExcalidrawElement.Create(ExcalidrawElement.Rectangle, 300, 0, 100, 100, random);
        doc.Elements.AddRange([a, b]);
        var arrow = ExcalidrawElement.Create(ExcalidrawElement.Arrow, 0, 0, 0, 0, random);
        DrawingGeometry.SetAbsolutePoints(arrow, [new Pt(50, 50), new Pt(350, 50)]);
        doc.Elements.Add(arrow);

        Assert.Same(a, DrawingGeometry.FindBindTarget(doc, new Pt(50, 50)));
        DrawingGeometry.Bind(doc, arrow, start: true, a);
        DrawingGeometry.Bind(doc, arrow, start: false, b);
        DrawingGeometry.UpdateArrowEnds(doc, arrow);

        var points = DrawingGeometry.AbsolutePoints(arrow);
        Assert.Equal(106, points[0].X, 3); // 오른쪽 변 + 간격 6
        Assert.Equal(294, points[^1].X, 3);
        Assert.Contains(a.BoundElements, r => r.Id == arrow.Id && r.Type == "arrow");

        // 아래 도형을 아래로 옮기면 화살표 끝이 따라간다.
        b.Y = 300;
        var changed = DrawingGeometry.UpdateBoundArrows(doc, [b]);
        Assert.Single(changed);
        points = DrawingGeometry.AbsolutePoints(arrow);
        Assert.True(points[^1].Y > 200, $"end {points[^1]}");
        Assert.True(DrawingGeometry.Bounds(b).Inflate(10).Contains(points[^1]));

        // 떼면 도형의 기록도 지운다.
        DrawingGeometry.Bind(doc, arrow, start: false, null);
        Assert.Null(arrow.EndBinding);
        Assert.DoesNotContain(b.BoundElements, r => r.Id == arrow.Id);
    }

    [Fact]
    public void ArrowSticksToConnectionPoint()
    {
        var random = new Random(3);
        var doc = ExcalidrawDocument.CreateEmpty();
        var a = ExcalidrawElement.Create(ExcalidrawElement.Rectangle, 0, 0, 100, 60, random);
        var b = ExcalidrawElement.Create(ExcalidrawElement.Diamond, 300, 200, 120, 80, random);
        doc.Elements.AddRange([a, b]);

        // 연결점: 위·오른쪽·아래·왼쪽 가운데, 마름모는 꼭짓점.
        Assert.Equal([new Pt(50, 0), new Pt(100, 30), new Pt(50, 60), new Pt(0, 30)], DrawingGeometry.ConnectionPoints(a).Select(c => c.World));
        Assert.Equal(new Pt(300 + 61, 200), DrawingGeometry.ConnectionPoints(b)[0].World);

        // 아래 가운데 근처를 누르면 그 연결점을 찾는다.
        var snap = DrawingGeometry.FindConnectionPoint(doc, new Pt(53, 64), 10);
        Assert.NotNull(snap);
        Assert.Same(a, snap.Value.Shape);
        Assert.Equal(new Pt(0.5, 1), snap.Value.Ratio);

        var arrow = ExcalidrawElement.Create(ExcalidrawElement.Arrow, 0, 0, 0, 0, random);
        DrawingGeometry.SetAbsolutePoints(arrow, [new Pt(50, 60), new Pt(361, 200)]);
        doc.Elements.Add(arrow);
        DrawingGeometry.Bind(doc, arrow, start: true, a, new Pt(0.5, 1));
        DrawingGeometry.Bind(doc, arrow, start: false, b, new Pt(DrawingGeometry.ConnectionPoints(b)[0].Ratio.X, 0));
        Assert.Equal(new Pt(0.5, 1), arrow.StartBinding!.FixedPoint);
        Assert.Contains("fixedPoint", arrow.Node["startBinding"]!.ToJsonString());

        DrawingGeometry.UpdateArrowEnds(doc, arrow);
        var points = DrawingGeometry.AbsolutePoints(arrow);
        Assert.Equal(new Pt(50, 60 + DrawingGeometry.ConnectionGap), points[0]);
        Assert.Equal(361, points[^1].X, 6);
        Assert.Equal(200 - DrawingGeometry.ConnectionGap, points[^1].Y, 6);

        // 도형을 옮겨도 같은 연결점을 따라간다.
        a.X = 500;
        DrawingGeometry.UpdateBoundArrows(doc, [a]);
        Assert.Equal(new Pt(550, 60 + DrawingGeometry.ConnectionGap), DrawingGeometry.AbsolutePoints(arrow)[0]);

        // 파일로 썼다가 읽어도 연결점이 남는다.
        var reloaded = ExcalidrawDocument.Parse(doc.Serialize()).Find(arrow.Id)!;
        Assert.Equal(new Pt(0.5, 1), reloaded.StartBinding!.FixedPoint);
    }

    [Fact]
    public void CurvedArrowFollowsCurve()
    {
        var random = new Random(4);
        var arrow = ExcalidrawElement.Create(ExcalidrawElement.Arrow, 0, 0, 0, 0, random);
        DrawingGeometry.SetAbsolutePoints(arrow, [new Pt(0, 0), new Pt(100, 100), new Pt(200, 0)]);
        arrow.Roundness = new Roundness(2);

        // 곡선은 가운데 점을 지난다.
        Assert.Equal(new Pt(100, 100), DrawingGeometry.CurvePoint(DrawingGeometry.AbsolutePoints(arrow), 0, 1));
        Assert.True(DrawingGeometry.HitTest(arrow, new Pt(100, 99), 3));
        // 곧은 두 선의 가운데(50, 50)는 곡선에서 벗어나 있다.
        Assert.False(DrawingGeometry.HitTest(arrow, new Pt(50, 50), 3));
        arrow.Roundness = null;
        Assert.True(DrawingGeometry.HitTest(arrow, new Pt(50, 50), 3));

        // 굽히기 손잡이 자리: 곡선이면 곡선 위.
        arrow.Roundness = new Roundness(2);
        var mid = DrawingGeometry.SegmentMidpoint(arrow, 0);
        Assert.True(mid.Y > 55, $"{mid}");
    }

    [Fact]
    public void NextStepAddsConnectedShapeAndAvoidsOverlap()
    {
        var random = new Random(3);
        var doc = ExcalidrawDocument.CreateEmpty();
        var start = ExcalidrawElement.Create(ExcalidrawElement.Diamond, 0, 0, 120, 80, random);
        start.BackgroundColor = "#ffec99";
        doc.Elements.Add(start);

        var (next, arrow) = DrawingGeometry.AddNextStep(doc, start, StepDirection.Right, random);
        Assert.Equal("diamond", next.Type);
        Assert.Equal("#ffec99", next.BackgroundColor);
        Assert.Equal(120 + DrawingGeometry.FlowGap, next.X);
        Assert.Equal(start.Id, arrow.StartBinding!.ElementId);
        Assert.Equal(next.Id, arrow.EndBinding!.ElementId);

        // 같은 방향으로 한 번 더: 겹치지 않게 비켜 놓는다.
        var (third, _) = DrawingGeometry.AddNextStep(doc, start, StepDirection.Right, random);
        Assert.False(DrawingGeometry.Bounds(third).Intersects(DrawingGeometry.Bounds(next)));
    }

    [Fact]
    public void RoughLinesAreDeterministic()
    {
        Pt[] square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        var first = DrawingGeometry.RoughPolyline(square, closed: true, roughness: 1, seed: 9);
        var second = DrawingGeometry.RoughPolyline(square, closed: true, roughness: 1, seed: 9);
        Assert.Equal(2, first.Count);
        Assert.Equal(first[0], second[0]);
        Assert.All(first.SelectMany(s => s), p => Assert.True(p.X is > -10 and < 110 && p.Y is > -10 and < 110));
        Assert.Single(DrawingGeometry.RoughPolyline(square, closed: true, roughness: 0, seed: 9));
    }

    [Fact]
    public void StoreCreatesRenamesAndTrashes()
    {
        using var temp = new TempDirectory();
        var store = new DrawingStore(temp.Combine("Drawings"));
        var path = store.Create("업무 흐름");
        var duplicate = store.Create("업무 흐름");
        Assert.EndsWith("업무 흐름 (2).excalidraw", duplicate);
        Assert.Equal(2, store.List().Count);

        var doc = store.Load(path);
        doc.Elements.Add(ExcalidrawElement.Create(ExcalidrawElement.Rectangle, 0, 0, 10, 10, new Random(1)));
        store.Save(path, doc);
        Assert.Single(store.Load(path).Elements);

        var renamed = store.Rename(path, "결재: 절차?");
        Assert.Equal("결재_ 절차_", DrawingStore.NameOf(renamed));
        store.Delete(renamed);
        Assert.Single(store.List());
        Assert.Single(Directory.GetFiles(temp.Combine("Drawings", DrawingStore.TrashFolder)));
    }
}
