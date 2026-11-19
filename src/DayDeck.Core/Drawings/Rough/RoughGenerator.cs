namespace DayDeck.Core.Drawings.Rough;

/// <summary>rough.js generator.js. 옵션은 부를 때마다 기본값 위에 복사해 쓰므로 seed가 같으면 결과도 같다.</summary>
public static class RoughGenerator
{
    private static RoughOptions O(RoughOptions options) => options.Clone();

    public static Drawable Line(double x1, double y1, double x2, double y2, RoughOptions options)
    {
        var o = O(options);
        return new Drawable("line", [RoughRenderer.Line(x1, y1, x2, y2, o)], o);
    }

    public static Drawable Rectangle(double x, double y, double width, double height, RoughOptions options)
    {
        var o = O(options);
        var paths = new List<OpSet>();
        var outline = RoughRenderer.Rectangle(x, y, width, height, o);
        if (o.Fill is not null)
        {
            List<double[]> points = [[x, y], [x + width, y], [x + width, y + height], [x, y + height]];
            paths.Add(o.FillStyle == "solid" ? RoughRenderer.SolidFillPolygon([points], o) : RoughRenderer.PatternFillPolygons([points], o));
        }
        if (o.Stroke != "none")
        {
            paths.Add(outline);
        }
        return new Drawable("rectangle", paths, o);
    }

    public static Drawable Ellipse(double x, double y, double width, double height, RoughOptions options)
    {
        var o = O(options);
        var paths = new List<OpSet>();
        var ellipseParams = RoughRenderer.GenerateEllipseParams(width, height, o);
        var response = RoughRenderer.EllipseWithParams(x, y, o, ellipseParams);
        if (o.Fill is not null)
        {
            if (o.FillStyle == "solid")
            {
                var shape = RoughRenderer.EllipseWithParams(x, y, o, ellipseParams).OpSet;
                shape.Type = OpSetType.FillPath;
                paths.Add(shape);
            }
            else
            {
                paths.Add(RoughRenderer.PatternFillPolygons([response.EstimatedPoints], o));
            }
        }
        if (o.Stroke != "none")
        {
            paths.Add(response.OpSet);
        }
        return new Drawable("ellipse", paths, o);
    }

    public static Drawable Circle(double x, double y, double diameter, RoughOptions options)
    {
        var drawable = Ellipse(x, y, diameter, diameter, options);
        drawable.Shape = "circle";
        return drawable;
    }

    public static Drawable LinearPath(IReadOnlyList<double[]> points, RoughOptions options)
    {
        var o = O(options);
        return new Drawable("linearPath", [RoughRenderer.LinearPath(points, false, o)], o);
    }

    public static Drawable Curve(IReadOnlyList<double[]> points, RoughOptions options)
    {
        var o = O(options);
        var paths = new List<OpSet>();
        var outline = RoughRenderer.Curve(points, o);
        if (o.Fill is not null && o.Fill != "none" && points.Count >= 3)
        {
            if (o.FillStyle == "solid")
            {
                var fillOptions = o.Clone();
                fillOptions.DisableMultiStroke = true;
                fillOptions.Roughness = o.Roughness != 0 ? o.Roughness + o.FillShapeRoughnessGain : 0;
                var fillShape = RoughRenderer.Curve(points, fillOptions);
                paths.Add(new OpSet(OpSetType.FillPath, MergedShape(fillShape.Ops)));
            }
            else
            {
                var bcurve = PointsOnCurve.CurveToBezier(points);
                var polyPoints = PointsOnCurve.PointsOnBezierCurves(bcurve, 10, (1 + o.Roughness) / 2);
                paths.Add(RoughRenderer.PatternFillPolygons([polyPoints], o));
            }
        }
        if (o.Stroke != "none")
        {
            paths.Add(outline);
        }
        return new Drawable("curve", paths, o);
    }

    public static Drawable Polygon(IReadOnlyList<double[]> points, RoughOptions options)
    {
        var o = O(options);
        var paths = new List<OpSet>();
        var outline = RoughRenderer.LinearPath(points, true, o);
        if (o.Fill is not null)
        {
            var list = new List<double[]>(points);
            paths.Add(o.FillStyle == "solid" ? RoughRenderer.SolidFillPolygon([list], o) : RoughRenderer.PatternFillPolygons([list], o));
        }
        if (o.Stroke != "none")
        {
            paths.Add(outline);
        }
        return new Drawable("polygon", paths, o);
    }

    public static Drawable Path(IReadOnlyList<PathSeg> d, RoughOptions options)
    {
        var o = O(options);
        var paths = new List<OpSet>();
        if (d.Count == 0)
        {
            return new Drawable("path", paths, o);
        }
        var hasFill = o.Fill is not null && o.Fill != "transparent" && o.Fill != "none";
        var hasStroke = o.Stroke != "none";
        var distance = (1 + o.Roughness) / 2;
        var sets = PointsOnCurve.PointsOnPath(d, 1, distance);
        var shape = RoughRenderer.SvgPath(d, o);
        if (hasFill)
        {
            if (o.FillStyle == "solid")
            {
                if (sets.Count == 1)
                {
                    var fillOptions = o.Clone();
                    fillOptions.DisableMultiStroke = true;
                    fillOptions.Roughness = o.Roughness != 0 ? o.Roughness + o.FillShapeRoughnessGain : 0;
                    var fillShape = RoughRenderer.SvgPath(d, fillOptions);
                    paths.Add(new OpSet(OpSetType.FillPath, MergedShape(fillShape.Ops)));
                }
                else
                {
                    paths.Add(RoughRenderer.SolidFillPolygon(sets, o));
                }
            }
            else
            {
                paths.Add(RoughRenderer.PatternFillPolygons(sets, o));
            }
        }
        if (hasStroke)
        {
            paths.Add(shape);
        }
        return new Drawable("path", paths, o);
    }

    private static List<RoughOp> MergedShape(List<RoughOp> input) =>
        [.. input.Where((op, i) => i == 0 || op.Kind != RoughOpKind.Move)];

    /// <summary>SVG 경로 문자열 (rough.js opsToPath와 같은 모양).</summary>
    public static string OpsToPath(OpSet set)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var op in set.Ops)
        {
            var d = op.Data.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            switch (op.Kind)
            {
                case RoughOpKind.Move:
                    sb.Append($"M{d[0]} {d[1]} ");
                    break;
                case RoughOpKind.BCurveTo:
                    sb.Append($"C{d[0]} {d[1]}, {d[2]} {d[3]}, {d[4]} {d[5]} ");
                    break;
                case RoughOpKind.LineTo:
                    sb.Append($"L{d[0]} {d[1]} ");
                    break;
            }
        }
        return sb.ToString().Trim();
    }
}
