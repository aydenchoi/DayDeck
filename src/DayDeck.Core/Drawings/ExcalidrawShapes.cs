using DayDeck.Core.Drawings.Rough;

namespace DayDeck.Core.Drawings;

/// <summary>
/// Excalidraw 0.18의 scene/Shape.ts를 옮긴 것: 요소마다 rough.js 옵션과 도형(모서리 둥글기, 마름모, 곡선 화살표, 화살촉)을
/// 같은 규칙으로 만든다. 결과 좌표는 요소 기준(요소의 x, y가 0, 0)이다.
/// </summary>
public static class ExcalidrawShapes
{
    public const double DefaultProportionalRadius = 0.25;
    public const double DefaultAdaptiveRadius = 32;
    public const double LineConfirmThreshold = 8;

    public static bool IsTransparent(string? color) =>
        color is null || color == "transparent"
        || (color.Length == 5 && color[4] == '0')
        || (color.Length == 9 && color[7..] == "00");

    public static bool CanChangeRoundness(string type) => type is "rectangle" or "iframe" or "embeddable" or "line" or "diamond" or "image";

    public static double[] DashedArray(double strokeWidth) => [8, 8 + strokeWidth];

    public static double[] DottedArray(double strokeWidth) => [1.5, 6 + strokeWidth];

    public static double AdjustRoughness(ExcalidrawElement e)
    {
        var roughness = e.Roughness;
        var maxSize = Math.Max(e.Width, e.Height);
        var minSize = Math.Min(e.Width, e.Height);
        if ((minSize >= 20 && maxSize >= 50)
            || (minSize >= 15 && e.Roundness is not null && CanChangeRoundness(e.Type))
            || (e.IsLinear && maxSize >= 50))
        {
            return roughness;
        }
        return Math.Min(roughness / (maxSize < 10 ? 3 : 2), 2.5);
    }

    public static bool IsPathALoop(IReadOnlyList<Pt> points) =>
        points.Count >= 3 && Math.Sqrt(Math.Pow(points[0].X - points[^1].X, 2) + Math.Pow(points[0].Y - points[^1].Y, 2)) <= LineConfirmThreshold;

    public static RoughOptions RoughOptionsFor(ExcalidrawElement e, bool continuousPath = false)
    {
        var options = new RoughOptions
        {
            Seed = e.Seed,
            StrokeLineDash = e.StrokeStyle == "dashed" ? DashedArray(e.StrokeWidth) : e.StrokeStyle == "dotted" ? DottedArray(e.StrokeWidth) : null,
            DisableMultiStroke = e.StrokeStyle != "solid",
            StrokeWidth = e.StrokeStyle != "solid" ? e.StrokeWidth + 0.5 : e.StrokeWidth,
            FillWeight = e.StrokeWidth / 2,
            HachureGap = e.StrokeWidth * 4,
            Roughness = AdjustRoughness(e),
            Stroke = e.StrokeColor,
            PreserveVertices = continuousPath || e.Roughness < 2,
        };
        switch (e.Type)
        {
            case "rectangle" or "iframe" or "embeddable" or "diamond" or "ellipse":
                options.FillStyle = e.FillStyle;
                options.Fill = IsTransparent(e.BackgroundColor) ? null : e.BackgroundColor;
                if (e.Type == "ellipse")
                {
                    options.CurveFitting = 1;
                }
                break;
            case "line" or "freedraw":
                if (IsPathALoop(e.Points))
                {
                    options.FillStyle = e.FillStyle;
                    options.Fill = e.BackgroundColor == "transparent" ? null : e.BackgroundColor;
                }
                break;
        }
        return options;
    }

    public static double CornerRadius(double x, ExcalidrawElement e)
    {
        if (e.Roundness is { Type: 1 or 2 })
        {
            return x * DefaultProportionalRadius;
        }
        if (e.Roundness is { Type: 3 } roundness)
        {
            var fixedRadius = roundness.Value ?? DefaultAdaptiveRadius;
            var cutoff = fixedRadius / DefaultProportionalRadius;
            return x <= cutoff ? x * DefaultProportionalRadius : fixedRadius;
        }
        return 0;
    }

    /// <summary>마름모 꼭짓점 (위, 오른쪽, 아래, 왼쪽). Excalidraw처럼 가운데를 정수로 내림 + 1.</summary>
    public static double[] DiamondPoints(ExcalidrawElement e)
    {
        var topX = Math.Floor(e.Width / 2) + 1;
        var rightY = Math.Floor(e.Height / 2) + 1;
        return [topX, 0, e.Width, rightY, topX, e.Height, 0, rightY];
    }

    /// <summary>요소의 손그림 도형 목록 (그릴 순서). 글자·그림·프레임은 비어 있다.</summary>
    public static List<Drawable> Generate(ExcalidrawElement e, string canvasBackground = "#ffffff")
    {
        switch (e.Type)
        {
            case "rectangle" or "iframe" or "embeddable":
            {
                if (e.Roundness is not null)
                {
                    var w = e.Width;
                    var h = e.Height;
                    var r = CornerRadius(Math.Min(w, h), e);
                    var path = SvgPathParser.Parse(Fmt($"M {r} 0 L {w - r} 0 Q {w} 0, {w} {r} L {w} {h - r} Q {w} {h}, {w - r} {h} L {r} {h} Q 0 {h}, 0 {h - r} L 0 {r} Q 0 0, {r} 0"));
                    return [RoughGenerator.Path(path, RoughOptionsFor(e, true))];
                }
                return [RoughGenerator.Rectangle(0, 0, e.Width, e.Height, RoughOptionsFor(e))];
            }
            case "diamond":
            {
                var p = DiamondPoints(e);
                var (topX, topY, rightX, rightY, bottomX, bottomY, leftX, leftY) = (p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
                if (e.Roundness is not null)
                {
                    var v = CornerRadius(Math.Abs(topX - leftX), e);
                    var hr = CornerRadius(Math.Abs(rightY - topY), e);
                    var path = SvgPathParser.Parse(Fmt(
                        $"M {topX + v} {topY + hr} L {rightX - v} {rightY - hr} C {rightX} {rightY}, {rightX} {rightY}, {rightX - v} {rightY + hr} L {bottomX + v} {bottomY - hr} C {bottomX} {bottomY}, {bottomX} {bottomY}, {bottomX - v} {bottomY - hr} L {leftX + v} {leftY + hr} C {leftX} {leftY}, {leftX} {leftY}, {leftX + v} {leftY - hr} L {topX - v} {topY + hr} C {topX} {topY}, {topX} {topY}, {topX + v} {topY + hr}"));
                    return [RoughGenerator.Path(path, RoughOptionsFor(e, true))];
                }
                return [RoughGenerator.Polygon([[topX, topY], [rightX, rightY], [bottomX, bottomY], [leftX, leftY]], RoughOptionsFor(e))];
            }
            case "ellipse":
                return [RoughGenerator.Ellipse(e.Width / 2, e.Height / 2, e.Width, e.Height, RoughOptionsFor(e))];
            case "line" or "arrow":
            {
                var options = RoughOptionsFor(e);
                var points = e.Points.Count > 0 ? e.Points.Select(p => new[] { p.X, p.Y }).ToList() : [[0, 0]];
                List<Drawable> shape;
                if (e.Roundness is null)
                {
                    shape = [options.Fill is not null ? RoughGenerator.Polygon(points, options) : RoughGenerator.LinearPath(points, options)];
                }
                else
                {
                    shape = [RoughGenerator.Curve(points, options)];
                }
                if (e.Type == "arrow")
                {
                    if (e.StartArrowhead is { } start)
                    {
                        shape.AddRange(ArrowheadShapes(e, shape, "start", start, options, canvasBackground));
                    }
                    // Excalidraw는 끝 화살촉이 비어 있으면(필드 없음) 기본 "arrow"다. null이면 없음.
                    var end = e.Node.ContainsKey("endArrowhead") ? e.EndArrowhead : "arrow";
                    if (end is not null)
                    {
                        shape.AddRange(ArrowheadShapes(e, shape, "end", end, options, canvasBackground));
                    }
                }
                return shape;
            }
            case "freedraw":
            {
                if (IsPathALoop(e.Points))
                {
                    var simplified = PointsOnCurve.Simplify([.. e.Points.Select(p => new[] { p.X, p.Y })], 0.75);
                    var options = RoughOptionsFor(e);
                    options.Stroke = "none";
                    return [RoughGenerator.Curve(simplified, options)];
                }
                return [];
            }
            default:
                return [];
        }
    }

    /// <summary>펜 획의 채울 외곽선 (선 색으로 채움, nonzero).</summary>
    public static List<PathSeg> FreedrawOutline(ExcalidrawElement e)
    {
        var points = e.Points;
        var pressures = e.Pressures;
        List<double[]> input = e.SimulatePressure
            ? [.. points.Select(p => new[] { p.X, p.Y })]
            : points.Count > 0
                ? [.. points.Select((p, i) => new[] { p.X, p.Y, i < pressures.Count ? pressures[i] : 0.5 })]
                : [[0, 0, 0.5]];
        if (input.Count == 0)
        {
            input = [[0, 0]];
        }
        return PerfectFreehand.FreedrawPath(input, e.StrokeWidth, e.SimulatePressure, e.HasLastCommittedPoint);
    }

    private static string Fmt(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int ArrowheadSize(string arrowhead) => arrowhead switch
    {
        "arrow" => 25,
        "diamond" or "diamond_outline" => 12,
        "crowfoot_many" or "crowfoot_one" or "crowfoot_one_or_many" => 20,
        _ => 15,
    };

    private static double ArrowheadAngle(string arrowhead) => arrowhead switch
    {
        "bar" => 90,
        "arrow" => 20,
        _ => 25,
    };

    private static List<RoughOp> CurvePathOps(Drawable shape)
    {
        foreach (var set in shape.Sets)
        {
            if (set.Type == OpSetType.Path)
            {
                return set.Ops;
            }
        }
        return shape.Sets.Count > 0 ? shape.Sets[0].Ops : [];
    }

    private static (double X, double Y) RotateRads(double x, double y, double cx, double cy, double angle) =>
        ((x - cx) * Math.Cos(angle) - (y - cy) * Math.Sin(angle) + cx, (x - cx) * Math.Sin(angle) + (y - cy) * Math.Cos(angle) + cy);

    /// <summary>getArrowheadPoints: 선의 끝 곡선 방향으로 화살촉 꼭짓점을 구한다.</summary>
    public static double[]? ArrowheadPoints(ExcalidrawElement e, List<Drawable> shape, string position, string arrowhead)
    {
        if (shape.Count < 1)
        {
            return null;
        }
        var ops = CurvePathOps(shape[0]);
        if (ops.Count < 1)
        {
            return null;
        }
        var index = position == "start" ? 1 : ops.Count - 1;
        if (index >= ops.Count || ops[index].Data.Length != 6)
        {
            return null;
        }
        var data = ops[index].Data;
        double[] p3 = [data[4], data[5]];
        double[] p2 = [data[2], data[3]];
        double[] p1 = [data[0], data[1]];
        var prev = ops[index - 1];
        double[] p0 = [0, 0];
        if (prev.Kind == RoughOpKind.Move)
        {
            p0 = [prev.Data[0], prev.Data[1]];
        }
        else if (prev.Kind == RoughOpKind.BCurveTo)
        {
            p0 = [prev.Data[4], prev.Data[5]];
        }
        double Eq(double t, int i) =>
            Math.Pow(1 - t, 3) * p3[i] + 3 * t * Math.Pow(1 - t, 2) * p2[i] + 3 * Math.Pow(t, 2) * (1 - t) * p1[i] + p0[i] * Math.Pow(t, 3);
        var (x2, y2) = position == "start" ? (p0[0], p0[1]) : (p3[0], p3[1]);
        var (x1, y1) = (Eq(0.3, 0), Eq(0.3, 1));
        var distance = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        var nx = (x2 - x1) / distance;
        var ny = (y2 - y1) / distance;
        var size = ArrowheadSize(arrowhead);
        var pts = e.Points;
        double length;
        {
            var c = position == "end" ? pts[^1] : pts[0];
            var p = pts.Count > 1 ? (position == "end" ? pts[^2] : pts[1]) : new Pt(0, 0);
            length = Math.Sqrt(Math.Pow(c.X - p.X, 2) + Math.Pow(c.Y - p.Y, 2));
        }
        var lengthMultiplier = arrowhead is "diamond" or "diamond_outline" ? 0.25 : 0.5;
        var minSize = Math.Min(size, length * lengthMultiplier);
        var xs = x2 - nx * minSize;
        var ys = y2 - ny * minSize;
        if (arrowhead is "dot" or "circle" or "circle_outline")
        {
            var diameter = Math.Sqrt(Math.Pow(ys - y2, 2) + Math.Pow(xs - x2, 2)) + e.StrokeWidth - 2;
            return [x2, y2, diameter];
        }
        var angle = ArrowheadAngle(arrowhead);
        if (arrowhead is "crowfoot_many" or "crowfoot_one_or_many")
        {
            var (cx3, cy3) = RotateRads(x2, y2, xs, ys, -angle * Math.PI / 180);
            var (cx4, cy4) = RotateRads(x2, y2, xs, ys, angle * Math.PI / 180);
            return [xs, ys, cx3, cy3, cx4, cy4];
        }
        var (x3, y3) = RotateRads(xs, ys, x2, y2, -angle * Math.PI / 180);
        var (x4, y4) = RotateRads(xs, ys, x2, y2, angle * Math.PI / 180);
        if (arrowhead is "diamond" or "diamond_outline")
        {
            double ox, oy;
            if (position == "start")
            {
                var p = pts.Count > 1 ? pts[1] : new Pt(0, 0);
                (ox, oy) = RotateRads(x2 + minSize * 2, y2, x2, y2, Math.Atan2(p.Y - y2, p.X - x2));
            }
            else
            {
                var p = pts.Count > 1 ? pts[^2] : new Pt(0, 0);
                (ox, oy) = RotateRads(x2 - minSize * 2, y2, x2, y2, Math.Atan2(y2 - p.Y, x2 - p.X));
            }
            return [x2, y2, x3, y3, ox, oy, x4, y4];
        }
        return [x2, y2, x3, y3, x4, y4];
    }

    private static List<Drawable> ArrowheadShapes(ExcalidrawElement e, List<Drawable> shape, string position, string arrowhead,
        RoughOptions options, string canvasBackground)
    {
        var points = ArrowheadPoints(e, shape, position, arrowhead);
        if (points is null)
        {
            return [];
        }
        switch (arrowhead)
        {
            case "dot" or "circle" or "circle_outline":
            {
                options.StrokeLineDash = null;
                var o = options.Clone();
                o.Fill = arrowhead == "circle_outline" ? canvasBackground : e.StrokeColor;
                o.FillStyle = "solid";
                o.Stroke = e.StrokeColor;
                o.Roughness = Math.Min(0.5, options.Roughness);
                return [RoughGenerator.Circle(points[0], points[1], points[2], o)];
            }
            case "triangle" or "triangle_outline":
            {
                options.StrokeLineDash = null;
                var o = options.Clone();
                o.Fill = arrowhead == "triangle_outline" ? canvasBackground : e.StrokeColor;
                o.FillStyle = "solid";
                o.Roughness = Math.Min(1, options.Roughness);
                return [RoughGenerator.Polygon([[points[0], points[1]], [points[2], points[3]], [points[4], points[5]], [points[0], points[1]]], o)];
            }
            case "diamond" or "diamond_outline":
            {
                options.StrokeLineDash = null;
                var o = options.Clone();
                o.Fill = arrowhead == "diamond_outline" ? canvasBackground : e.StrokeColor;
                o.FillStyle = "solid";
                o.Roughness = Math.Min(1, options.Roughness);
                return [RoughGenerator.Polygon(
                    [[points[0], points[1]], [points[2], points[3]], [points[4], points[5]], [points[6], points[7]], [points[0], points[1]]], o)];
            }
            case "crowfoot_one":
                return [RoughGenerator.Line(points[2], points[3], points[4], points[5], options)];
            default:
            {
                if (e.StrokeStyle == "dotted")
                {
                    var dash = DottedArray(e.StrokeWidth - 1);
                    options.StrokeLineDash = [dash[0], dash[1] - 1];
                }
                else
                {
                    options.StrokeLineDash = null;
                }
                options.Roughness = Math.Min(1, options.Roughness);
                var result = new List<Drawable>
                {
                    RoughGenerator.Line(points[2], points[3], points[0], points[1], options),
                    RoughGenerator.Line(points[4], points[5], points[0], points[1], options),
                };
                if (arrowhead == "crowfoot_one_or_many" && ArrowheadPoints(e, shape, position, "crowfoot_one") is { } one)
                {
                    result.Add(RoughGenerator.Line(one[2], one[3], one[4], one[5], options));
                }
                return result;
            }
        }
    }
}
