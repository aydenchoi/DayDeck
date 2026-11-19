namespace DayDeck.Core.Drawings.Rough;

/// <summary>SVG 경로 한 조각 (절대 좌표). M, L, C, Q, Z만 쓴다.</summary>
public readonly record struct PathSeg(char Key, double[] Data);

/// <summary>path-data-parser의 normalize (Q → C). 입력은 이미 절대 좌표.</summary>
public static class PathData
{
    public static List<PathSeg> Normalize(IReadOnlyList<PathSeg> segments)
    {
        var output = new List<PathSeg>();
        double cx = 0, cy = 0, subx = 0, suby = 0;
        foreach (var (key, data) in segments)
        {
            switch (key)
            {
                case 'M':
                    output.Add(new PathSeg('M', [.. data]));
                    (cx, cy) = (data[0], data[1]);
                    (subx, suby) = (data[0], data[1]);
                    break;
                case 'C':
                    output.Add(new PathSeg('C', [.. data]));
                    cx = data[4];
                    cy = data[5];
                    break;
                case 'L':
                    output.Add(new PathSeg('L', [.. data]));
                    (cx, cy) = (data[0], data[1]);
                    break;
                case 'Q':
                {
                    var (x1, y1, x, y) = (data[0], data[1], data[2], data[3]);
                    var cx1 = cx + 2 * (x1 - cx) / 3;
                    var cy1 = cy + 2 * (y1 - cy) / 3;
                    var cx2 = x + 2 * (x1 - x) / 3;
                    var cy2 = y + 2 * (y1 - y) / 3;
                    output.Add(new PathSeg('C', [cx1, cy1, cx2, cy2, x, y]));
                    cx = x;
                    cy = y;
                    break;
                }
                case 'Z':
                    output.Add(new PathSeg('Z', []));
                    cx = subx;
                    cy = suby;
                    break;
            }
        }
        return output;
    }
}

/// <summary>points-on-curve / points-on-path (MIT, Preet Shihn).</summary>
public static class PointsOnCurve
{
    private static double DistanceSq(double[] p1, double[] p2) => Math.Pow(p1[0] - p2[0], 2) + Math.Pow(p1[1] - p2[1], 2);

    private static double Distance(double[] p1, double[] p2) => Math.Sqrt(DistanceSq(p1, p2));

    private static double[] Lerp(double[] a, double[] b, double t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];

    private static double DistanceToSegmentSq(double[] p, double[] v, double[] w)
    {
        var l2 = DistanceSq(v, w);
        if (l2 == 0)
        {
            return DistanceSq(p, v);
        }
        var t = ((p[0] - v[0]) * (w[0] - v[0]) + (p[1] - v[1]) * (w[1] - v[1])) / l2;
        t = Math.Max(0, Math.Min(1, t));
        return DistanceSq(p, Lerp(v, w, t));
    }

    private static double Flatness(IReadOnlyList<double[]> points, int offset)
    {
        var p1 = points[offset];
        var p2 = points[offset + 1];
        var p3 = points[offset + 2];
        var p4 = points[offset + 3];
        var ux = 3 * p2[0] - 2 * p1[0] - p4[0];
        ux *= ux;
        var uy = 3 * p2[1] - 2 * p1[1] - p4[1];
        uy *= uy;
        var vx = 3 * p3[0] - 2 * p4[0] - p1[0];
        vx *= vx;
        var vy = 3 * p3[1] - 2 * p4[1] - p1[1];
        vy *= vy;
        if (ux < vx)
        {
            ux = vx;
        }
        if (uy < vy)
        {
            uy = vy;
        }
        return ux + uy;
    }

    private static void PointsOnBezierWithSplitting(IReadOnlyList<double[]> points, int offset, double tolerance, List<double[]> outPoints)
    {
        if (Flatness(points, offset) < tolerance)
        {
            var p0 = points[offset];
            if (outPoints.Count > 0)
            {
                if (Distance(outPoints[^1], p0) > 1)
                {
                    outPoints.Add(p0);
                }
            }
            else
            {
                outPoints.Add(p0);
            }
            outPoints.Add(points[offset + 3]);
        }
        else
        {
            const double t = .5;
            var p1 = points[offset];
            var p2 = points[offset + 1];
            var p3 = points[offset + 2];
            var p4 = points[offset + 3];
            var q1 = Lerp(p1, p2, t);
            var q2 = Lerp(p2, p3, t);
            var q3 = Lerp(p3, p4, t);
            var r1 = Lerp(q1, q2, t);
            var r2 = Lerp(q2, q3, t);
            var red = Lerp(r1, r2, t);
            PointsOnBezierWithSplitting([p1, q1, r1, red], 0, tolerance, outPoints);
            PointsOnBezierWithSplitting([red, r2, q3, p4], 0, tolerance, outPoints);
        }
    }

    public static List<double[]> Simplify(List<double[]> points, double distance) => SimplifyPoints(points, 0, points.Count, distance, []);

    private static List<double[]> SimplifyPoints(List<double[]> points, int start, int end, double epsilon, List<double[]> outPoints)
    {
        var s = points[start];
        var e = points[end - 1];
        double maxDistSq = 0;
        var maxNdx = 1;
        for (var i = start + 1; i < end - 1; ++i)
        {
            var distSq = DistanceToSegmentSq(points[i], s, e);
            if (distSq > maxDistSq)
            {
                maxDistSq = distSq;
                maxNdx = i;
            }
        }
        if (Math.Sqrt(maxDistSq) > epsilon)
        {
            SimplifyPoints(points, start, maxNdx + 1, epsilon, outPoints);
            SimplifyPoints(points, maxNdx, end, epsilon, outPoints);
        }
        else
        {
            if (outPoints.Count == 0)
            {
                outPoints.Add(s);
            }
            outPoints.Add(e);
        }
        return outPoints;
    }

    public static List<double[]> PointsOnBezierCurves(IReadOnlyList<double[]> points, double tolerance = 0.15, double distance = 0)
    {
        var newPoints = new List<double[]>();
        var numSegments = (points.Count - 1) / 3;
        for (var i = 0; i < numSegments; i++)
        {
            PointsOnBezierWithSplitting(points, i * 3, tolerance, newPoints);
        }
        if (distance > 0)
        {
            return SimplifyPoints(newPoints, 0, newPoints.Count, distance, []);
        }
        return newPoints;
    }

    /// <summary>curve-to-bezier: 점들을 지나는 곡선을 3차 베지어 점 목록으로.</summary>
    public static List<double[]> CurveToBezier(IReadOnlyList<double[]> pointsIn, double curveTightness = 0)
    {
        var len = pointsIn.Count;
        if (len < 3)
        {
            throw new ArgumentException("A curve must have at least three points.");
        }
        var output = new List<double[]>();
        if (len == 3)
        {
            output.Add([.. pointsIn[0]]);
            output.Add([.. pointsIn[1]]);
            output.Add([.. pointsIn[2]]);
            output.Add([.. pointsIn[2]]);
            return output;
        }
        var points = new List<double[]> { pointsIn[0], pointsIn[0] };
        for (var i = 1; i < pointsIn.Count; i++)
        {
            points.Add(pointsIn[i]);
            if (i == pointsIn.Count - 1)
            {
                points.Add(pointsIn[i]);
            }
        }
        var s = 1 - curveTightness;
        output.Add([.. points[0]]);
        for (var i = 1; i + 2 < points.Count; i++)
        {
            var c = points[i];
            output.Add([c[0] + (s * points[i + 1][0] - s * points[i - 1][0]) / 6, c[1] + (s * points[i + 1][1] - s * points[i - 1][1]) / 6]);
            output.Add([points[i + 1][0] + (s * points[i][0] - s * points[i + 2][0]) / 6, points[i + 1][1] + (s * points[i][1] - s * points[i + 2][1]) / 6]);
            output.Add([points[i + 1][0], points[i + 1][1]]);
        }
        return output;
    }

    /// <summary>points-on-path: 경로를 점 목록들로 (곡선은 잘게 나눔), distance가 있으면 단순화.</summary>
    public static List<List<double[]>> PointsOnPath(IReadOnlyList<PathSeg> path, double tolerance, double distance)
    {
        var normalized = PathData.Normalize(path);
        var sets = new List<List<double[]>>();
        var currentPoints = new List<double[]>();
        double[] start = [0, 0];
        var pendingCurve = new List<double[]>();

        void AppendPendingCurve()
        {
            if (pendingCurve.Count >= 4)
            {
                currentPoints.AddRange(PointsOnBezierCurves(pendingCurve, tolerance));
            }
            pendingCurve = [];
        }

        void AppendPendingPoints()
        {
            AppendPendingCurve();
            if (currentPoints.Count > 0)
            {
                sets.Add(currentPoints);
                currentPoints = [];
            }
        }

        foreach (var (key, data) in normalized)
        {
            switch (key)
            {
                case 'M':
                    AppendPendingPoints();
                    start = [data[0], data[1]];
                    currentPoints.Add(start);
                    break;
                case 'L':
                    AppendPendingCurve();
                    currentPoints.Add([data[0], data[1]]);
                    break;
                case 'C':
                    if (pendingCurve.Count == 0)
                    {
                        var last = currentPoints.Count > 0 ? currentPoints[^1] : start;
                        pendingCurve.Add([last[0], last[1]]);
                    }
                    pendingCurve.Add([data[0], data[1]]);
                    pendingCurve.Add([data[2], data[3]]);
                    pendingCurve.Add([data[4], data[5]]);
                    break;
                case 'Z':
                    AppendPendingCurve();
                    currentPoints.Add([start[0], start[1]]);
                    break;
            }
        }
        AppendPendingPoints();
        if (distance == 0)
        {
            return sets;
        }
        var result = new List<List<double[]>>();
        foreach (var set in sets)
        {
            var simplified = Simplify(set, distance);
            if (simplified.Count > 0)
            {
                result.Add(simplified);
            }
        }
        return result;
    }
}

/// <summary>rough.js의 채우기 (hachure-fill 포함). 점(dots) 채우기는 원본도 무작위라 빗금으로 대신한다.</summary>
public static class RoughFillers
{
    public static OpSet Fill(IReadOnlyList<List<double[]>> polygonList, RoughOptions o)
    {
        switch (o.FillStyle)
        {
            case "cross-hatch":
            {
                var set = Hachure(polygonList, o);
                var o2 = o.Clone();
                o2.HachureAngle = o.HachureAngle + 90;
                var set2 = Hachure(polygonList, o2);
                set.Ops.AddRange(set2.Ops);
                return set;
            }
            case "zigzag":
                return ZigZag(polygonList, o);
            case "dashed":
                return new OpSet(OpSetType.FillSketch, Dashed(PolygonHachureLines(polygonList, o), o));
            case "zigzag-line":
            {
                var gap = o.HachureGap < 0 ? o.StrokeWidth * 4 : o.HachureGap;
                var zo = o.ZigzagOffset < 0 ? gap : o.ZigzagOffset;
                var o2 = o.Clone();
                o2.HachureGap = gap + zo;
                return new OpSet(OpSetType.FillSketch, ZigZagLines(PolygonHachureLines(polygonList, o2), zo, o2));
            }
            default:
                return Hachure(polygonList, o);
        }
    }

    private static OpSet Hachure(IReadOnlyList<List<double[]>> polygonList, RoughOptions o) =>
        new(OpSetType.FillSketch, RenderLines(PolygonHachureLines(polygonList, o), o));

    private static List<RoughOp> RenderLines(List<double[][]> lines, RoughOptions o)
    {
        var ops = new List<RoughOp>();
        foreach (var line in lines)
        {
            ops.AddRange(RoughRenderer.DoubleLineFillOps(line[0][0], line[0][1], line[1][0], line[1][1], o));
        }
        return ops;
    }

    private static OpSet ZigZag(IReadOnlyList<List<double[]>> polygonList, RoughOptions o)
    {
        var gap = o.HachureGap;
        if (gap < 0)
        {
            gap = o.StrokeWidth * 4;
        }
        gap = Math.Max(gap, 0.1);
        var o2 = o.Clone();
        o2.HachureGap = gap;
        var lines = PolygonHachureLines(polygonList, o2);
        var zigZagAngle = Math.PI / 180 * o.HachureAngle;
        var zigzagLines = new List<double[][]>();
        var dgx = gap * 0.5 * Math.Cos(zigZagAngle);
        var dgy = gap * 0.5 * Math.Sin(zigZagAngle);
        foreach (var line in lines)
        {
            var (p1, p2) = (line[0], line[1]);
            if (Math.Sqrt(Math.Pow(p1[0] - p2[0], 2) + Math.Pow(p1[1] - p2[1], 2)) != 0)
            {
                zigzagLines.Add([[p1[0] - dgx, p1[1] + dgy], [.. p2]]);
                zigzagLines.Add([[p1[0] + dgx, p1[1] - dgy], [.. p2]]);
            }
        }
        return new OpSet(OpSetType.FillSketch, RenderLines(zigzagLines, o));
    }

    private static double LineLength(double[][] line) => Math.Sqrt(Math.Pow(line[0][0] - line[1][0], 2) + Math.Pow(line[0][1] - line[1][1], 2));

    private static List<RoughOp> Dashed(List<double[][]> lines, RoughOptions o)
    {
        var offset = o.DashOffset < 0 ? (o.HachureGap < 0 ? o.StrokeWidth * 4 : o.HachureGap) : o.DashOffset;
        var gap = o.DashGap < 0 ? (o.HachureGap < 0 ? o.StrokeWidth * 4 : o.HachureGap) : o.DashGap;
        var ops = new List<RoughOp>();
        foreach (var line in lines)
        {
            var length = LineLength(line);
            var count = Math.Floor(length / (offset + gap));
            var startOffset = (length + gap - count * (offset + gap)) / 2;
            var (p1, p2) = (line[0], line[1]);
            if (p1[0] > p2[0])
            {
                (p1, p2) = (line[1], line[0]);
            }
            var alpha = Math.Atan((p2[1] - p1[1]) / (p2[0] - p1[0]));
            for (var i = 0; i < count; i++)
            {
                var lstart = i * (offset + gap);
                var lend = lstart + offset;
                double[] start = [p1[0] + lstart * Math.Cos(alpha) + startOffset * Math.Cos(alpha), p1[1] + lstart * Math.Sin(alpha) + startOffset * Math.Sin(alpha)];
                double[] end = [p1[0] + lend * Math.Cos(alpha) + startOffset * Math.Cos(alpha), p1[1] + lend * Math.Sin(alpha) + startOffset * Math.Sin(alpha)];
                ops.AddRange(RoughRenderer.DoubleLineFillOps(start[0], start[1], end[0], end[1], o));
            }
        }
        return ops;
    }

    private static List<RoughOp> ZigZagLines(List<double[][]> lines, double zo, RoughOptions o)
    {
        var ops = new List<RoughOp>();
        foreach (var line in lines)
        {
            var length = LineLength(line);
            var count = Math.Floor(length / (2 * zo) + 0.5);
            var (p1, p2) = (line[0], line[1]);
            if (p1[0] > p2[0])
            {
                (p1, p2) = (line[1], line[0]);
            }
            var alpha = Math.Atan((p2[1] - p1[1]) / (p2[0] - p1[0]));
            for (var i = 0; i < count; i++)
            {
                var lstart = i * 2 * zo;
                var lend = (i + 1) * 2 * zo;
                var dz = Math.Sqrt(2 * Math.Pow(zo, 2));
                double[] start = [p1[0] + lstart * Math.Cos(alpha), p1[1] + lstart * Math.Sin(alpha)];
                double[] end = [p1[0] + lend * Math.Cos(alpha), p1[1] + lend * Math.Sin(alpha)];
                double[] middle = [start[0] + dz * Math.Cos(alpha + Math.PI / 4), start[1] + dz * Math.Sin(alpha + Math.PI / 4)];
                ops.AddRange(RoughRenderer.DoubleLineFillOps(start[0], start[1], middle[0], middle[1], o));
                ops.AddRange(RoughRenderer.DoubleLineFillOps(middle[0], middle[1], end[0], end[1], o));
            }
        }
        return ops;
    }

    /// <summary>scan-line-hachure.</summary>
    private static List<double[][]> PolygonHachureLines(IReadOnlyList<List<double[]>> polygonList, RoughOptions o)
    {
        var angle = o.HachureAngle + 90;
        var gap = o.HachureGap;
        if (gap < 0)
        {
            gap = o.StrokeWidth * 4;
        }
        gap = Math.Max(gap, 0.1);
        double skipOffset = 1;
        if (o.Roughness >= 1)
        {
            var r = o.Randomizer?.Next() ?? 0;
            if ((r != 0 ? r : System.Random.Shared.NextDouble()) > 0.7)
            {
                skipOffset = gap;
            }
        }
        return HachureLines(polygonList, gap, angle, skipOffset != 0 ? skipOffset : 1);
    }

    /// <summary>hachure-fill (MIT): 다각형을 기울인 평행선으로 채운다. 원본처럼 입력 점을 돌렸다가 되돌린다.</summary>
    private static List<double[][]> HachureLines(IReadOnlyList<List<double[]>> polygons, double hachureGap, double hachureAngle, double hachureStepOffset)
    {
        var gap = Math.Max(hachureGap, 0.1);
        double[] center = [0, 0];
        if (hachureAngle != 0)
        {
            foreach (var polygon in polygons)
            {
                RotatePoints(polygon, center, hachureAngle);
            }
        }
        var lines = StraightHachureLines(polygons, gap, hachureStepOffset);
        if (hachureAngle != 0)
        {
            foreach (var polygon in polygons)
            {
                RotatePoints(polygon, center, -hachureAngle);
            }
            RotatePoints(lines.SelectMany(l => l).ToList(), center, -hachureAngle);
        }
        return lines;
    }

    private static void RotatePoints(IReadOnlyList<double[]> points, double[] center, double degrees)
    {
        var (cx, cy) = (center[0], center[1]);
        var angle = Math.PI / 180 * degrees;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        foreach (var p in points)
        {
            var (x, y) = (p[0], p[1]);
            p[0] = (x - cx) * cos - (y - cy) * sin + cx;
            p[1] = (x - cx) * sin + (y - cy) * cos + cy;
        }
    }

    private sealed class Edge
    {
        public double YMin;
        public double YMax;
        public double X;
        public double ISlope;
    }

    private static List<double[][]> StraightHachureLines(IReadOnlyList<List<double[]>> polygons, double gap, double hachureStepOffset)
    {
        var vertexArray = new List<List<double[]>>();
        foreach (var polygon in polygons)
        {
            var vertices = new List<double[]>(polygon);
            if (!(vertices[0][0] == vertices[^1][0] && vertices[0][1] == vertices[^1][1]))
            {
                vertices.Add([vertices[0][0], vertices[0][1]]);
            }
            if (vertices.Count > 2)
            {
                vertexArray.Add(vertices);
            }
        }
        var lines = new List<double[][]>();
        gap = Math.Max(gap, 0.1);
        var edges = new List<Edge>();
        foreach (var vertices in vertexArray)
        {
            for (var i = 0; i < vertices.Count - 1; i++)
            {
                var p1 = vertices[i];
                var p2 = vertices[i + 1];
                if (p1[1] != p2[1])
                {
                    var ymin = Math.Min(p1[1], p2[1]);
                    edges.Add(new Edge
                    {
                        YMin = ymin,
                        YMax = Math.Max(p1[1], p2[1]),
                        X = ymin == p1[1] ? p1[0] : p2[0],
                        ISlope = (p2[0] - p1[0]) / (p2[1] - p1[1]),
                    });
                }
            }
        }
        // JS의 Array.sort는 안정 정렬이므로 OrderBy(안정)로 같은 순서를 낸다.
        edges = [.. edges.OrderBy(e => e, Comparer<Edge>.Create(CompareEdges))];
        if (edges.Count == 0)
        {
            return lines;
        }
        var activeEdges = new List<(double S, Edge Edge)>();
        var y = edges[0].YMin;
        var iteration = 0;
        while (activeEdges.Count > 0 || edges.Count > 0)
        {
            if (edges.Count > 0)
            {
                var ix = -1;
                for (var i = 0; i < edges.Count; i++)
                {
                    if (edges[i].YMin > y)
                    {
                        break;
                    }
                    ix = i;
                }
                var removed = edges.GetRange(0, ix + 1);
                edges.RemoveRange(0, ix + 1);
                foreach (var edge in removed)
                {
                    activeEdges.Add((y, edge));
                }
            }
            activeEdges = [.. activeEdges.Where(ae => !(ae.Edge.YMax <= y))];
            activeEdges = [.. activeEdges.OrderBy(ae => ae, Comparer<(double S, Edge Edge)>.Create((a, b) =>
            {
                if (a.Edge.X == b.Edge.X)
                {
                    return 0;
                }
                return Math.Sign((a.Edge.X - b.Edge.X) / Math.Abs(a.Edge.X - b.Edge.X));
            }))];
            if (hachureStepOffset != 1 || iteration % gap == 0)
            {
                if (activeEdges.Count > 1)
                {
                    for (var i = 0; i < activeEdges.Count; i += 2)
                    {
                        var next = i + 1;
                        if (next >= activeEdges.Count)
                        {
                            break;
                        }
                        var ce = activeEdges[i].Edge;
                        var ne = activeEdges[next].Edge;
                        lines.Add([[JsRound(ce.X), y], [JsRound(ne.X), y]]);
                    }
                }
            }
            y += hachureStepOffset;
            foreach (var ae in activeEdges)
            {
                ae.Edge.X += hachureStepOffset * ae.Edge.ISlope;
            }
            iteration++;
        }
        return lines;
    }

    private static int CompareEdges(Edge e1, Edge e2)
    {
        if (e1.YMin < e2.YMin)
        {
            return -1;
        }
        if (e1.YMin > e2.YMin)
        {
            return 1;
        }
        if (e1.X < e2.X)
        {
            return -1;
        }
        if (e1.X > e2.X)
        {
            return 1;
        }
        if (e1.YMax == e2.YMax)
        {
            return 0;
        }
        return Math.Sign((e1.YMax - e2.YMax) / Math.Abs(e1.YMax - e2.YMax));
    }

    /// <summary>JS Math.round (반올림은 +∞ 쪽).</summary>
    public static double JsRound(double x) => Math.Floor(x + 0.5);
}

/// <summary>절대 좌표 SVG 경로 문자열(M, L, C, Q, Z)을 조각으로 읽는다.</summary>
public static class SvgPathParser
{
    public static List<PathSeg> Parse(string d)
    {
        var result = new List<PathSeg>();
        var tokens = System.Text.RegularExpressions.Regex.Matches(d, @"[MLCQZmlcqz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
        char command = ' ';
        var numbers = new List<double>();

        void Flush()
        {
            var size = command switch { 'M' or 'L' => 2, 'C' => 6, 'Q' => 4, _ => 0 };
            if (command == 'Z')
            {
                result.Add(new PathSeg('Z', []));
                return;
            }
            for (var i = 0; size > 0 && i + size <= numbers.Count; i += size)
            {
                result.Add(new PathSeg(i > 0 && command == 'M' ? 'L' : command, [.. numbers.GetRange(i, size)]));
            }
        }

        foreach (System.Text.RegularExpressions.Match token in tokens)
        {
            if (char.IsLetter(token.Value[0]))
            {
                if (command != ' ')
                {
                    Flush();
                }
                command = char.ToUpperInvariant(token.Value[0]);
                numbers.Clear();
            }
            else
            {
                numbers.Add(double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (command != ' ')
        {
            Flush();
        }
        return result;
    }
}
