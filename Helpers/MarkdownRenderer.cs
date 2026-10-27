using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noted;

internal static partial class MarkdownRenderer
{
    // Markdown becomes native document objects only. HTML, URLs and image paths never
    // become executable markup, navigation targets or resource-loading controls.
    internal static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            LineHeight = 21,
            PagePadding = new Thickness(18)
        };

        document.SetResourceReference(FlowDocument.ForegroundProperty, "NotedTextBrush");

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

            if (TryGetTableHeader(lines, index, out var header, out var alignments))
            {
                index = AddTable(document, lines, index, header, alignments);
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

        var paragraph = new Paragraph(new Run(string.Join(Environment.NewLine, codeLines)))
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 10),
            Padding = new Thickness(10)
        };
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "NotedInputBackgroundBrush");
        document.Blocks.Add(paragraph);
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
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 2, 0, 2)
        };
        paragraph.SetResourceReference(Block.BorderBrushProperty, "NotedAccentBrush");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "NotedSecondaryTextBrush");
        AddInlineContent(paragraph.Inlines, match.Groups[1].Value);
        document.Blocks.Add(paragraph);
        return true;
    }

    private static bool TryAddRule(FlowDocument document, string line)
    {
        if (!RulePattern().IsMatch(line))
            return false;

        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 10)
        };
        rule.SetResourceReference(Border.BackgroundProperty, "NotedBorderBrush");
        document.Blocks.Add(new BlockUIContainer(rule));
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
               !StartsBlock(lines[index]) &&
               !TryGetTableHeader(lines, index, out _, out _))
        {
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.Add(new LineBreak());
            AddInlineContent(paragraph.Inlines, lines[index]);
            index++;
        }

        document.Blocks.Add(paragraph);
        return index;
    }

    private static bool TryGetTableHeader(string[] lines, int index, out List<string> header, out TextAlignment[] alignments)
    {
        header = [];
        alignments = [];
        if (index + 1 >= lines.Length || string.IsNullOrWhiteSpace(lines[index]) || StartsBlock(lines[index]))
            return false;
        var delimiters = SplitTableRow(lines[index + 1], out var delimiterHasPipe);
        if (delimiters.Count == 0 || delimiters.Any(cell => !TableDelimiterPattern().IsMatch(cell)))
            return false;
        header = SplitTableRow(lines[index], out var headerHasPipe);
        if ((!headerHasPipe && !delimiterHasPipe) || header.Count != delimiters.Count)
            return false;
        alignments = delimiters.Select(cell => cell.EndsWith(':')
            ? cell.StartsWith(':') ? TextAlignment.Center : TextAlignment.Right
            : TextAlignment.Left).ToArray();
        return true;
    }

    private static List<string> SplitTableRow(string line, out bool hasPipe)
    {
        line = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        hasPipe = false;
        var leadingPipe = false;
        var lastPipe = -1;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] != '|')
            {
                cell.Append(line[index]);
                continue;
            }
            var backslashes = 0;
            for (var previous = index - 1; previous >= 0 && line[previous] == '\\'; previous--) backslashes++;
            if (backslashes % 2 != 0)
            {
                cell.Length--;
                cell.Append('|');
                continue;
            }
            hasPipe = true;
            leadingPipe |= index == 0;
            lastPipe = index;
            cells.Add(cell.ToString().Trim());
            cell.Clear();
        }
        cells.Add(cell.ToString().Trim());
        if (lastPipe == line.Length - 1 && lastPipe >= 0) cells.RemoveAt(cells.Count - 1);
        if (leadingPipe) cells.RemoveAt(0);
        return cells;
    }

    private static int AddTable(FlowDocument document, string[] lines, int index, List<string> header, TextAlignment[] alignments)
    {
        var rows = new List<List<string>> { header };
        index += 2;
        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index]))
            rows.Add(SplitTableRow(lines[index++], out _));

        var table = new Table
        {
            CellSpacing = 0,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 12)
        };
        table.SetResourceReference(Block.BorderBrushProperty, "NotedBorderBrush");
        for (var column = 0; column < header.Count; column++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            if (rowIndex == 0)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TextElement.BackgroundProperty, "NotedSubtleSurfaceBrush");
            }
            else if (rowIndex % 2 == 0)
                row.SetResourceReference(TextElement.BackgroundProperty, "NotedInputBackgroundBrush");
            group.Rows.Add(row);
            for (var column = 0; column < header.Count; column++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignments[column] };
                AddInlineContent(paragraph.Inlines, column < rows[rowIndex].Count ? rows[rowIndex][column] : "");
                var cell = new TableCell(paragraph)
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    BorderThickness = new Thickness(0, 0, column + 1 < header.Count ? 1 : 0, rowIndex + 1 < rows.Count ? 1 : 0)
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "NotedBorderBrush");
                row.Cells.Add(cell);
            }
        }
        document.Blocks.Add(table);
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
            if (token.StartsWith('['))
            {
                var separator = token.IndexOf("](", StringComparison.Ordinal);
                var label = token[1..separator];
                var destination = token[(separator + 2)..^1];
                inlines.Add(new Run($"{label} ({destination})") { TextDecorations = TextDecorations.Underline });
            }
            else if (token.StartsWith('`'))
            {
                var code = new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas")
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "NotedInputBackgroundBrush");
                inlines.Add(code);
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

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex TableDelimiterPattern();

    [GeneratedRegex(@"(\[[^\]\r\n]+\]\([^()\r\n]*\))|(`[^`\r\n]+`)|(\*\*[^*\r\n]+\*\*)|(__[^_\r\n]+__)|(\*[^*\r\n]+\*)|(_[^_\r\n]+_)")]
    private static partial Regex InlinePattern();
}
