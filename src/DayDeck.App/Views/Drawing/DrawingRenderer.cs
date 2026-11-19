using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using DayDeck.App.Services;
using DayDeck.Core.Drawings;
using DayDeck.Core.Drawings.Rough;

namespace DayDeck.App.Views.Drawing;

/// <summary>
/// Excalidraw 요소를 Excalidraw와 같은 모양으로 그린다 (화면과 PNG 내보내기가 함께 쓴다).
/// 도형 좌표는 <see cref="ExcalidrawShapes"/>(rough.js·Excalidraw 규칙을 옮긴 것)에서 받아 canvas와 같은 방식으로 칠한다:
/// 채우기 → 빗금 → 테두리, 둥근 선 끝·이음, Excalidraw의 점선 간격, 글자는 글꼴 지표로 줄마다 기준선에 맞춘다.
/// 어두운 화면에서는 Excalidraw처럼 색을 뒤집는다 (invert 93% + 색상 180도).
/// </summary>
internal static class DrawingRenderer
{
    private static readonly Uri FontBase = new("pack://application:,,,/");
    private const string Hangul = "./Assets/Fonts/#Xiaolai, Malgun Gothic, Segoe UI Emoji";

    /// <summary>Excalidraw 글꼴 번호 → 글꼴 (앱에 넣은 Excalidraw 글꼴, 한글은 Xiaolai).</summary>
    public static FontFamily FontFor(int family) => FontCache.GetOrAdd(family, f => new FontFamily(FontBase, f switch
    {
        1 => "./Assets/Fonts/#Virgil, " + Hangul,
        2 => "Helvetica, Arial, Liberation Sans, Malgun Gothic, Segoe UI Emoji",
        3 => "Cascadia Code, Cascadia Mono, Consolas, Malgun Gothic, Segoe UI Emoji",
        6 => "./Assets/Fonts/#Nunito, Malgun Gothic, Segoe UI Emoji",
        7 => "./Assets/Fonts/#Lilita One, Malgun Gothic, Segoe UI Emoji",
        8 => "./Assets/Fonts/#Comic Shanns, Malgun Gothic, Segoe UI Emoji",
        9 => "Liberation Sans, Arial, Malgun Gothic, Segoe UI Emoji",
        _ => "./Assets/Fonts/#Excalifont, " + Hangul,
    }));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, FontFamily> FontCache = new();

    /// <summary>Excalidraw 글꼴 지표 (unitsPerEm, ascender, descender, 줄 높이). 글자를 줄마다 기준선에 놓는 데 쓴다.</summary>
    public static (double UnitsPerEm, double Ascender, double Descender, double LineHeight) Metrics(int family) => family switch
    {
        2 => (2048, 1577, -471, 1.15),
        3 => (2048, 1900, -480, 1.2),
        6 => (1000, 1011, -353, 1.35),
        7 => (1000, 923, -220, 1.15),
        8 => (1000, 750, -250, 1.25),
        9 => (2048, 1854, -434, 1.15),
        _ => (1000, 886, -374, 1.25),
    };

    /// <summary>글꼴 번호의 기본 줄 높이 (새 글자 요소의 lineHeight).</summary>
    public static double DefaultLineHeight(int family) => Metrics(family).LineHeight;

    /// <summary>첫 줄 기준선까지의 거리 (Excalidraw getVerticalOffset).</summary>
    public static double VerticalOffset(int family, double fontSize, double lineHeightPx)
    {
        var (unitsPerEm, ascender, descender, _) = Metrics(family);
        var em = fontSize / unitsPerEm;
        var lineGap = (lineHeightPx - em * ascender + em * descender) / 2;
        return em * ascender + lineGap;
    }

    /// <summary>"#rrggbb", "#rgb", "#rrggbbaa", 색 이름. 투명이면 null.</summary>
    public static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ExcalidrawShapes.IsTransparent(value))
        {
            return null;
        }
        try
        {
            var text = value.Trim();
            // CSS #rrggbbaa → WPF #aarrggbb
            if (text.Length == 9 && text[0] == '#')
            {
                text = "#" + text[7..] + text[1..7];
            }
            else if (text.Length == 5 && text[0] == '#')
            {
                text = "#" + text[4] + text[1..4];
            }
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 그리기 색. 어두운 화면(themed)에서는 Excalidraw의 어두운 테마처럼 invert(93%) + hue-rotate(180°)를 적용한다 (파일 값은 그대로).
    /// </summary>
    public static Color StrokeColor(string value, bool themed) => Themed(ParseColor(value) ?? Colors.Transparent, themed);

    public static Color Themed(Color color, bool themed)
    {
        if (!themed || !ThemeService.IsDark)
        {
            return color;
        }
        static double Invert(byte c) => c / 255.0 * (1 - 2 * 0.93) + 0.93;
        var r = Invert(color.R);
        var g = Invert(color.G);
        var b = Invert(color.B);
        // hue-rotate(180deg) 행렬 (cos = -1, sin = 0)
        var nr = -0.574 * r + 1.43 * g + 0.144 * b;
        var ng = 0.426 * r + 0.43 * g + 0.144 * b;
        var nb = 0.426 * r + 1.43 * g - 0.856 * b;
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromArgb(color.A, B(nr), B(ng), B(nb));
    }

    public static FormattedText MeasureText(string text, double fontSize, int fontFamily, double pixelsPerDip, Brush brush, double? maxWidth = null)
    {
        var formatted = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFor(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), fontSize, brush, pixelsPerDip)
        {
            LineHeight = fontSize * DefaultLineHeight(fontFamily),
        };
        if (maxWidth is { } width && width > 1)
        {
            formatted.MaxTextWidth = width;
        }
        return formatted;
    }

    /// <summary>한 줄의 너비 (canvas measureText처럼 끝 공백 포함).</summary>
    public static double LineWidth(string line, double fontSize, int fontFamily, double pixelsPerDip) =>
        line.Length == 0 ? 0 : MeasureText(line, fontSize, fontFamily, pixelsPerDip, Brushes.Black).WidthIncludingTrailingWhitespace;

    public static void DrawDocument(DrawingContext dc, ExcalidrawDocument doc, double pixelsPerDip, bool themed)
    {
        foreach (var element in doc.Visible)
        {
            Draw(dc, doc, element, pixelsPerDip, themed);
        }
    }

    /// <summary>요소의 회전 중심 (선·펜은 점들의 범위 가운데).</summary>
    public static Pt RotationCenter(ExcalidrawElement e)
    {
        if ((e.IsLinear || e.Type == ExcalidrawElement.FreeDraw) && e.Points.Count > 0)
        {
            var minX = e.Points.Min(p => p.X);
            var maxX = e.Points.Max(p => p.X);
            var minY = e.Points.Min(p => p.Y);
            var maxY = e.Points.Max(p => p.Y);
            return new Pt(e.X + (minX + maxX) / 2, e.Y + (minY + maxY) / 2);
        }
        return new Pt(e.X + e.Width / 2, e.Y + e.Height / 2);
    }

    public static void Draw(DrawingContext dc, ExcalidrawDocument doc, ExcalidrawElement e, double pixelsPerDip, bool themed)
    {
        var pushes = 0;
        if (e.Opacity < 100)
        {
            dc.PushOpacity(Math.Clamp(e.Opacity / 100, 0, 1));
            pushes++;
        }
        if (e.Angle != 0)
        {
            var center = RotationCenter(e);
            dc.PushTransform(new RotateTransform(e.Angle * 180 / Math.PI, center.X, center.Y));
            pushes++;
        }
        dc.PushTransform(new TranslateTransform(e.X, e.Y));
        pushes++;

        if (e.Type == ExcalidrawElement.Text)
        {
            DrawText(dc, e, pixelsPerDip, themed);
        }
        else
        {
            // 화살표 글자 아래는 선을 비운다 (Excalidraw의 mask).
            var clipped = false;
            if (e.Type == ExcalidrawElement.Arrow && DrawingGeometry.BoundText(doc, e) is { } label && label.TextContent.Length > 0)
            {
                var all = new RectangleGeometry(new Rect(-100000, -100000, 200000, 200000));
                var hole = new RectangleGeometry(new Rect(label.X - e.X, label.Y - e.Y, Math.Max(0, label.Width), Math.Max(0, label.Height)));
                dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, all, hole));
                clipped = true;
            }
            foreach (var part in Shapes(e))
            {
                DrawPart(dc, part, themed);
            }
            if (e.Type == ExcalidrawElement.FreeDraw)
            {
                var brush = Freeze(new SolidColorBrush(StrokeColor(e.StrokeColor, themed)));
                dc.DrawGeometry(brush, null, CachedFreedraw(e));
            }
            if (clipped)
            {
                dc.Pop();
            }
        }

        for (var i = 0; i < pushes; i++)
        {
            dc.Pop();
        }
    }

    private static void DrawText(DrawingContext dc, ExcalidrawElement e, double pixelsPerDip, bool themed)
    {
        var brush = Freeze(new SolidColorBrush(StrokeColor(e.StrokeColor, themed)));
        var lines = e.TextContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lineHeightPx = e.FontSize * e.LineHeight;
        var verticalOffset = VerticalOffset(e.FontFamily, e.FontSize, lineHeightPx);
        var typeface = new Typeface(FontFor(e.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }
            var formatted = new FormattedText(lines[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, e.FontSize, brush, pixelsPerDip);
            var width = formatted.WidthIncludingTrailingWhitespace;
            var x = e.TextAlign switch
            {
                "center" => e.Width / 2 - width / 2,
                "right" => e.Width - width,
                _ => 0,
            };
            var baseline = i * lineHeightPx + verticalOffset;
            dc.DrawText(formatted, new Point(x, baseline - formatted.Baseline));
        }
    }

    // ---------- 도형 (rough.js 결과) ----------

    /// <summary>한 번 만든 모양은 요소 내용이 바뀌기 전까지 다시 쓴다 (빗금 계산이 무거워서).</summary>
    private sealed class Cached
    {
        public string Key = "";
        public List<Part> Parts = [];
        public Geometry? Freedraw;
    }

    private static readonly ConditionalWeakTable<JsonObject, Cached> Cache = new();

    private sealed record Part(OpSetType Type, Geometry Geometry, string? Color, double Width, double[]? Dash, bool EvenOdd);

    private static Cached Entry(ExcalidrawElement e)
    {
        var key = e.Node.ToJsonString();
        var entry = Cache.GetOrCreateValue(e.Node);
        if (entry.Key != key)
        {
            entry.Key = key;
            entry.Parts = BuildParts(e);
            entry.Freedraw = null;
        }
        return entry;
    }

    private static List<Part> Shapes(ExcalidrawElement e) => Entry(e).Parts;

    private static Geometry CachedFreedraw(ExcalidrawElement e)
    {
        var entry = Entry(e);
        return entry.Freedraw ??= ToGeometry(ExcalidrawShapes.FreedrawOutline(e), FillRule.Nonzero);
    }

    private static List<Part> BuildParts(ExcalidrawElement e)
    {
        var parts = new List<Part>();
        foreach (var drawable in ExcalidrawShapes.Generate(e))
        {
            var o = drawable.Options;
            foreach (var set in drawable.Sets)
            {
                switch (set.Type)
                {
                    case OpSetType.Path:
                        if (o.Stroke != "none")
                        {
                            parts.Add(new Part(set.Type, ToGeometry(set.Ops, FillRule.Nonzero), o.Stroke, o.StrokeWidth, o.StrokeLineDash, false));
                        }
                        break;
                    case OpSetType.FillPath:
                    {
                        var evenOdd = drawable.Shape is "curve" or "polygon" or "path";
                        parts.Add(new Part(set.Type, ToGeometry(set.Ops, evenOdd ? FillRule.EvenOdd : FillRule.Nonzero), o.Fill, 0, null, evenOdd));
                        break;
                    }
                    case OpSetType.FillSketch:
                    {
                        var weight = o.FillWeight < 0 ? o.StrokeWidth / 2 : o.FillWeight;
                        parts.Add(new Part(set.Type, ToGeometry(set.Ops, FillRule.Nonzero), o.Fill, weight, null, false));
                        break;
                    }
                }
            }
        }
        return parts;
    }

    private static void DrawPart(DrawingContext dc, Part part, bool themed)
    {
        if (ParseColor(part.Color) is not { } raw)
        {
            return;
        }
        var brush = Freeze(new SolidColorBrush(Themed(raw, themed)));
        if (part.Type == OpSetType.FillPath)
        {
            dc.DrawGeometry(brush, null, part.Geometry);
            return;
        }
        var pen = new Pen(brush, Math.Max(0.1, part.Width))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (part.Dash is { Length: > 0 } dash)
        {
            // canvas의 점선 길이(px)를 WPF 단위(선 굵기 배수)로. 둥근 끝이 덧붙는 것도 canvas와 같다.
            pen.DashStyle = new DashStyle(dash.Select(d => d / pen.Thickness), 0);
            pen.DashCap = PenLineCap.Round;
        }
        pen.Freeze();
        dc.DrawGeometry(null, pen, part.Geometry);
    }

    private static Geometry ToGeometry(List<RoughOp> ops, FillRule rule)
    {
        var geometry = new StreamGeometry { FillRule = rule };
        using (var ctx = geometry.Open())
        {
            var open = false;
            foreach (var op in ops)
            {
                var d = op.Data;
                switch (op.Kind)
                {
                    case RoughOpKind.Move:
                        ctx.BeginFigure(new Point(d[0], d[1]), isFilled: true, isClosed: false);
                        open = true;
                        break;
                    case RoughOpKind.BCurveTo when open:
                        ctx.BezierTo(new Point(d[0], d[1]), new Point(d[2], d[3]), new Point(d[4], d[5]), isStroked: true, isSmoothJoin: false);
                        break;
                    case RoughOpKind.LineTo when open:
                        ctx.LineTo(new Point(d[0], d[1]), isStroked: true, isSmoothJoin: false);
                        break;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry ToGeometry(List<PathSeg> path, FillRule rule)
    {
        var geometry = new StreamGeometry { FillRule = rule };
        using (var ctx = geometry.Open())
        {
            var open = false;
            foreach (var (key, d) in path)
            {
                switch (key)
                {
                    case 'M':
                        ctx.BeginFigure(new Point(d[0], d[1]), isFilled: true, isClosed: false);
                        open = true;
                        break;
                    case 'Q' when open:
                        ctx.QuadraticBezierTo(new Point(d[0], d[1]), new Point(d[2], d[3]), isStroked: false, isSmoothJoin: false);
                        break;
                    case 'L' when open:
                        ctx.LineTo(new Point(d[0], d[1]), isStroked: false, isSmoothJoin: false);
                        break;
                    case 'C' when open:
                        ctx.BezierTo(new Point(d[0], d[1]), new Point(d[2], d[3]), new Point(d[4], d[5]), isStroked: false, isSmoothJoin: false);
                        break;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public static StreamGeometry Polyline(IReadOnlyList<Pt> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (points.Count > 0)
            {
                ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: closed, isClosed: closed);
                ctx.PolyLineTo([.. points.Skip(1).Select(p => new Point(p.X, p.Y))], isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
