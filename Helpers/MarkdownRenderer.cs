using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noted;

internal static partial class MarkdownRenderer
{
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(44, 110, 145));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(91, 113, 125));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(238, 244, 247));

    internal static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            Background = Brushes.White,
            Foreground = TextBrush,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            LineHeight = 21,
            PagePadding = new Thickness(18)
        };

        var lines = NormalizeLines(markdown);
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                index = AddCodeBlock(document, lines, index + 1);
                continue;
            }

            if (TryAddHeading(document, line) ||
                TryAddQuote(document, line) ||
                TryAddRule(document, line))
            {
                index++;
                continue;
            }

            if (TryGetListItem(line, out var isOrdered, out _))
            {
                index = AddList(document, lines, index, isOrdered);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            index = AddParagraph(document, lines, index);
        }

        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph());

        return document;
    }

    private static string[] NormalizeLines(string markdown)
    {
        return (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static int AddCodeBlock(FlowDocument document, string[] lines, int index)
    {
        var codeLines = new List<string>();
        while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
            codeLines.Add(lines[index++]);

        if (index < lines.Length)
            index++;

        document.Blocks.Add(new Paragraph(new Run(string.Join(Environment.NewLine, codeLines)))
        {
            Background = CodeBackground,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 10),
            Padding = new Thickness(10)
        });
        return index;
    }

    private static bool TryAddHeading(FlowDocument document, string line)
    {
        var match = HeadingPattern().Match(line);
        if (!match.Success)
            return false;

        var level = match.Groups[1].Length;
        var paragraph = new Paragraph
        {
            Foreground = AccentBrush,
            FontSize = level switch
            {
                1 => 28,
                2 => 23,
                3 => 19,
                _ => 16
            },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6)
        };
        AddInlineContent(paragraph.Inlines, match.Groups[2].Value);
        document.Blocks.Add(paragraph);
        return true;
    }

    private static bool TryAddQuote(FlowDocument document, string line)
    {
        var match = QuotePattern().Match(line);
        if (!match.Success)
            return false;

        var paragraph = new Paragraph
        {
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Foreground = MutedBrush,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 2, 0, 2)
        };
        AddInlineContent(paragraph.Inlines, match.Groups[1].Value);
        document.Blocks.Add(paragraph);
        return true;
    }

    private static bool TryAddRule(FlowDocument document, string line)
    {
        if (!RulePattern().IsMatch(line))
            return false;

        document.Blocks.Add(new BlockUIContainer(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(201, 223, 236)),
            Height = 1,
            Margin = new Thickness(0, 10, 0, 10)
        }));
        return true;
    }

    private static int AddList(FlowDocument document, string[] lines, int index, bool isOrdered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = isOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(20, 3, 0, 8)
        };

        while (index < lines.Length &&
               TryGetListItem(lines[index], out var currentIsOrdered, out var content) &&
               currentIsOrdered == isOrdered)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddInlineContent(paragraph.Inlines, content);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        document.Blocks.Add(list);
        return index;
    }

    private static bool TryGetListItem(string line, out bool isOrdered, out string content)
    {
        var unordered = UnorderedListPattern().Match(line);
        if (unordered.Success)
        {
            isOrdered = false;
            content = unordered.Groups[1].Value;
            return true;
        }

        var ordered = OrderedListPattern().Match(line);
        if (ordered.Success)
        {
            isOrdered = true;
            content = ordered.Groups[1].Value;
            return true;
        }

        isOrdered = false;
        content = string.Empty;
        return false;
    }

    private static int AddParagraph(FlowDocument document, string[] lines, int index)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 9) };
        while (index < lines.Length &&
               !string.IsNullOrWhiteSpace(lines[index]) &&
               !StartsBlock(lines[index]))
        {
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.Add(new LineBreak());
            AddInlineContent(paragraph.Inlines, lines[index]);
            index++;
        }

        document.Blocks.Add(paragraph);
        return index;
    }

    private static bool StartsBlock(string line)
    {
        return line.StartsWith("```", StringComparison.Ordinal) ||
               HeadingPattern().IsMatch(line) ||
               QuotePattern().IsMatch(line) ||
               RulePattern().IsMatch(line) ||
               UnorderedListPattern().IsMatch(line) ||
               OrderedListPattern().IsMatch(line);
    }

    private static void AddInlineContent(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
                inlines.Add(new Run(text[position..match.Index]));

            var token = match.Value;
            if (token.StartsWith('`'))
            {
                inlines.Add(new Run(token[1..^1])
                {
                    Background = CodeBackground,
                    FontFamily = new FontFamily("Consolas")
                });
            }
            else if (token.StartsWith("**", StringComparison.Ordinal) ||
                     token.StartsWith("__", StringComparison.Ordinal))
            {
                inlines.Add(new Bold(new Run(token[2..^2])));
            }
            else
            {
                inlines.Add(new Italic(new Run(token[1..^1])));
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            inlines.Add(new Run(text[position..]));
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^>\s?(.*)$")]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"^\s*((?:-{3,})|(?:\*{3,})|(?:_{3,}))\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^\s*[-+*]\s+(.+)$")]
    private static partial Regex UnorderedListPattern();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$")]
    private static partial Regex OrderedListPattern();

    [GeneratedRegex(@"(`[^`\r\n]+`)|(\*\*[^*\r\n]+\*\*)|(__[^_\r\n]+__)|(\*[^*\r\n]+\*)|(_[^_\r\n]+_)")]
    private static partial Regex InlinePattern();
}
