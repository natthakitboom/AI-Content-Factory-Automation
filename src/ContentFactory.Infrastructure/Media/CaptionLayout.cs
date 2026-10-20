using System.Globalization;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Media;

public interface ITextWidthMeasurer
{
    /// <summary>
    /// Returns the rendered width of the supplied text in pixels. A production measurer
    /// must be backed by the same font renderer used for the burn-in; the conservative
    /// fallback deliberately over-estimates so it fails closed rather than letting an
    /// overflowing caption through.
    /// </summary>
    double Measure(string text);
}

public sealed record CaptionLayoutOptions(
    int MaxWidthPx = ProductBasicTemplate.CaptionMaxWidth,
    int MaxLines = ProductBasicTemplate.CaptionMaxLines,
    int FontSize = ProductBasicTemplate.CaptionFontSize,
    string FontName = ProductBasicTemplate.FontName);

public sealed record CaptionLayoutResult(
    bool Passed,
    IReadOnlyList<string> Lines,
    IReadOnlyList<ValidationIssue> Issues)
{
    public string AssText
        => string.Join(
            "\\N",
            Lines.Select(CaptionLayout.EscapeAssText));
}

/// <summary>
/// Deterministic grapheme-aware caption layout. Line breaks are chosen at whitespace when
/// one exists and otherwise only at a text-element boundary, so a Thai base character and
/// its combining marks are never separated. More than two lines is an overflow, not a
/// reason to shrink the font.
/// </summary>
public sealed class CaptionLayout
{
    private static readonly char[] BreakCharacters = [' ', '\t', '\u00A0'];
    private readonly CaptionLayoutOptions _options;
    private readonly ITextWidthMeasurer _measurer;

    public CaptionLayout(
        ITextWidthMeasurer measurer,
        CaptionLayoutOptions? options = null)
    {
        _measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));
        _options = options ?? new CaptionLayoutOptions();

        if (_options.MaxWidthPx <= 0 ||
            _options.MaxLines <= 0 ||
            _options.FontSize <= 0)
        {
            throw new ValidationException(
                "CAPTION_LAYOUT_CONFIG_INVALID",
                "Caption width, line count and font size must be positive.");
        }

        if (string.IsNullOrWhiteSpace(_options.FontName))
        {
            throw new ValidationException(
                "CAPTION_LAYOUT_CONFIG_INVALID",
                "A caption font name is required.");
        }
    }

    public CaptionLayoutResult Layout(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CaptionLayoutResult(
                false,
                [],
                [new ValidationIssue("CAPTION_EMPTY", "caption", "Caption text is required.")]);
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var lines = new List<string>();
        var issues = new List<ValidationIssue>();
        var overfull = false;

        foreach (var paragraph in normalized.Split('\n'))
        {
            LayoutParagraph(paragraph.Trim(), lines, ref overfull);
        }

        if (overfull)
        {
            issues.Add(new ValidationIssue(
                "CAPTION_OVERFLOW",
                "caption",
                "A caption line is wider than the template safe area."));
        }

        if (lines.Count > _options.MaxLines)
        {
            issues.Add(new ValidationIssue(
                "CAPTION_OVERFLOW",
                "caption",
                $"A caption needs {lines.Count} lines but the template allows {_options.MaxLines}."));
        }

        if (issues.Count == 0 && lines.Count == 0)
        {
            issues.Add(new ValidationIssue(
                "CAPTION_EMPTY",
                "caption",
                "Caption text is required."));
        }

        return new CaptionLayoutResult(issues.Count == 0, lines, issues);
    }

    public string BuildAss(RenderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var layouts = manifest.Segments
            .Select(segment => (Segment: segment, Layout: Layout(segment.Caption)))
            .ToArray();

        var failed = layouts.FirstOrDefault(item => !item.Layout.Passed);
        if (failed.Segment is not null)
        {
            var issue = failed.Layout.Issues.Count > 0
                ? failed.Layout.Issues[0]
                : new ValidationIssue(
                    "CAPTION_OVERFLOW",
                    "caption",
                    "Caption does not fit the template safe area.");
            throw new ValidationException(
                issue.Code,
                "A caption must fit two safe lines before the video can be rendered.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine("PlayResX: 1080");
        builder.AppendLine("PlayResY: 1920");
        builder.AppendLine("WrapStyle: 2");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        AppendStyle(builder, "Default", bold: false);
        AppendStyle(builder, "CTA", bold: true);
        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine(
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var item in layouts)
        {
            var style = item.Segment.Transform.StartsWith(
                "cta",
                StringComparison.OrdinalIgnoreCase)
                ? "CTA"
                : "Default";
            var startMs = item.Segment.StartMs;
            var endMs = startMs + item.Segment.DurationMs;
            if (endMs <= startMs)
            {
                throw new ValidationException(
                    "CAPTION_TIMING_INVALID",
                    "A caption must have a positive duration.");
            }

            builder.Append("Dialogue: 0,");
            builder.Append(ToAssTime(startMs));
            builder.Append(',');
            builder.Append(ToAssTime(endMs));
            builder.Append(',');
            builder.Append(style);
            builder.Append(",,0,0,0,,");
            builder.AppendLine(item.Layout.AssText);
        }

        return builder.ToString();
    }

    public string BuildSrt(RenderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var builder = new StringBuilder();
        var index = 1;
        foreach (var segment in manifest.Segments)
        {
            var layout = Layout(segment.Caption);
            if (!layout.Passed)
            {
                throw new ValidationException(
                    layout.Issues.Count > 0 ? layout.Issues[0].Code : "CAPTION_OVERFLOW",
                    "A caption must fit two safe lines before subtitles can be exported.");
            }

            var endMs = segment.StartMs + segment.DurationMs;
            builder.Append(index++);
            builder.AppendLine();
            builder.Append(ToSrtTime(segment.StartMs));
            builder.Append(" --> ");
            builder.AppendLine(ToSrtTime(endMs));
            builder.AppendLine(string.Join('\n', layout.Lines));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes text that must be displayed literally inside an ASS dialogue line. Braces,
    /// backslashes and newlines can otherwise become libass override controls.
    /// </summary>
    public static string EscapeAssText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '\r':
                    break;
                case '\n':
                    builder.Append("\\N");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '{':
                    builder.Append("\\{");
                    break;
                case '}':
                    builder.Append("\\}");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    public static string ToAssTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        var centiseconds = milliseconds / 10;
        var hours = centiseconds / 360_000;
        var minutes = centiseconds / 6_000 % 60;
        var seconds = centiseconds / 100 % 60;
        var fraction = centiseconds % 100;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{minutes:00}:{seconds:00}.{fraction:00}");
    }

    public static string ToSrtTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        var hours = milliseconds / 3_600_000;
        var minutes = milliseconds / 60_000 % 60;
        var seconds = milliseconds / 1_000 % 60;
        var fraction = milliseconds % 1_000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00},{fraction:000}");
    }

    internal static IEnumerable<string> TextElements(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }

    private void LayoutParagraph(
        string paragraph,
        ICollection<string> lines,
        ref bool overfull)
    {
        if (paragraph.Length == 0)
        {
            return;
        }

        var current = new StringBuilder();
        foreach (var element in TextElements(paragraph))
        {
            if (current.Length == 0)
            {
                current.Append(element);
                continue;
            }

            if (Measure(current.ToString() + element) <= _options.MaxWidthPx)
            {
                current.Append(element);
                continue;
            }

            var text = current.ToString();
            var lastBreak = text.LastIndexOfAny(BreakCharacters);
            if (lastBreak > 0)
            {
                AddLine(text[..lastBreak], lines, ref overfull);

                var remainder = text[(lastBreak + 1)..];
                current.Clear();
                current.Append(remainder);

                if (current.Length > 0 &&
                    Measure(current.ToString()) > _options.MaxWidthPx)
                {
                    AddLine(current.ToString(), lines, ref overfull);
                    current.Clear();
                }
            }
            else
            {
                AddLine(text, lines, ref overfull);
                current.Clear();
            }

            if (current.Length > 0 &&
                Measure(current.ToString() + element) > _options.MaxWidthPx)
            {
                AddLine(current.ToString(), lines, ref overfull);
                current.Clear();
            }

            current.Append(element);
        }

        if (current.Length > 0)
        {
            AddLine(current.ToString(), lines, ref overfull);
        }
    }

    private void AddLine(
        string value,
        ICollection<string> lines,
        ref bool overfull)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (Measure(trimmed) > _options.MaxWidthPx)
        {
            overfull = true;
        }

        lines.Add(trimmed);
    }

    private void AppendStyle(
        StringBuilder builder,
        string name,
        bool bold)
    {
        // ASS colours are &HAABBGGRR. White text, black outline, translucent shadow.
        builder.Append("Style: ");
        builder.Append(name);
        builder.Append(',');
        builder.Append(_options.FontName);
        builder.Append(',');
        builder.Append(_options.FontSize.ToString(CultureInfo.InvariantCulture));
        builder.Append(",&H00FFFFFF,&H000000FF,&H00000000,&H80000000,");
        builder.Append(bold ? "-1" : "0");
        builder.Append(",0,0,0,100,100,0,0,1,3,0,2,80,80,420,1");
        builder.AppendLine();
    }

    private double Measure(string value)
        => _measurer.Measure(value);
}

/// <summary>
/// Safe fallback for hosts that have not injected native font metrics. It over-estimates
/// Thai/CJK glyph widths and treats combining marks as attached to their text element. It
/// can block a valid caption but must never allow a wider one to pass.
/// </summary>
public sealed class ConservativeTextMeasurer : ITextWidthMeasurer
{
    private readonly double _fontSize;

    public ConservativeTextMeasurer(int fontSize = ProductBasicTemplate.CaptionFontSize)
    {
        if (fontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }

        _fontSize = fontSize;
    }

    public double Measure(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0d;
        foreach (var element in CaptionLayout.TextElements(text))
        {
            var rune = Rune.GetRuneAt(element, 0);
            if (char.IsWhiteSpace(element[0]))
            {
                width += _fontSize * 0.35d;
            }
            else if (IsMark(rune))
            {
                // A combining mark is part of the preceding text element.
            }
            else if (IsWide(rune))
            {
                width += _fontSize * 0.95d;
            }
            else
            {
                width += _fontSize * 0.58d;
            }
        }

        return width;
    }

    private static bool IsMark(Rune rune)
        => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;

    private static bool IsWide(Rune rune)
        => rune.Value is >= 0x0E00 and <= 0x0E7F or
            >= 0x1100 and <= 0x11FF or
            >= 0x2E80 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7AF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x1F300 and <= 0x1FAFF;
}
