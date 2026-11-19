using DayDeck.Core.Drawings;

namespace DayDeck.App.Views.Drawing;

/// <summary>
/// Excalidraw 글자 배치 규칙 (element/textElement.ts, textWrapping.ts): 글자 요소의 <c>text</c>에는 줄바꿈한 결과를,
/// <c>originalText</c>에는 입력한 그대로를 둔다. 크기 = 가장 긴 줄 너비 × (줄 수 × 글자 크기 × 줄 높이).
/// 상자 안 글자는 안쪽 여백 5, 원·마름모는 안쪽 사각형에 맞추고, 넘치면 상자 높이를 키운다. 화살표 글자는 가운데 지점.
/// </summary>
internal static class DrawingText
{
    public const double BoundTextPadding = 5;

    public static double LineHeightPx(ExcalidrawElement text) => text.FontSize * text.LineHeight;

    public static (double Width, double Height) Measure(string text, double fontSize, int family, double lineHeight, double pixelsPerDip)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var width = lines.Max(l => DrawingRenderer.LineWidth(l, fontSize, family, pixelsPerDip));
        return (width, lines.Length * fontSize * lineHeight);
    }

    public static double MaxWidth(ExcalidrawElement container, ExcalidrawElement? text)
    {
        var width = container.Width;
        if (container.Type == ExcalidrawElement.Arrow)
        {
            var minWidth = (text?.FontSize ?? 20) * 11;
            return Math.Max(0.7 * width, minWidth);
        }
        return container.Type switch
        {
            ExcalidrawElement.Ellipse => Math.Round(width / 2 * Math.Sqrt(2)) - BoundTextPadding * 2,
            ExcalidrawElement.Diamond => Math.Round(width / 2) - BoundTextPadding * 2,
            _ => width - BoundTextPadding * 2,
        };
    }

    public static double MaxHeight(ExcalidrawElement container, ExcalidrawElement text)
    {
        var height = container.Height;
        if (container.Type == ExcalidrawElement.Arrow)
        {
            return height - BoundTextPadding * 8 * 2 <= 0 ? text.Height : height;
        }
        return container.Type switch
        {
            ExcalidrawElement.Ellipse => Math.Round(height / 2 * Math.Sqrt(2)) - BoundTextPadding * 2,
            ExcalidrawElement.Diamond => Math.Round(height / 2) - BoundTextPadding * 2,
            _ => height - BoundTextPadding * 2,
        };
    }

    private static double ContainerDimensionFor(double dimension, string type)
    {
        dimension = Math.Ceiling(dimension);
        const double padding = BoundTextPadding * 2;
        return type switch
        {
            ExcalidrawElement.Ellipse => Math.Round((dimension + padding) / Math.Sqrt(2) * 2),
            ExcalidrawElement.Arrow => dimension + padding * 8,
            ExcalidrawElement.Diamond => 2 * (dimension + padding),
            _ => dimension + padding,
        };
    }

    private static bool IsCjk(char c) =>
        c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    /// <summary>
    /// 줄바꿈: 단어(공백 기준) 단위로 채우고, 한글·한자는 글자마다 끊을 수 있다. 한 단어가 너무 길면 글자 단위로 자른다.
    /// 줄 끝 공백은 지운다 (Excalidraw wrapText와 같은 규칙).
    /// </summary>
    public static string Wrap(string text, double maxWidth, double fontSize, int family, double pixelsPerDip)
    {
        if (maxWidth <= 0 || !double.IsFinite(maxWidth))
        {
            return text;
        }
        double W(string s) => DrawingRenderer.LineWidth(s, fontSize, family, pixelsPerDip);
        var result = new List<string>();
        foreach (var original in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (W(original) <= maxWidth)
            {
                result.Add(original);
                continue;
            }
            // 끊을 수 있는 조각: 공백 묶음, 한글·한자 한 글자, 그 밖의 연속 글자(단어).
            var tokens = new List<string>();
            var i = 0;
            while (i < original.Length)
            {
                var start = i;
                if (char.IsWhiteSpace(original[i]))
                {
                    while (i < original.Length && char.IsWhiteSpace(original[i]))
                    {
                        i++;
                    }
                }
                else if (IsCjk(original[i]))
                {
                    i++;
                    // 한글 뒤에 붙은 문장 부호는 같은 조각으로.
                    while (i < original.Length && char.IsPunctuation(original[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    while (i < original.Length && !char.IsWhiteSpace(original[i]) && !IsCjk(original[i]))
                    {
                        i++;
                    }
                }
                tokens.Add(original[start..i]);
            }
            var line = "";
            foreach (var token in tokens)
            {
                var candidate = line + token;
                if (W(candidate.TrimEnd()) <= maxWidth || line.Length == 0 && W(token) <= maxWidth)
                {
                    line = candidate;
                    continue;
                }
                if (line.Trim().Length > 0)
                {
                    result.Add(line.TrimEnd());
                }
                line = "";
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }
                if (W(token) <= maxWidth)
                {
                    line = token;
                    continue;
                }
                // 너무 긴 단어는 글자 단위로.
                foreach (var ch in token)
                {
                    if (line.Length > 0 && W(line + ch) > maxWidth)
                    {
                        result.Add(line);
                        line = "";
                    }
                    line += ch;
                }
            }
            result.Add(line.TrimEnd());
        }
        return string.Join("\n", result);
    }

    /// <summary>상자 밖 글자: 크기만 다시 잰다 (자동 크기).</summary>
    public static void MeasureFree(ExcalidrawElement text, double pixelsPerDip)
    {
        text.TextContent = text.OriginalText;
        var (width, height) = Measure(text.TextContent, text.FontSize, text.FontFamily, text.LineHeight, pixelsPerDip);
        text.Width = width;
        text.Height = height;
    }

    /// <summary>상자·화살표 안 글자를 줄바꿈하고 자리를 잡는다 (넘치면 상자 높이를 키운다).</summary>
    public static void LayoutBound(ExcalidrawDocument doc, ExcalidrawElement container, ExcalidrawElement text, double pixelsPerDip)
    {
        var maxWidth = MaxWidth(container, text);
        text.TextContent = Wrap(text.OriginalText, maxWidth, text.FontSize, text.FontFamily, pixelsPerDip);
        var (width, height) = Measure(text.TextContent, text.FontSize, text.FontFamily, text.LineHeight, pixelsPerDip);
        text.Width = width;
        text.Height = height;

        if (container.Type == ExcalidrawElement.Arrow)
        {
            var mid = ArrowLabelCenter(container);
            text.X = mid.X - width / 2;
            text.Y = mid.Y - height / 2;
            return;
        }
        if (height > MaxHeight(container, text))
        {
            container.Height = ContainerDimensionFor(height, container.Type);
            DrawingGeometry.UpdateBoundArrows(doc, [container]);
        }
        var offsetX = BoundTextPadding;
        var offsetY = BoundTextPadding;
        if (container.Type == ExcalidrawElement.Ellipse)
        {
            offsetX += container.Width / 2 * (1 - Math.Sqrt(2) / 2);
            offsetY += container.Height / 2 * (1 - Math.Sqrt(2) / 2);
        }
        else if (container.Type == ExcalidrawElement.Diamond)
        {
            offsetX += container.Width / 4;
            offsetY += container.Height / 4;
        }
        var maxH = MaxHeight(container, text);
        var maxW = MaxWidth(container, text);
        var x0 = container.X + offsetX;
        var y0 = container.Y + offsetY;
        text.Y = text.VerticalAlign switch
        {
            "top" => y0,
            "bottom" => y0 + (maxH - height),
            _ => y0 + (maxH / 2 - height / 2),
        };
        text.X = text.TextAlign switch
        {
            "left" => x0,
            "right" => x0 + (maxW - width),
            _ => x0 + (maxW / 2 - width / 2),
        };
    }

    /// <summary>화살표 글자의 가운데: 점이 홀수 개면 가운데 점, 짝수 개면 가운데 구간의 중간(곡선이면 곡선 위).</summary>
    public static Pt ArrowLabelCenter(ExcalidrawElement arrow)
    {
        var points = DrawingGeometry.AbsolutePoints(arrow);
        if (points.Count == 0)
        {
            return new Pt(arrow.X, arrow.Y);
        }
        if (points.Count % 2 == 1)
        {
            return points[points.Count / 2];
        }
        var index = points.Count / 2 - 1;
        if (arrow.Roundness is not null && points.Count > 2)
        {
            return DrawingGeometry.CurvePoint(points, index, 0.5);
        }
        return (points[index] + points[index + 1]) * 0.5;
    }
}
