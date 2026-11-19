namespace DayDeck.Core.Drawings.Rough;

// rough.js 4.6.4 (MIT, Preet Shihn)를 C#으로 옮긴 것. Excalidraw와 같은 손그림 모양이 나오도록
// 난수 순서·계산식을 원본 그대로 따른다 (같은 seed면 같은 좌표). 원본: https://github.com/rough-stuff/rough

public enum RoughOpKind
{
    Move,
    BCurveTo,
    LineTo,
}

public readonly record struct RoughOp(RoughOpKind Kind, double[] Data);

public enum OpSetType
{
    Path,
    FillPath,
    FillSketch,
}

public sealed class OpSet(OpSetType type, List<RoughOp> ops)
{
    public OpSetType Type { get; set; } = type;
    public List<RoughOp> Ops { get; set; } = ops;
}

/// <summary>그리기 결과: 채우기·선 묶음과 그때 쓴 옵션.</summary>
public sealed class Drawable(string shape, List<OpSet> sets, RoughOptions options)
{
    public string Shape { get; set; } = shape;
    public List<OpSet> Sets { get; } = sets;
    public RoughOptions Options { get; } = options;
}

/// <summary>rough.js의 Random: Park–Miller (48271) 선형 합동 생성기. seed가 0이면 무작위.</summary>
public sealed class RoughRandom(int seed)
{
    private int _seed = seed;
    private static readonly Random Fallback = new();

    public double Next()
    {
        if (_seed != 0)
        {
            _seed = unchecked(48271 * _seed);
            return (int.MaxValue & _seed) / 2147483648.0;
        }
        return Fallback.NextDouble();
    }
}

/// <summary>
/// rough.js 옵션. <see cref="Clone"/>은 JS의 <c>Object.assign({}, o)</c>처럼 얕은 복사라
/// 이미 만든 <see cref="Randomizer"/>를 함께 쓴다 (원본과 같은 난수 순서를 위해 중요).
/// </summary>
public sealed class RoughOptions
{
    public double MaxRandomnessOffset { get; set; } = 2;
    public double Roughness { get; set; } = 1;
    public double Bowing { get; set; } = 1;
    public string Stroke { get; set; } = "#000";
    public double StrokeWidth { get; set; } = 1;
    public double CurveTightness { get; set; }
    public double CurveFitting { get; set; } = 0.95;
    public double CurveStepCount { get; set; } = 9;
    public string? Fill { get; set; }
    public string FillStyle { get; set; } = "hachure";
    public double FillWeight { get; set; } = -1;
    public double HachureAngle { get; set; } = -41;
    public double HachureGap { get; set; } = -1;
    public double DashOffset { get; set; } = -1;
    public double DashGap { get; set; } = -1;
    public double ZigzagOffset { get; set; } = -1;
    public int Seed { get; set; }
    public bool DisableMultiStroke { get; set; }
    public bool DisableMultiStrokeFill { get; set; }
    public bool PreserveVertices { get; set; }
    public double FillShapeRoughnessGain { get; set; } = 0.8;
    public double[]? StrokeLineDash { get; set; }
    public RoughRandom? Randomizer { get; set; }

    public RoughOptions Clone() => (RoughOptions)MemberwiseClone();
}

/// <summary>rough.js renderer.js.</summary>
public static class RoughRenderer
{
    public static OpSet Line(double x1, double y1, double x2, double y2, RoughOptions o) =>
        new(OpSetType.Path, DoubleLine(x1, y1, x2, y2, o));

    public static OpSet LinearPath(IReadOnlyList<double[]> points, bool close, RoughOptions o)
    {
        var len = points.Count;
        if (len > 2)
        {
            var ops = new List<RoughOp>();
            for (var i = 0; i < len - 1; i++)
            {
                ops.AddRange(DoubleLine(points[i][0], points[i][1], points[i + 1][0], points[i + 1][1], o));
            }
            if (close)
            {
                ops.AddRange(DoubleLine(points[len - 1][0], points[len - 1][1], points[0][0], points[0][1], o));
            }
            return new OpSet(OpSetType.Path, ops);
        }
        if (len == 2)
        {
            return Line(points[0][0], points[0][1], points[1][0], points[1][1], o);
        }
        return new OpSet(OpSetType.Path, []);
    }

    public static OpSet Polygon(IReadOnlyList<double[]> points, RoughOptions o) => LinearPath(points, true, o);

    public static OpSet Rectangle(double x, double y, double width, double height, RoughOptions o) =>
        Polygon([[x, y], [x + width, y], [x + width, y + height], [x, y + height]], o);

    public static OpSet Curve(IReadOnlyList<double[]> points, RoughOptions o)
    {
        var o1 = CurveWithOffset(points, 1 * (1 + o.Roughness * 0.2), o);
        if (!o.DisableMultiStroke)
        {
            var o2 = CurveWithOffset(points, 1.5 * (1 + o.Roughness * 0.22), CloneOptionsAlterSeed(o));
            o1.AddRange(o2);
        }
        return new OpSet(OpSetType.Path, o1);
    }

    public readonly record struct EllipseParams(double Increment, double Rx, double Ry);

    public static EllipseParams GenerateEllipseParams(double width, double height, RoughOptions o)
    {
        var psq = Math.Sqrt(Math.PI * 2 * Math.Sqrt((Math.Pow(width / 2, 2) + Math.Pow(height / 2, 2)) / 2));
        var stepCount = Math.Ceiling(Math.Max(o.CurveStepCount, o.CurveStepCount / Math.Sqrt(200) * psq));
        var increment = Math.PI * 2 / stepCount;
        var rx = Math.Abs(width / 2);
        var ry = Math.Abs(height / 2);
        var curveFitRandomness = 1 - o.CurveFitting;
        rx += OffsetOpt(rx * curveFitRandomness, o);
        ry += OffsetOpt(ry * curveFitRandomness, o);
        return new EllipseParams(increment, rx, ry);
    }

    public static (OpSet OpSet, List<double[]> EstimatedPoints) EllipseWithParams(double x, double y, RoughOptions o, EllipseParams p)
    {
        var (ap1, cp1) = ComputeEllipsePoints(p.Increment, x, y, p.Rx, p.Ry, 1, p.Increment * Offset(0.1, Offset(0.4, 1, o), o), o);
        var o1 = CurveOps(ap1, null, o);
        if (!o.DisableMultiStroke && o.Roughness != 0)
        {
            var (ap2, _) = ComputeEllipsePoints(p.Increment, x, y, p.Rx, p.Ry, 1.5, 0, o);
            o1.AddRange(CurveOps(ap2, null, o));
        }
        return (new OpSet(OpSetType.Path, o1), cp1);
    }

    public static OpSet SvgPath(IReadOnlyList<PathSeg> segments, RoughOptions o)
    {
        var ops = new List<RoughOp>();
        double[] first = [0, 0];
        double[] current = [0, 0];
        foreach (var (key, data) in PathData.Normalize(segments))
        {
            switch (key)
            {
                case 'M':
                    current = [data[0], data[1]];
                    first = [data[0], data[1]];
                    break;
                case 'L':
                    ops.AddRange(DoubleLine(current[0], current[1], data[0], data[1], o));
                    current = [data[0], data[1]];
                    break;
                case 'C':
                    ops.AddRange(BezierTo(data[0], data[1], data[2], data[3], data[4], data[5], current, o));
                    current = [data[4], data[5]];
                    break;
                case 'Z':
                    ops.AddRange(DoubleLine(current[0], current[1], first[0], first[1], o));
                    current = [first[0], first[1]];
                    break;
            }
        }
        return new OpSet(OpSetType.Path, ops);
    }

    public static OpSet SolidFillPolygon(IReadOnlyList<List<double[]>> polygonList, RoughOptions o)
    {
        var ops = new List<RoughOp>();
        foreach (var points in polygonList)
        {
            if (points.Count == 0)
            {
                continue;
            }
            var offset = o.MaxRandomnessOffset;
            var len = points.Count;
            if (len > 2)
            {
                ops.Add(new RoughOp(RoughOpKind.Move, [points[0][0] + OffsetOpt(offset, o), points[0][1] + OffsetOpt(offset, o)]));
                for (var i = 1; i < len; i++)
                {
                    ops.Add(new RoughOp(RoughOpKind.LineTo, [points[i][0] + OffsetOpt(offset, o), points[i][1] + OffsetOpt(offset, o)]));
                }
            }
        }
        return new OpSet(OpSetType.FillPath, ops);
    }

    public static OpSet PatternFillPolygons(IReadOnlyList<List<double[]>> polygonList, RoughOptions o) =>
        RoughFillers.Fill(polygonList, o);

    public static double RandOffset(double x, RoughOptions o) => OffsetOpt(x, o);

    public static double RandOffsetWithRange(double min, double max, RoughOptions o) => Offset(min, max, o);

    public static List<RoughOp> DoubleLineFillOps(double x1, double y1, double x2, double y2, RoughOptions o) =>
        DoubleLine(x1, y1, x2, y2, o, filling: true);

    // ---------- private helpers ----------

    private static RoughOptions CloneOptionsAlterSeed(RoughOptions o)
    {
        var result = o.Clone();
        result.Randomizer = null;
        if (o.Seed != 0)
        {
            result.Seed = o.Seed + 1;
        }
        return result;
    }

    internal static double Random(RoughOptions o)
    {
        o.Randomizer ??= new RoughRandom(o.Seed);
        return o.Randomizer.Next();
    }

    private static double Offset(double min, double max, RoughOptions o, double roughnessGain = 1) =>
        o.Roughness * roughnessGain * (Random(o) * (max - min) + min);

    private static double OffsetOpt(double x, RoughOptions o, double roughnessGain = 1) => Offset(-x, x, o, roughnessGain);

    private static List<RoughOp> DoubleLine(double x1, double y1, double x2, double y2, RoughOptions o, bool filling = false)
    {
        var singleStroke = filling ? o.DisableMultiStrokeFill : o.DisableMultiStroke;
        var o1 = LineOps(x1, y1, x2, y2, o, true, false);
        if (singleStroke)
        {
            return o1;
        }
        o1.AddRange(LineOps(x1, y1, x2, y2, o, true, true));
        return o1;
    }

    private static List<RoughOp> LineOps(double x1, double y1, double x2, double y2, RoughOptions o, bool move, bool overlay)
    {
        var lengthSq = Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2);
        var length = Math.Sqrt(lengthSq);
        double roughnessGain;
        if (length < 200)
        {
            roughnessGain = 1;
        }
        else if (length > 500)
        {
            roughnessGain = 0.4;
        }
        else
        {
            roughnessGain = -0.0016668 * length + 1.233334;
        }
        var offset = o.MaxRandomnessOffset;
        if (offset * offset * 100 > lengthSq)
        {
            offset = length / 10;
        }
        var halfOffset = offset / 2;
        var divergePoint = 0.2 + Random(o) * 0.2;
        var midDispX = o.Bowing * o.MaxRandomnessOffset * (y2 - y1) / 200;
        var midDispY = o.Bowing * o.MaxRandomnessOffset * (x1 - x2) / 200;
        midDispX = OffsetOpt(midDispX, o, roughnessGain);
        midDispY = OffsetOpt(midDispY, o, roughnessGain);
        var ops = new List<RoughOp>();
        double RandomHalf() => OffsetOpt(halfOffset, o, roughnessGain);
        double RandomFull() => OffsetOpt(offset, o, roughnessGain);
        var preserve = o.PreserveVertices;
        if (move)
        {
            if (overlay)
            {
                var mx = x1 + (preserve ? 0 : RandomHalf());
                var my = y1 + (preserve ? 0 : RandomHalf());
                ops.Add(new RoughOp(RoughOpKind.Move, [mx, my]));
            }
            else
            {
                var mx = x1 + (preserve ? 0 : OffsetOpt(offset, o, roughnessGain));
                var my = y1 + (preserve ? 0 : OffsetOpt(offset, o, roughnessGain));
                ops.Add(new RoughOp(RoughOpKind.Move, [mx, my]));
            }
        }
        Func<double> r = overlay ? RandomHalf : RandomFull;
        var c1x = midDispX + x1 + (x2 - x1) * divergePoint + r();
        var c1y = midDispY + y1 + (y2 - y1) * divergePoint + r();
        var c2x = midDispX + x1 + 2 * (x2 - x1) * divergePoint + r();
        var c2y = midDispY + y1 + 2 * (y2 - y1) * divergePoint + r();
        var ex = x2 + (preserve ? 0 : r());
        var ey = y2 + (preserve ? 0 : r());
        ops.Add(new RoughOp(RoughOpKind.BCurveTo, [c1x, c1y, c2x, c2y, ex, ey]));
        return ops;
    }

    private static List<RoughOp> CurveWithOffset(IReadOnlyList<double[]> points, double offset, RoughOptions o)
    {
        var ps = new List<double[]>
        {
            new[] { points[0][0] + OffsetOpt(offset, o), points[0][1] + OffsetOpt(offset, o) },
            new[] { points[0][0] + OffsetOpt(offset, o), points[0][1] + OffsetOpt(offset, o) },
        };
        for (var i = 1; i < points.Count; i++)
        {
            ps.Add([points[i][0] + OffsetOpt(offset, o), points[i][1] + OffsetOpt(offset, o)]);
            if (i == points.Count - 1)
            {
                ps.Add([points[i][0] + OffsetOpt(offset, o), points[i][1] + OffsetOpt(offset, o)]);
            }
        }
        return CurveOps(ps, null, o);
    }

    private static List<RoughOp> CurveOps(IReadOnlyList<double[]> points, double[]? closePoint, RoughOptions o)
    {
        var len = points.Count;
        var ops = new List<RoughOp>();
        if (len > 3)
        {
            var s = 1 - o.CurveTightness;
            ops.Add(new RoughOp(RoughOpKind.Move, [points[1][0], points[1][1]]));
            for (var i = 1; i + 2 < len; i++)
            {
                var c = points[i];
                double[] b1 = [c[0] + (s * points[i + 1][0] - s * points[i - 1][0]) / 6, c[1] + (s * points[i + 1][1] - s * points[i - 1][1]) / 6];
                double[] b2 = [points[i + 1][0] + (s * points[i][0] - s * points[i + 2][0]) / 6, points[i + 1][1] + (s * points[i][1] - s * points[i + 2][1]) / 6];
                double[] b3 = [points[i + 1][0], points[i + 1][1]];
                ops.Add(new RoughOp(RoughOpKind.BCurveTo, [b1[0], b1[1], b2[0], b2[1], b3[0], b3[1]]));
            }
            if (closePoint is { Length: 2 })
            {
                var ro = o.MaxRandomnessOffset;
                ops.Add(new RoughOp(RoughOpKind.LineTo, [closePoint[0] + OffsetOpt(ro, o), closePoint[1] + OffsetOpt(ro, o)]));
            }
        }
        else if (len == 3)
        {
            ops.Add(new RoughOp(RoughOpKind.Move, [points[1][0], points[1][1]]));
            ops.Add(new RoughOp(RoughOpKind.BCurveTo, [points[1][0], points[1][1], points[2][0], points[2][1], points[2][0], points[2][1]]));
        }
        else if (len == 2)
        {
            ops.AddRange(DoubleLine(points[0][0], points[0][1], points[1][0], points[1][1], o));
        }
        return ops;
    }

    private static (List<double[]> All, List<double[]> Core) ComputeEllipsePoints(double increment, double cx, double cy, double rx, double ry,
        double offset, double overlap, RoughOptions o)
    {
        var coreOnly = o.Roughness == 0;
        var corePoints = new List<double[]>();
        var allPoints = new List<double[]>();
        if (coreOnly)
        {
            increment /= 4;
            allPoints.Add([cx + rx * Math.Cos(-increment), cy + ry * Math.Sin(-increment)]);
            for (var angle = 0.0; angle <= Math.PI * 2; angle += increment)
            {
                double[] p = [cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)];
                corePoints.Add(p);
                allPoints.Add(p);
            }
            allPoints.Add([cx + rx * Math.Cos(0), cy + ry * Math.Sin(0)]);
            allPoints.Add([cx + rx * Math.Cos(increment), cy + ry * Math.Sin(increment)]);
        }
        else
        {
            var radOffset = OffsetOpt(0.5, o) - Math.PI / 2;
            {
                var px = OffsetOpt(offset, o) + cx + 0.9 * rx * Math.Cos(radOffset - increment);
                var py = OffsetOpt(offset, o) + cy + 0.9 * ry * Math.Sin(radOffset - increment);
                allPoints.Add([px, py]);
            }
            var endAngle = Math.PI * 2 + radOffset - 0.01;
            for (var angle = radOffset; angle < endAngle; angle += increment)
            {
                var px = OffsetOpt(offset, o) + cx + rx * Math.Cos(angle);
                var py = OffsetOpt(offset, o) + cy + ry * Math.Sin(angle);
                double[] p = [px, py];
                corePoints.Add(p);
                allPoints.Add(p);
            }
            {
                var px = OffsetOpt(offset, o) + cx + rx * Math.Cos(radOffset + Math.PI * 2 + overlap * 0.5);
                var py = OffsetOpt(offset, o) + cy + ry * Math.Sin(radOffset + Math.PI * 2 + overlap * 0.5);
                allPoints.Add([px, py]);
            }
            {
                var px = OffsetOpt(offset, o) + cx + 0.98 * rx * Math.Cos(radOffset + overlap);
                var py = OffsetOpt(offset, o) + cy + 0.98 * ry * Math.Sin(radOffset + overlap);
                allPoints.Add([px, py]);
            }
            {
                var px = OffsetOpt(offset, o) + cx + 0.9 * rx * Math.Cos(radOffset + overlap * 0.5);
                var py = OffsetOpt(offset, o) + cy + 0.9 * ry * Math.Sin(radOffset + overlap * 0.5);
                allPoints.Add([px, py]);
            }
        }
        return (allPoints, corePoints);
    }

    private static List<RoughOp> BezierTo(double x1, double y1, double x2, double y2, double x, double y, double[] current, RoughOptions o)
    {
        var ops = new List<RoughOp>();
        var baseOffset = o.MaxRandomnessOffset != 0 ? o.MaxRandomnessOffset : 1;
        double[] ros = [baseOffset, baseOffset + 0.3];
        var iterations = o.DisableMultiStroke ? 1 : 2;
        var preserve = o.PreserveVertices;
        for (var i = 0; i < iterations; i++)
        {
            if (i == 0)
            {
                ops.Add(new RoughOp(RoughOpKind.Move, [current[0], current[1]]));
            }
            else
            {
                var mx = current[0] + (preserve ? 0 : OffsetOpt(ros[0], o));
                var my = current[1] + (preserve ? 0 : OffsetOpt(ros[0], o));
                ops.Add(new RoughOp(RoughOpKind.Move, [mx, my]));
            }
            double fx, fy;
            if (preserve)
            {
                fx = x;
                fy = y;
            }
            else
            {
                fx = x + OffsetOpt(ros[i], o);
                fy = y + OffsetOpt(ros[i], o);
            }
            var a = x1 + OffsetOpt(ros[i], o);
            var b = y1 + OffsetOpt(ros[i], o);
            var c = x2 + OffsetOpt(ros[i], o);
            var d = y2 + OffsetOpt(ros[i], o);
            ops.Add(new RoughOp(RoughOpKind.BCurveTo, [a, b, c, d, fx, fy]));
        }
        return ops;
    }
}
