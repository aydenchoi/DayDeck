namespace DayDeck.Core.Drawings.Rough;

/// <summary>
/// perfect-freehand 1.2.0 (MIT, Steve Ruiz)을 옮긴 것. Excalidraw 펜(freedraw) 획의 굵기가 변하는 외곽선을 만든다.
/// </summary>
public static class PerfectFreehand
{
    public sealed class Options
    {
        public double Size { get; init; } = 16;
        public double Smoothing { get; init; } = 0.5;
        public double Thinning { get; init; } = 0.5;
        public bool SimulatePressure { get; init; } = true;
        public Func<double, double> Easing { get; init; } = t => t;
        public double Streamline { get; init; } = 0.5;
        public bool Last { get; init; }
    }

    private sealed class StrokePoint
    {
        public required double[] Point;
        public double Pressure;
        public required double[] Vector;
        public double Distance;
        public double RunningLength;
    }

    private const double RateOfPressureChange = 0.275;
    private const double FixedPi = Math.PI + 1e-4;

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1]];
    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1]];
    private static double[] Mul(double[] a, double n) => [a[0] * n, a[1] * n];
    private static double[] Div(double[] a, double n) => [a[0] / n, a[1] / n];
    private static double[] Per(double[] a) => [a[1], -a[0]];
    private static double[] Neg(double[] a) => [-a[0], -a[1]];
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1];
    private static bool IsEqual(double[] a, double[] b) => a[0] == b[0] && a[1] == b[1];
    private static double Len(double[] a) => Hypot(a[0], a[1]);
    private static double Len2(double[] a) => a[0] * a[0] + a[1] * a[1];
    private static double Dist2(double[] a, double[] b) => Len2(Sub(a, b));
    private static double[] Uni(double[] a) => Div(a, Len(a));
    private static double Dist(double[] a, double[] b) => Hypot(a[1] - b[1], a[0] - b[0]);
    private static double[] Lrp(double[] a, double[] b, double t) => Add(a, Mul(Sub(b, a), t));
    private static double[] Prj(double[] a, double[] b, double c) => Add(a, Mul(b, c));

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double[] RotAround(double[] a, double[] c, double r)
    {
        var s = Math.Sin(r);
        var co = Math.Cos(r);
        var px = a[0] - c[0];
        var py = a[1] - c[1];
        var nx = px * co - py * s;
        var ny = px * s + py * co;
        return [nx + c[0], ny + c[1]];
    }

    private static double StrokeRadius(double size, double thinning, double pressure, Func<double, double> easing) =>
        size * easing(0.5 - thinning * (0.5 - pressure));

    /// <summary>입력 점(x, y, 압력 — 압력이 없으면 음수)을 외곽선 점으로.</summary>
    public static List<double[]> GetStroke(IReadOnlyList<double[]> input, Options options) =>
        GetStrokeOutlinePoints(GetStrokePoints(input, options), options);

    private static List<StrokePoint> GetStrokePoints(IReadOnlyList<double[]> input, Options options)
    {
        if (input.Count == 0)
        {
            return [];
        }
        var t = 0.15 + (1 - options.Streamline) * 0.85;
        var pts = input.Select(p => (double[])[.. p]).ToList();
        if (pts.Count == 2)
        {
            var last = pts[1];
            pts = pts.GetRange(0, 1);
            for (var i = 1; i < 5; i++)
            {
                pts.Add(Lrp(pts[0], last, i / 4.0));
            }
        }
        if (pts.Count == 1)
        {
            pts = [.. pts, [.. Add(pts[0], [1, 1]), .. pts[0].Skip(2)]];
        }
        double PressureOf(double[] p, double fallback) => p.Length > 2 && p[2] >= 0 ? p[2] : fallback;

        var strokePoints = new List<StrokePoint>
        {
            new() { Point = [pts[0][0], pts[0][1]], Pressure = PressureOf(pts[0], 0.25), Vector = [1, 1], Distance = 0, RunningLength = 0 },
        };
        var hasReachedMinimumLength = false;
        double runningLength = 0;
        var prev = strokePoints[0];
        var max = pts.Count - 1;
        for (var i = 1; i < pts.Count; i++)
        {
            var point = options.Last && i == max ? [pts[i][0], pts[i][1]] : Lrp(prev.Point, pts[i], t);
            if (IsEqual(prev.Point, point))
            {
                continue;
            }
            var distance = Dist(point, prev.Point);
            runningLength += distance;
            if (i < max && !hasReachedMinimumLength)
            {
                if (runningLength < options.Size)
                {
                    continue;
                }
                hasReachedMinimumLength = true;
            }
            prev = new StrokePoint
            {
                Point = point,
                Pressure = PressureOf(pts[i], 0.5),
                Vector = Uni(Sub(prev.Point, point)),
                Distance = distance,
                RunningLength = runningLength,
            };
            strokePoints.Add(prev);
        }
        strokePoints[0].Vector = strokePoints.Count > 1 ? strokePoints[1].Vector : [0, 0];
        return strokePoints;
    }

    private static List<double[]> GetStrokeOutlinePoints(List<StrokePoint> points, Options options)
    {
        var size = options.Size;
        var smoothing = options.Smoothing;
        var thinning = options.Thinning;
        var simulatePressure = options.SimulatePressure;
        var easing = options.Easing;
        Func<double, double> taperStartEase = r => r * (2 - r);
        Func<double, double> taperEndEase = r => --r * r * r + 1;
        if (points.Count == 0 || size <= 0)
        {
            return [];
        }
        var totalLength = points[^1].RunningLength;
        double taperStart = 0;
        double taperEnd = 0;
        var minDistance = Math.Pow(size * smoothing, 2);
        var leftPts = new List<double[]>();
        var rightPts = new List<double[]>();

        var prevPressure = points[0].Pressure;
        foreach (var curr in points.Take(10))
        {
            var pressure = curr.Pressure;
            if (simulatePressure)
            {
                var sp = Math.Min(1, curr.Distance / size);
                var rp = Math.Min(1, 1 - sp);
                pressure = Math.Min(1, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
            }
            prevPressure = (prevPressure + pressure) / 2;
        }

        var radius = StrokeRadius(size, thinning, points[^1].Pressure, easing);
        double? firstRadius = null;
        var prevVector = points[0].Vector;
        var pl = points[0].Point;
        var pr = pl;
        var tl = pl;
        var tr = pr;
        var isPrevPointSharpCorner = false;

        for (var i = 0; i < points.Count; i++)
        {
            var pressure = points[i].Pressure;
            var point = points[i].Point;
            var vector = points[i].Vector;
            var distance = points[i].Distance;
            var running = points[i].RunningLength;
            if (i < points.Count - 1 && totalLength - running < 3)
            {
                continue;
            }
            if (thinning != 0)
            {
                if (simulatePressure)
                {
                    var sp = Math.Min(1, distance / size);
                    var rp = Math.Min(1, 1 - sp);
                    pressure = Math.Min(1, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
                }
                radius = StrokeRadius(size, thinning, pressure, easing);
            }
            else
            {
                radius = size / 2;
            }
            firstRadius ??= radius;
            var ts = running < taperStart ? taperStartEase(running / taperStart) : 1;
            var te = totalLength - running < taperEnd ? taperEndEase((totalLength - running) / taperEnd) : 1;
            radius = Math.Max(0.01, radius * Math.Min(ts, te));

            var nextVector = (i < points.Count - 1 ? points[i + 1] : points[i]).Vector;
            double? nextDpr = i < points.Count - 1 ? Dot(vector, nextVector) : 1;
            var prevDpr = Dot(vector, prevVector);
            var isPointSharpCorner = prevDpr < 0 && !isPrevPointSharpCorner;
            var isNextPointSharpCorner = nextDpr is { } nd && nd < 0;

            if (isPointSharpCorner || isNextPointSharpCorner)
            {
                var offset = Mul(Per(prevVector), radius);
                const double step = 1.0 / 13;
                for (double t = 0; t <= 1; t += step)
                {
                    tl = RotAround(Sub(point, offset), point, FixedPi * t);
                    leftPts.Add(tl);
                    tr = RotAround(Add(point, offset), point, FixedPi * -t);
                    rightPts.Add(tr);
                }
                pl = tl;
                pr = tr;
                if (isNextPointSharpCorner)
                {
                    isPrevPointSharpCorner = true;
                }
                continue;
            }
            isPrevPointSharpCorner = false;
            if (i == points.Count - 1)
            {
                var offset = Mul(Per(vector), radius);
                leftPts.Add(Sub(point, offset));
                rightPts.Add(Add(point, offset));
                continue;
            }
            var off = Mul(Per(Lrp(nextVector, vector, nextDpr!.Value)), radius);
            tl = Sub(point, off);
            if (i <= 1 || Dist2(pl, tl) > minDistance)
            {
                leftPts.Add(tl);
                pl = tl;
            }
            tr = Add(point, off);
            if (i <= 1 || Dist2(pr, tr) > minDistance)
            {
                rightPts.Add(tr);
                pr = tr;
            }
            prevPressure = pressure;
            prevVector = vector;
        }

        double[] firstPoint = [points[0].Point[0], points[0].Point[1]];
        var lastPoint = points.Count > 1 ? [points[^1].Point[0], points[^1].Point[1]] : Add(points[0].Point, [1, 1]);
        var startCap = new List<double[]>();
        var endCap = new List<double[]>();
        if (points.Count == 1)
        {
            if (!(taperStart != 0 || taperEnd != 0) || options.Last)
            {
                var start = Prj(firstPoint, Uni(Per(Sub(firstPoint, lastPoint))), -(firstRadius ?? radius));
                var dotPts = new List<double[]>();
                const double step = 1.0 / 13;
                for (var t = step; t <= 1; t += step)
                {
                    dotPts.Add(RotAround(start, firstPoint, FixedPi * 2 * t));
                }
                return dotPts;
            }
        }
        else
        {
            // 시작 둥근 끝 (cap = true, taper 없음)
            const double step = 1.0 / 13;
            for (var t = step; t <= 1; t += step)
            {
                startCap.Add(RotAround(rightPts[0], firstPoint, FixedPi * t));
            }
            var direction = Per(Neg(points[^1].Vector));
            var startEnd = Prj(lastPoint, direction, radius);
            const double endStep = 1.0 / 29;
            for (var t = endStep; t < 1; t += endStep)
            {
                endCap.Add(RotAround(startEnd, lastPoint, FixedPi * 3 * t));
            }
        }
        rightPts.Reverse();
        return [.. leftPts, .. endCap, .. rightPts, .. startCap];
    }

    /// <summary>Excalidraw getFreeDrawSvgPath: 외곽선 점을 이차 곡선 경로로 (소수 둘째 자리에서 자름).</summary>
    public static List<PathSeg> FreedrawPath(IReadOnlyList<double[]> inputPoints, double strokeWidth, bool simulatePressure, bool last)
    {
        var stroke = GetStroke(inputPoints, new Options
        {
            Size = strokeWidth * 4.25,
            Thinning = 0.6,
            Smoothing = 0.5,
            Streamline = 0.5,
            Easing = t => Math.Sin(t * Math.PI / 2),
            Last = last,
            SimulatePressure = simulatePressure,
        });
        var path = new List<PathSeg>();
        if (stroke.Count == 0)
        {
            return path;
        }
        static double T(double v) => Math.Truncate(v * 100) / 100;
        double[] P(double[] p) => [T(p[0]), T(p[1])];
        var first = P(stroke[0]);
        path.Add(new PathSeg('M', first));
        // "Q p0 m01 p1 m12 ..." = 이어진 이차 곡선들 (조절점 p_i, 끝점 m_i,i+1)
        for (var i = 0; i < stroke.Count; i++)
        {
            var next = i == stroke.Count - 1 ? stroke[0] : stroke[i + 1];
            double[] mid = [(stroke[i][0] + next[0]) / 2, (stroke[i][1] + next[1]) / 2];
            var c = P(stroke[i]);
            var m = P(mid);
            path.Add(new PathSeg('Q', [c[0], c[1], m[0], m[1]]));
        }
        path.Add(new PathSeg('L', first));
        path.Add(new PathSeg('Z', []));
        return path;
    }
}
