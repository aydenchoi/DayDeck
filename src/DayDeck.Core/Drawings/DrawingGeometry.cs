namespace DayDeck.Core.Drawings;

/// <summary>워크플로우 다음 단계 방향 (Ctrl+화살표 키).</summary>
public enum StepDirection
{
    Right,
    Down,
    Left,
    Up,
}

/// <summary>요소의 영역·누르기 판정·화살표 연결·손그림 선 계산 (WPF 없이 테스트할 수 있게 Core에 둔다).</summary>
public static class DrawingGeometry
{
    /// <summary>화살표 끝이 도형에 붙는 거리.</summary>
    public const double BindDistance = 12;

    /// <summary>다음 단계 도형 사이 간격.</summary>
    public const double FlowGap = 80;

    // ---------- 영역 ----------

    public static Box Bounds(ExcalidrawElement e)
    {
        if (e.IsLinear || e.Type == ExcalidrawElement.FreeDraw)
        {
            var points = e.Points;
            if (points.Count == 0)
            {
                return new Box(e.X, e.Y, 0, 0);
            }
            var minX = points.Min(p => p.X);
            var minY = points.Min(p => p.Y);
            return new Box(e.X + minX, e.Y + minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
        }
        return new Box(e.X, e.Y, e.Width, e.Height);
    }

    /// <summary>선·화살표·펜의 점 (그림 좌표).</summary>
    public static List<Pt> AbsolutePoints(ExcalidrawElement e) => [.. e.Points.Select(p => new Pt(e.X + p.X, e.Y + p.Y))];

    /// <summary>점들을 다시 요소에 넣는다 (첫 점 = X,Y, 너비·높이는 점 범위).</summary>
    public static void SetAbsolutePoints(ExcalidrawElement e, IReadOnlyList<Pt> points)
    {
        if (points.Count == 0)
        {
            return;
        }
        var origin = points[0];
        e.X = origin.X;
        e.Y = origin.Y;
        var relative = points.Select(p => p - origin).ToList();
        e.Points = relative;
        e.Width = relative.Max(p => p.X) - relative.Min(p => p.X);
        e.Height = relative.Max(p => p.Y) - relative.Min(p => p.Y);
    }

    // ---------- 누르기 판정 ----------

    /// <summary>그림 좌표 <paramref name="p"/>가 요소 위인지. 닫힌 도형은 안쪽도 포함한다.</summary>
    public static bool HitTest(ExcalidrawElement e, Pt p, double tolerance = 6)
    {
        var bounds = Bounds(e);
        if (e.Angle != 0 && !e.IsLinear && e.Type != ExcalidrawElement.FreeDraw)
        {
            p = Rotate(p, bounds.Center, -e.Angle);
        }
        tolerance = Math.Max(tolerance, e.StrokeWidth / 2 + 2);
        if (!bounds.Inflate(tolerance).Contains(p))
        {
            return false;
        }
        switch (e.Type)
        {
            case ExcalidrawElement.Rectangle:
            case ExcalidrawElement.Text:
                return true;
            case ExcalidrawElement.Ellipse:
            {
                var c = bounds.Center;
                var rx = bounds.Width / 2 + tolerance;
                var ry = bounds.Height / 2 + tolerance;
                if (rx <= 0 || ry <= 0)
                {
                    return false;
                }
                var dx = (p.X - c.X) / rx;
                var dy = (p.Y - c.Y) / ry;
                return dx * dx + dy * dy <= 1;
            }
            case ExcalidrawElement.Diamond:
            {
                var c = bounds.Center;
                var hw = bounds.Width / 2 + tolerance;
                var hh = bounds.Height / 2 + tolerance;
                return hw > 0 && hh > 0 && Math.Abs(p.X - c.X) / hw + Math.Abs(p.Y - c.Y) / hh <= 1;
            }
            default:
            {
                var points = e.IsLinear && e.Roundness is not null && e.Points.Count > 2 ? CurvePolyline(AbsolutePoints(e)) : AbsolutePoints(e);
                if (points.Count == 1)
                {
                    return (points[0] - p).Length <= tolerance;
                }
                for (var i = 1; i < points.Count; i++)
                {
                    if (DistanceToSegment(p, points[i - 1], points[i]) <= tolerance)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    /// <summary>위에 있는 것부터 찾는다. 상자 안 글자는 상자로 본다.</summary>
    public static ExcalidrawElement? HitTopmost(ExcalidrawDocument doc, Pt p, double tolerance = 6)
    {
        for (var i = doc.Elements.Count - 1; i >= 0; i--)
        {
            var e = doc.Elements[i];
            if (!e.IsDeleted && HitTest(e, p, tolerance))
            {
                return e.ContainerId is { } container ? doc.Find(container) ?? e : e;
            }
        }
        return null;
    }

    public static double DistanceToSegment(Pt p, Pt a, Pt b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared == 0)
        {
            return (p - a).Length;
        }
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public static Pt Rotate(Pt p, Pt center, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var d = p - center;
        return new Pt(center.X + d.X * cos - d.Y * sin, center.Y + d.X * sin + d.Y * cos);
    }

    // ---------- 화살표 연결 ----------

    /// <summary>
    /// 도형 가장자리에서 <paramref name="toward"/> 쪽으로 나가는 점 (가운데에서 그 점으로 그은 선이 테두리와 만나는 곳 + 간격).
    /// </summary>
    public static Pt OutlinePoint(ExcalidrawElement shape, Pt toward, double gap)
    {
        var b = Bounds(shape);
        var c = b.Center;
        var d = toward - c;
        if (d.Length < 0.001)
        {
            d = new Pt(1, 0);
        }
        var hw = Math.Max(b.Width / 2, 0.5);
        var hh = Math.Max(b.Height / 2, 0.5);
        double t; // c + d * t 가 테두리
        switch (shape.Type)
        {
            case ExcalidrawElement.Ellipse:
                t = 1 / Math.Sqrt(d.X * d.X / (hw * hw) + d.Y * d.Y / (hh * hh));
                break;
            case ExcalidrawElement.Diamond:
                t = 1 / (Math.Abs(d.X) / hw + Math.Abs(d.Y) / hh);
                break;
            default:
                t = Math.Min(d.X == 0 ? double.MaxValue : hw / Math.Abs(d.X), d.Y == 0 ? double.MaxValue : hh / Math.Abs(d.Y));
                break;
        }
        var edge = c + d * t;
        var unit = d * (1 / d.Length);
        return edge + unit * gap;
    }

    /// <summary>화살표를 붙일 도형 (점이 도형 위이거나 <see cref="BindDistance"/> 안).</summary>
    public static ExcalidrawElement? FindBindTarget(ExcalidrawDocument doc, Pt p, string? exceptId = null)
    {
        for (var i = doc.Elements.Count - 1; i >= 0; i--)
        {
            var e = doc.Elements[i];
            if (!e.IsDeleted && e.IsBindable && e.Id != exceptId && HitTest(e, p, BindDistance))
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>화살표 한쪽 끝을 도형에 붙인다 (도형의 boundElements에도 적는다). null이면 뗀다.</summary>
    public static void Bind(ExcalidrawDocument doc, ExcalidrawElement arrow, bool start, ExcalidrawElement? target, Pt? fixedPoint = null)
    {
        var old = start ? arrow.StartBinding : arrow.EndBinding;
        if (old is not null && old.ElementId != target?.Id)
        {
            var other = start ? arrow.EndBinding : arrow.StartBinding;
            if (other?.ElementId != old.ElementId)
            {
                doc.Find(old.ElementId)?.RemoveBound(arrow.Id);
            }
        }
        var binding = target is null ? null : new ArrowBinding(target.Id, 0, fixedPoint is null ? 6 : ConnectionGap, fixedPoint);
        if (start)
        {
            arrow.StartBinding = binding;
        }
        else
        {
            arrow.EndBinding = binding;
        }
        target?.AddBound(arrow.Id, ExcalidrawElement.Arrow);
    }

    /// <summary>붙은 도형에 맞춰 화살표 양 끝을 다시 놓는다 (가운데 점은 그대로).</summary>
    public static void UpdateArrowEnds(ExcalidrawDocument doc, ExcalidrawElement arrow)
    {
        var points = AbsolutePoints(arrow);
        if (points.Count < 2)
        {
            return;
        }
        var startShape = doc.Find(arrow.StartBinding?.ElementId);
        var endShape = doc.Find(arrow.EndBinding?.ElementId);
        if (startShape is null && endShape is null)
        {
            return;
        }
        // 한쪽 끝은 반대쪽(도형이면 그 가운데, 아니면 바로 옆 점)을 향한다.
        var startToward = points.Count > 2 ? points[1] : endShape is not null ? Bounds(endShape).Center : points[^1];
        var endToward = points.Count > 2 ? points[^2] : startShape is not null ? Bounds(startShape).Center : points[0];
        // 연결점에 붙은 끝은 그 점에 고정, 아니면 반대쪽을 향해 테두리에 붙는다.
        if (arrow.StartBinding?.FixedPoint is { } startFixed && startShape is not null)
        {
            points[0] = FixedPointPosition(startShape, startFixed, arrow.StartBinding.Gap);
        }
        if (arrow.EndBinding?.FixedPoint is { } endFixed && endShape is not null)
        {
            points[^1] = FixedPointPosition(endShape, endFixed, arrow.EndBinding.Gap);
        }
        if (points.Count == 2)
        {
            startToward = arrow.EndBinding?.FixedPoint is not null ? points[^1] : startToward;
            endToward = arrow.StartBinding?.FixedPoint is not null ? points[0] : endToward;
        }
        if (startShape is not null && arrow.StartBinding?.FixedPoint is null)
        {
            points[0] = OutlinePoint(startShape, startToward, arrow.StartBinding!.Gap);
        }
        if (endShape is not null && arrow.EndBinding?.FixedPoint is null)
        {
            points[^1] = OutlinePoint(endShape, endToward, arrow.EndBinding!.Gap);
        }
        SetAbsolutePoints(arrow, points);
    }

    /// <summary>움직인 요소들에 붙은 화살표를 따라 옮긴다. 바뀐 화살표를 돌려준다.</summary>
    public static List<ExcalidrawElement> UpdateBoundArrows(ExcalidrawDocument doc, IEnumerable<ExcalidrawElement> moved)
    {
        var movedIds = moved.Select(m => m.Id).ToHashSet();
        var updated = new List<ExcalidrawElement>();
        foreach (var arrow in doc.Visible.Where(e => e.Type == ExcalidrawElement.Arrow || e.Type == ExcalidrawElement.Line).ToList())
        {
            if (movedIds.Contains(arrow.Id))
            {
                continue; // 화살표째 옮긴 경우는 끝도 같이 움직였다.
            }
            if ((arrow.StartBinding is { } s && movedIds.Contains(s.ElementId)) || (arrow.EndBinding is { } e && movedIds.Contains(e.ElementId)))
            {
                UpdateArrowEnds(doc, arrow);
                updated.Add(arrow);
            }
        }
        return updated;
    }

    // ---------- 연결점 ----------

    /// <summary>연결점에 붙은 화살표 끝과 도형 사이 간격.</summary>
    public const double ConnectionGap = 4;

    /// <summary>
    /// 도형의 연결점: 위·오른쪽·아래·왼쪽 가운데 (마름모는 꼭짓점). 비율(Excalidraw fixedPoint, 도형 상자 기준 0~1)과 그림 좌표.
    /// </summary>
    public static List<(Pt Ratio, Pt World)> ConnectionPoints(ExcalidrawElement shape)
    {
        if (!shape.IsShape || shape.Width <= 0 || shape.Height <= 0)
        {
            return [];
        }
        List<Pt> ratios;
        if (shape.Type == ExcalidrawElement.Diamond)
        {
            var d = ExcalidrawShapes.DiamondPoints(shape);
            ratios = [new(d[0] / shape.Width, 0), new(1, d[3] / shape.Height), new(d[4] / shape.Width, 1), new(0, d[7] / shape.Height)];
        }
        else
        {
            ratios = [new(0.5, 0), new(1, 0.5), new(0.5, 1), new(0, 0.5)];
        }
        return [.. ratios.Select(r => (r, RatioToWorld(shape, r)))];
    }

    /// <summary>도형 상자 안 비율 위치 → 그림 좌표 (회전 반영).</summary>
    public static Pt RatioToWorld(ExcalidrawElement shape, Pt ratio)
    {
        var b = Bounds(shape);
        var p = new Pt(shape.X + ratio.X * shape.Width, shape.Y + ratio.Y * shape.Height);
        return shape.Angle == 0 ? p : Rotate(p, b.Center, shape.Angle);
    }

    /// <summary>연결점에 붙은 화살표 끝: 연결점에서 도형 바깥쪽으로 간격만큼.</summary>
    public static Pt FixedPointPosition(ExcalidrawElement shape, Pt ratio, double gap)
    {
        var point = RatioToWorld(shape, ratio);
        var center = Bounds(shape).Center;
        var d = point - center;
        // 가운데 점은 변의 바깥 방향으로 (가로·세로 비율이 달라도 변에 수직이 되게).
        var outward = new Pt(ratio.X <= 0.001 ? -1 : ratio.X >= 0.999 ? 1 : 0, ratio.Y <= 0.001 ? -1 : ratio.Y >= 0.999 ? 1 : 0);
        if (outward.X == 0 && outward.Y == 0)
        {
            outward = d.Length < 0.001 ? new Pt(1, 0) : d * (1 / d.Length);
        }
        else
        {
            outward = shape.Angle == 0 ? outward : Rotate(outward, new Pt(0, 0), shape.Angle);
            outward = outward * (1 / outward.Length);
        }
        return point + outward * gap;
    }

    /// <summary><paramref name="p"/>에서 <paramref name="radius"/> 안의 가장 가까운 연결점 (위에 있는 도형부터).</summary>
    public static (ExcalidrawElement Shape, Pt Ratio, Pt World)? FindConnectionPoint(ExcalidrawDocument doc, Pt p, double radius, string? exceptId = null)
    {
        (ExcalidrawElement, Pt, Pt)? best = null;
        var bestDistance = radius;
        for (var i = doc.Elements.Count - 1; i >= 0; i--)
        {
            var e = doc.Elements[i];
            if (e.IsDeleted || !e.IsShape || e.Id == exceptId || !Bounds(e).Inflate(radius).Contains(p))
            {
                continue;
            }
            foreach (var (ratio, world) in ConnectionPoints(e))
            {
                var distance = (world - p).Length;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = (e, ratio, world);
                }
            }
        }
        return best;
    }

    // ---------- 곡선 ----------

    /// <summary>점들을 지나는 곡선 (Excalidraw의 둥근 선과 같은 Catmull-Rom → 3차 베지어). 구간마다 (시작, 조절1, 조절2, 끝).</summary>
    public static List<(Pt P0, Pt C1, Pt C2, Pt P3)> CurveSegments(IReadOnlyList<Pt> points)
    {
        if (points.Count < 3)
        {
            return points.Count == 2 ? [(points[0], points[0], points[1], points[1])] : [];
        }
        // rough.js curve()와 같이 양 끝 점을 한 번 더 넣고 Catmull-Rom(긴장도 0)으로 이어 모든 점을 지난다.
        var ps = new List<Pt> { points[0] };
        ps.AddRange(points);
        ps.Add(points[^1]);
        var segments = new List<(Pt, Pt, Pt, Pt)>();
        for (var i = 1; i + 2 < ps.Count; i++)
        {
            var c1 = ps[i] + (ps[i + 1] - ps[i - 1]) * (1.0 / 6);
            var c2 = ps[i + 1] + (ps[i] - ps[i + 2]) * (1.0 / 6);
            segments.Add((ps[i], c1, c2, ps[i + 1]));
        }
        return segments;
    }

    /// <summary>곡선의 <paramref name="segment"/>번째 구간에서 t 위치의 점.</summary>
    public static Pt CurvePoint(IReadOnlyList<Pt> points, int segment, double t)
    {
        var segments = CurveSegments(points);
        if (segments.Count == 0)
        {
            return points.Count > 0 ? points[0] : new Pt(0, 0);
        }
        var (p0, c1, c2, p3) = segments[Math.Clamp(segment, 0, segments.Count - 1)];
        var u = 1 - t;
        return p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
    }

    /// <summary>곡선을 잘게 나눈 점들 (누르기 판정용).</summary>
    public static List<Pt> CurvePolyline(IReadOnlyList<Pt> points, int steps = 12)
    {
        var result = new List<Pt>();
        var segments = CurveSegments(points);
        for (var s = 0; s < segments.Count; s++)
        {
            for (var k = s == 0 ? 0 : 1; k <= steps; k++)
            {
                result.Add(CurvePoint(points, s, k / (double)steps));
            }
        }
        return result.Count > 0 ? result : [.. points];
    }

    /// <summary>선의 i번째 구간 가운데 (곡선이면 곡선 위). 굽히기 손잡이 자리.</summary>
    public static Pt SegmentMidpoint(ExcalidrawElement line, int index)
    {
        var points = AbsolutePoints(line);
        return line.Roundness is not null && points.Count > 2 ? CurvePoint(points, index, 0.5) : (points[index] + points[index + 1]) * 0.5;
    }

    /// <summary>상자 안 글자 (boundElements의 text, 또는 containerId로 찾음).</summary>
    public static ExcalidrawElement? BoundText(ExcalidrawDocument doc, ExcalidrawElement container) =>
        container.BoundElements.Where(b => b.Type == ExcalidrawElement.Text).Select(b => doc.Find(b.Id)).FirstOrDefault(t => t is not null)
        ?? doc.Visible.FirstOrDefault(t => t.Type == ExcalidrawElement.Text && t.ContainerId == container.Id);

    /// <summary>
    /// 워크플로우 다음 단계: 같은 모양·크기·색의 도형을 <paramref name="direction"/> 쪽에 만들고 화살표로 잇는다.
    /// 그 자리에 이미 도형이 있으면 옆으로 비켜 놓는다. (새 도형, 화살표)를 돌려준다.
    /// </summary>
    public static (ExcalidrawElement Shape, ExcalidrawElement Arrow) AddNextStep(ExcalidrawDocument doc, ExcalidrawElement from, StepDirection direction, Random random)
    {
        var b = Bounds(from);
        var (dx, dy) = direction switch
        {
            StepDirection.Right => (b.Width + FlowGap, 0.0),
            StepDirection.Left => (-(b.Width + FlowGap), 0.0),
            StepDirection.Down => (0.0, b.Height + FlowGap),
            _ => (0.0, -(b.Height + FlowGap)),
        };
        // 비키는 방향은 진행 방향과 직각.
        var (sx, sy) = direction is StepDirection.Right or StepDirection.Left ? (0.0, b.Height + FlowGap / 2) : (b.Width + FlowGap / 2, 0.0);
        var target = new Box(b.X + dx, b.Y + dy, b.Width, b.Height);
        for (var i = 1; i < 20 && doc.Visible.Any(e => e.IsShape && Bounds(e).Intersects(target)); i++)
        {
            // 아래(오른쪽) → 위(왼쪽) 번갈아.
            var k = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
            target = new Box(b.X + dx + sx * k, b.Y + dy + sy * k, b.Width, b.Height);
        }

        var shape = ExcalidrawElement.Create(from.IsShape ? from.Type : ExcalidrawElement.Rectangle, target.X, target.Y, target.Width, target.Height, random);
        shape.StrokeColor = from.StrokeColor;
        shape.BackgroundColor = from.BackgroundColor;
        shape.FillStyle = from.FillStyle;
        shape.StrokeWidth = from.StrokeWidth;
        shape.StrokeStyle = from.StrokeStyle;
        shape.Roughness = from.Roughness;
        shape.Opacity = from.Opacity;
        doc.Elements.Add(shape);

        var arrow = ExcalidrawElement.Create(ExcalidrawElement.Arrow, 0, 0, 0, 0, random);
        arrow.StrokeColor = from.StrokeColor;
        arrow.StrokeWidth = from.StrokeWidth;
        arrow.Roughness = from.Roughness;
        SetAbsolutePoints(arrow, [b.Center, target.Center]);
        doc.Elements.Add(arrow);
        Bind(doc, arrow, start: true, from);
        Bind(doc, arrow, start: false, shape);
        UpdateArrowEnds(doc, arrow);
        return (shape, arrow);
    }

    // ---------- 손그림 ----------

    /// <summary>
    /// 손으로 그린 듯한 선. 같은 seed면 늘 같은 모양 (다시 그려도 떨리지 않음).
    /// 거칠기 0이면 곧은 선 하나, 아니면 조금씩 어긋난 선 두 번.
    /// </summary>
    public static List<List<Pt>> RoughPolyline(IReadOnlyList<Pt> points, bool closed, double roughness, int seed)
    {
        if (points.Count < 2 || roughness <= 0)
        {
            var single = points.ToList();
            if (closed && single.Count > 0)
            {
                single.Add(single[0]);
            }
            return [single];
        }
        var rng = new SeededRandom(seed);
        var strokes = new List<List<Pt>>();
        for (var pass = 0; pass < 2; pass++)
        {
            var stroke = new List<Pt>();
            var count = closed ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var length = (b - a).Length;
                var offset = Math.Min(length * 0.05, 2.5) * roughness * (pass + 1) * 0.8;
                Pt Jitter(Pt p) => new(p.X + rng.Range(-offset, offset), p.Y + rng.Range(-offset, offset));
                var start = Jitter(a);
                var end = Jitter(b);
                if (stroke.Count == 0)
                {
                    stroke.Add(start);
                }
                // 가운데 두 점을 살짝 휘게.
                var bow = Math.Min(length * 0.02, 1.5) * roughness;
                var normal = length > 0 ? new Pt(-(b.Y - a.Y) / length, (b.X - a.X) / length) : new Pt(0, 0);
                var m1 = a + (b - a) * rng.Range(0.3, 0.45) + normal * rng.Range(-bow, bow);
                var m2 = a + (b - a) * rng.Range(0.55, 0.75) + normal * rng.Range(-bow, bow);
                stroke.Add(Jitter(m1));
                stroke.Add(Jitter(m2));
                stroke.Add(end);
            }
            strokes.Add(stroke);
        }
        return strokes;
    }

    /// <summary>손그림 타원 (조금 겹쳐 끝나는 두 바퀴).</summary>
    public static List<List<Pt>> RoughEllipse(Box b, double roughness, int seed)
    {
        var c = b.Center;
        var rx = b.Width / 2;
        var ry = b.Height / 2;
        const int Steps = 36;
        if (roughness <= 0)
        {
            return [[.. Enumerable.Range(0, Steps + 1).Select(i => EllipsePoint(c, rx, ry, i * 2 * Math.PI / Steps))]];
        }
        var rng = new SeededRandom(seed);
        var strokes = new List<List<Pt>>();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = rng.Range(0, 2 * Math.PI);
            var overlap = rng.Range(0.1, 0.35);
            var wobble = Math.Min(Math.Max(rx, ry) * 0.03, 3) * roughness;
            var stroke = new List<Pt>();
            for (var i = 0; i <= Steps; i++)
            {
                var angle = start + (2 * Math.PI + overlap) * i / Steps;
                var r = rng.Range(-wobble, wobble);
                stroke.Add(EllipsePoint(c, rx + r, ry + r, angle));
            }
            strokes.Add(stroke);
        }
        return strokes;
    }

    /// <summary>도형의 꼭짓점 (사각형·마름모).</summary>
    public static List<Pt> ShapeCorners(ExcalidrawElement e)
    {
        var b = Bounds(e);
        return e.Type == ExcalidrawElement.Diamond
            ? [new Pt(b.Center.X, b.Y), new Pt(b.Right, b.Center.Y), new Pt(b.Center.X, b.Bottom), new Pt(b.X, b.Center.Y)]
            : [new Pt(b.X, b.Y), new Pt(b.Right, b.Y), new Pt(b.Right, b.Bottom), new Pt(b.X, b.Bottom)];
    }

    private static Pt EllipsePoint(Pt c, double rx, double ry, double angle) => new(c.X + rx * Math.Cos(angle), c.Y + ry * Math.Sin(angle));

    /// <summary>플랫폼과 무관하게 같은 값을 내는 작은 난수기 (mulberry32).</summary>
    private sealed class SeededRandom(int seed)
    {
        private uint _state = unchecked((uint)seed);

        public double Next()
        {
            unchecked
            {
                _state += 0x6D2B79F5;
                var t = _state;
                t = (t ^ (t >> 15)) * (t | 1);
                t ^= t + (t ^ (t >> 7)) * (t | 61);
                return ((t ^ (t >> 14)) >> 0) / 4294967296.0;
            }
        }

        public double Range(double min, double max) => min + (max - min) * Next();
    }
}
