using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class MarkdownPreviewTests
{
    [TestMethod]
    public void StrikethroughCommandRendersWithoutChangingCodeOrUnmatchedDelimiters() =>
        Noted.Tests.Services.WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "remove" };
        using var surface = new Noted.Tests.Services.WpfEditorTestSurface(editor);
        EditorTextWrapping.Attach(editor);
        editor.SelectAll();
        EditorTextWrapping.Strikethrough.Execute(null, editor);
        Assert.AreEqual("~~remove~~", editor.Text);

        var document = MarkdownRenderer.Render(editor.Text + " `~~code~~` ~~unfinished");
        var paragraph = (Paragraph)document.Blocks.FirstBlock;
        var strike = paragraph.Inlines.OfType<Run>().Single(run => run.Text == "remove");
        Assert.AreEqual(TextDecorationLocation.Strikethrough, strike.TextDecorations.Single().Location);
        var code = paragraph.Inlines.OfType<Run>().Single(run => run.Text == "~~code~~");
        Assert.AreEqual("Consolas", code.FontFamily.Source);
        Assert.AreEqual(0, code.TextDecorations.Count);
        StringAssert.Contains(Text(document), "~~unfinished");
        editor.Undo();
        Assert.AreEqual("remove", editor.Text);
    });

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void FencesKeepShorterMarkersAndSuffixLinesLiteral(string newline) => RunSta(() =>
    {
        var code = string.Join(newline, "```", "**literal** ~~literal~~", "````suffix", "  indented");
        var source = "before" + newline + "````md" + newline + code + newline +
            "  ````` \t" + newline + "after";
        var paragraphs = MarkdownRenderer.Render(source).Blocks.Cast<Paragraph>().ToArray();
        Assert.AreEqual(3, paragraphs.Length);
        Assert.AreEqual("before", new TextRange(paragraphs[0].ContentStart, paragraphs[0].ContentEnd).Text.TrimEnd('\r', '\n'));
        Assert.AreEqual(code.Replace(newline, Environment.NewLine),
            new TextRange(paragraphs[1].ContentStart, paragraphs[1].ContentEnd).Text.TrimEnd('\r', '\n'));
        Assert.AreEqual("Consolas", paragraphs[1].FontFamily.Source);
        Assert.AreEqual(1, paragraphs[1].Inlines.Count);
        Assert.AreEqual("after", new TextRange(paragraphs[2].ContentStart, paragraphs[2].ContentEnd).Text.TrimEnd('\r', '\n'));
    });

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void IndentedFencesRemoveOnlyOpeningIndentationFromCode(int spaces) => RunSta(() =>
    {
        var indent = new string(' ', spaces);
        var source = "paragraph\n" + indent + "```cs\n" + indent + "**literal**\n" +
            indent + "  extra\n" + indent + "```\n\n| A | B |\n| --- | --- |\n| x | y |";
        var blocks = MarkdownRenderer.Render(source).Blocks.ToArray();
        Assert.AreEqual(3, blocks.Length);
        var code = (Paragraph)blocks[1];
        Assert.AreEqual("Consolas", code.FontFamily.Source);
        Assert.AreEqual("**literal**" + Environment.NewLine + "  extra",
            new TextRange(code.ContentStart, code.ContentEnd).Text.TrimEnd('\r', '\n'));
        Assert.IsInstanceOfType<Table>(blocks[2]);
    });

    [TestMethod]
    public void UnclosedFenceKeepsRemainingContentLiteral() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("  ````\n  **literal**\n  ```\n  ~~literal~~");
        var code = (Paragraph)document.Blocks.Single();
        Assert.AreEqual("Consolas", code.FontFamily.Source);
        Assert.AreEqual("**literal**" + Environment.NewLine + "```" + Environment.NewLine + "~~literal~~",
            Text(document).TrimEnd('\r', '\n'));
    });

    [TestMethod]
    public void StrikethroughUsesExistingInlinePathInTablesAndTracksThemes() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("| Status |\n| --- |\n| ~~removed~~ |");
        var table = document.Blocks.OfType<Table>().Single();
        var run = ((Paragraph)table.RowGroups[0].Rows[1].Cells[0].Blocks.FirstBlock).Inlines.OfType<Run>().Single();
        Assert.AreEqual("removed", run.Text);
        Assert.AreEqual(TextDecorationLocation.Strikethrough, run.TextDecorations.Single().Location);
        using var manager = new AppThemeManager(document.Resources, AppThemeMode.Light, "#15FFFF");
        foreach (var theme in Enum.GetValues<AppThemeMode>())
        {
            manager.Apply(theme, "#15FFFF");
            Assert.AreSame(document.Resources["NotedTextBrush"], run.Foreground);
        }
    });

    [TestMethod]
    public void RenderedMarkdownTracksThemeResourcesWithoutRerendering() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("# Heading\n\nbody **bold** *italic* `code` [link](example)\n\n> quote\n\n- item\n\n```\ncode\n```\n\n---");
        using var manager = new AppThemeManager(document.Resources, AppThemeMode.Light, "#15FFFF");
        var blocks = document.Blocks.ToArray();
        foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Midnight, AppThemeMode.OLED, AppThemeMode.Warm })
        foreach (var accent in new[] { "#15FFFF", "#000000", "#FFFFFF" })
        {
            manager.Apply(theme, accent);
            var text = document.Resources["NotedTextBrush"];
            Assert.AreSame(text, document.Foreground);
            Assert.AreSame(text, blocks[0].Foreground);
            foreach (var inline in ((Paragraph)blocks[1]).Inlines)
                Assert.AreSame(text, inline.Foreground);
            var quote = (Paragraph)blocks[2];
            Assert.AreSame(document.Resources["NotedSecondaryTextBrush"], quote.Foreground);
            Assert.AreSame(document.Resources["NotedAccentBrush"], quote.BorderBrush);
            var list = (System.Windows.Documents.List)blocks[3];
            Assert.AreSame(text, list.Foreground);
            Assert.AreSame(text, list.ListItems.First().Blocks.First().Foreground);
            Assert.AreSame(document.Resources["NotedInputBackgroundBrush"], blocks[4].Background);
            var code = ((Paragraph)blocks[1]).Inlines.OfType<Run>().Single(run => run.Text == "code");
            Assert.AreSame(document.Resources["NotedInputBackgroundBrush"], code.Background);
            var rule = (Border)((BlockUIContainer)blocks[5]).Child;
            Assert.AreSame(document.Resources["NotedBorderBrush"], rule.Background);
            Assert.AreEqual(System.Windows.Media.Brushes.Transparent, document.Background);
        }
    });

    [TestMethod]
    public void EscapedNumberedMarkersDisplayWithoutBackslashes() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("1\\. first\n2\\. second\n\n`3\\.`\n\n4\\\\. literal slash");
        var paragraphs = document.Blocks.OfType<Paragraph>().ToArray();

        Assert.AreEqual(3, paragraphs.Length);
        Assert.AreEqual("1. first" + Environment.NewLine + "2. second",
            new TextRange(paragraphs[0].ContentStart, paragraphs[0].ContentEnd).Text.TrimEnd('\r', '\n'));
        Assert.AreEqual(@"3\.", paragraphs[1].Inlines.OfType<Run>().Single().Text);
        Assert.AreEqual(@"4\. literal slash",
            new TextRange(paragraphs[2].ContentStart, paragraphs[2].ContentEnd).Text.TrimEnd('\r', '\n'));
    });

    [TestMethod]
    public void RendererSupportsCommonBlocksAndInlineFormatting() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("# Heading\n\n**bold** *italic* `code`\n\n> quote\n\n- first\n- second\n\n1. ordered\n\n```cs\n<literal>\n```\n\n---");
        var blocks = document.Blocks.ToArray();
        Assert.AreEqual(7, blocks.Length);
        Assert.AreEqual(28d, ((Paragraph)blocks[0]).FontSize);
        var inlines = ((Paragraph)blocks[1]).Inlines.ToArray();
        Assert.IsTrue(inlines.Any(inline => inline is Bold));
        Assert.IsTrue(inlines.Any(inline => inline is Italic));
        Assert.AreEqual(2, ((System.Windows.Documents.List)blocks[3]).ListItems.Count);
        Assert.AreEqual(TextMarkerStyle.Decimal, ((System.Windows.Documents.List)blocks[4]).MarkerStyle);
        Assert.IsInstanceOfType<BlockUIContainer>(blocks[6]);
        Assert.IsTrue(Text(document).Contains("<literal>", StringComparison.Ordinal));
    });

    [TestMethod]
    public void OverviewTableRendersAsStyledCellsWithInlineCode() => RunSta(() =>
    {
        const string source = """
            # Golf Booking Automation System Overview

            This workspace contains four separately versioned repositories:

            | Repository | Runtime | Confirmed responsibility | Azure deployment |
            | --- | --- | --- | --- |
            | `TeeTimeClient` | React 18, Vite | Login and booking request UI | Azure Static Web Apps |
            | `BookerAPI` | ASP.NET Core .NET 8 | JWT authentication and booking HTTP endpoint | Azure App Service `TeeBookerAPI` |
            | `TeeTimeBookerFunc` | Azure Functions isolated .NET 8 | Queue consumption, scraper HTTP call, manual retry | Function App `TeeTimeBookerFunc` |
            | `TeeTimeWebScraper` | Express, Playwright, Node 20 | Browser automation | Container App `tee-time-booking-container` |

            ## System diagram
            ```
            Browser | API
            ```
            """;
        var document = MarkdownRenderer.Render(source);
        var table = document.Blocks.OfType<Table>().Single();
        var rows = table.RowGroups.Single().Rows;
        Assert.AreEqual(4, table.Columns.Count);
        Assert.AreEqual(5, rows.Count);
        Assert.AreEqual(0d, table.CellSpacing);
        Assert.AreEqual(new Thickness(1), table.BorderThickness);
        Assert.AreEqual(FontWeights.SemiBold, rows[0].FontWeight);
        Assert.AreEqual("Repository", CellText(rows[0].Cells[0]));
        Assert.AreEqual("TeeTimeClient", CellText(rows[1].Cells[0]));
        Assert.AreEqual("Consolas", ((Paragraph)rows[1].Cells[0].Blocks.FirstBlock).Inlines.FirstInline.FontFamily.Source);
        foreach (var row in rows)
        foreach (var cell in row.Cells)
        {
            Assert.AreEqual(new Thickness(10, 6, 10, 6), cell.Padding);
            Assert.AreEqual(new Thickness(0), ((Paragraph)cell.Blocks.FirstBlock).Margin);
        }
        Assert.IsTrue(document.Blocks.OfType<Paragraph>().Any(paragraph => paragraph.FontSize == 23));
        StringAssert.Contains(Text(document), "Browser | API");
        Assert.IsFalse(Text(document).Contains("| --- |", StringComparison.Ordinal));
    });

    [TestMethod]
    [DataRow("| A | B |\n| --- | --- |\n| x | y |")]
    [DataRow("A | B\n--- | ---\nx | y")]
    [DataRow("| A | B\n:-: | ---:\nx | y |")]
    [DataRow(" A | B | \n | - | - | \n| x | y ")]
    public void TablesAcceptOptionalOuterPipesAndTrimCellWhitespace(string source) => RunSta(() =>
    {
        var table = MarkdownRenderer.Render(source).Blocks.OfType<Table>().Single();
        var rows = table.RowGroups.Single().Rows;
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("A", CellText(rows[0].Cells[0]));
        Assert.AreEqual("B", CellText(rows[0].Cells[1]));
        Assert.AreEqual("x", CellText(rows[1].Cells[0]));
        Assert.AreEqual("y", CellText(rows[1].Cells[1]));
    });

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void TableAlignmentsWorkWithAllExistingLineEndings(string newline) => RunSta(() =>
    {
        var source = string.Join(newline, "| Left | Center | Right | Default |", "| :--- | :---: | ---: | --- |", "| a | b | c | d |");
        var table = MarkdownRenderer.Render(source).Blocks.OfType<Table>().Single();
        foreach (var row in table.RowGroups.Single().Rows)
        {
            var expected = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Left };
            for (var column = 0; column < expected.Length; column++)
                Assert.AreEqual(expected[column], ((Paragraph)row.Cells[column].Blocks.FirstBlock).TextAlignment);
        }
    });

    [TestMethod]
    public void EscapedPipesStayInCellsIncludingInlineCodeAndBold() => RunSta(() =>
    {
        const string source = "| A\\|B | Inline |\n| --- | --- |\n| one\\|two | `x\\|y` **a\\|b** |\n| slash\\\\| next |";
        var table = MarkdownRenderer.Render(source).Blocks.OfType<Table>().Single();
        var rows = table.RowGroups.Single().Rows;
        Assert.AreEqual("A|B", CellText(rows[0].Cells[0]));
        Assert.AreEqual("one|two", CellText(rows[1].Cells[0]));
        Assert.AreEqual("x|y a|b", CellText(rows[1].Cells[1]));
        var paragraph = (Paragraph)rows[1].Cells[1].Blocks.FirstBlock;
        Assert.IsTrue(paragraph.Inlines.OfType<Run>().Any(run => run.Text == "x|y" && run.FontFamily.Source == "Consolas"));
        Assert.IsTrue(paragraph.Inlines.OfType<Bold>().Any());
        Assert.AreEqual("slash\\\\", CellText(rows[2].Cells[0]));
        Assert.AreEqual("next", CellText(rows[2].Cells[1]));
    });

    [TestMethod]
    public void TableRowsPadMissingCellsAndIgnoreExtraCells() => RunSta(() =>
    {
        var table = MarkdownRenderer.Render("| A | B |\n| --- | --- |\n| first |\n| second | value | excess |\nplain row\n||").Blocks.OfType<Table>().Single();
        var rows = table.RowGroups.Single().Rows;
        Assert.AreEqual(5, rows.Count);
        foreach (var row in rows) Assert.AreEqual(2, row.Cells.Count);
        Assert.AreEqual("", CellText(rows[1].Cells[1]));
        Assert.AreEqual("value", CellText(rows[2].Cells[1]));
        Assert.AreEqual("plain row", CellText(rows[3].Cells[0]));
        Assert.AreEqual("", CellText(rows[4].Cells[0]));
        Assert.AreEqual("", CellText(rows[4].Cells[1]));
        Assert.IsFalse(new TextRange(table.ContentStart, table.ContentEnd).Text.Contains("excess", StringComparison.Ordinal));
    });

    [TestMethod]
    [DataRow("| Name |\n| --- |", 1)]
    [DataRow("Name\n| --- |", 1)]
    [DataRow("| A | B |\n| --- | --- |", 2)]
    public void HeaderOnlyTablesAndSingleColumnTablesAreSupported(string source, int columns) => RunSta(() =>
    {
        var table = MarkdownRenderer.Render(source).Blocks.OfType<Table>().Single();
        Assert.AreEqual(columns, table.Columns.Count);
        Assert.AreEqual(1, table.RowGroups.Single().Rows.Count);
    });

    [TestMethod]
    [DataRow("ordinary | pipe separated text\nmore text")]
    [DataRow("| A | B |\n| --- |\n| x | y |")]
    [DataRow("| A | B |\n| --- | : |\n| x | y |")]
    [DataRow("| A | B |\n| --x | --- |\n| x | y |")]
    [DataRow("| A | B |\n\n| --- | --- |")]
    public void NonTablesAndMalformedSeparatorsRemainText(string source) => RunSta(() =>
    {
        var document = MarkdownRenderer.Render(source);
        Assert.IsFalse(document.Blocks.OfType<Table>().Any());
        StringAssert.Contains(Text(document), source.Split('\n')[0]);
    });

    [TestMethod]
    public void TableDetectionRespectsParagraphAndBlockBoundaries() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("intro\n| A | B |\n| --- | --- |\n| x | y |\n# Heading\n\n| C | D |\n| --- | --- |\n> quote\n\n```\n| literal | code |\n| --- | --- |\n```");
        var blocks = document.Blocks.ToArray();
        Assert.AreEqual(6, blocks.Length);
        Assert.IsInstanceOfType<Paragraph>(blocks[0]);
        Assert.IsInstanceOfType<Table>(blocks[1]);
        Assert.AreEqual(28d, blocks[2].FontSize);
        Assert.IsInstanceOfType<Table>(blocks[3]);
        StringAssert.Contains(new TextRange(blocks[4].ContentStart, blocks[4].ContentEnd).Text, "quote");
        StringAssert.Contains(new TextRange(blocks[5].ContentStart, blocks[5].ContentEnd).Text, "| literal | code |");
    });

    [TestMethod]
    public void TableCellsKeepLinksHtmlAndImagesInert() => RunSta(() =>
    {
        var table = MarkdownRenderer.Render("| Content |\n| --- |\n| <script>alert(1)</script> [local](file:///secret) ![image](file:///secret.png) |").Blocks.OfType<Table>().Single();
        var paragraph = (Paragraph)table.RowGroups.Single().Rows[1].Cells[0].Blocks.FirstBlock;
        Assert.IsFalse(paragraph.Inlines.Any(inline => inline is Hyperlink or InlineUIContainer));
        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        StringAssert.Contains(text, "<script>alert(1)</script>");
        StringAssert.Contains(text, "local (file:///secret)");
        StringAssert.Contains(text, "file:///secret.png");
    });

    [TestMethod]
    public void TableBordersHeaderAndStripingFollowThemeChanges() => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("| A | B |\n| --- | --- |\n| `code` | first |\n| next | second |");
        var table = document.Blocks.OfType<Table>().Single();
        var rows = table.RowGroups.Single().Rows;
        using var manager = new AppThemeManager(document.Resources, AppThemeMode.Light, "#15FFFF");
        foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Midnight, AppThemeMode.OLED, AppThemeMode.Warm })
        {
            manager.Apply(theme, "#15FFFF");
            Assert.AreSame(document.Resources["NotedBorderBrush"], table.BorderBrush);
            Assert.AreSame(document.Resources["NotedSubtleSurfaceBrush"], rows[0].Background);
            Assert.AreSame(document.Resources["NotedInputBackgroundBrush"], rows[2].Background);
            foreach (var row in rows)
            foreach (var cell in row.Cells)
            {
                Assert.AreSame(document.Resources["NotedBorderBrush"], cell.BorderBrush);
                Assert.AreSame(document.Resources["NotedTextBrush"], cell.Foreground);
            }
        }
    });

    [TestMethod]
    [DataRow(320d)]
    [DataRow(900d)]
    public void TablesLayOutAtNarrowAndWidePreviewWidths(double width) => RunSta(() =>
    {
        var document = MarkdownRenderer.Render("| Repository | Runtime | Responsibility | Deployment |\n| --- | --- | --- | --- |\n| `TeeTimeClient` | React 18 and Vite | Login and booking request UI | Azure Static Web Apps |");
        var viewer = new FlowDocumentScrollViewer { Document = document, IsToolBarVisible = false };
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Markdown table layout")
        {
            Width = (int)width, Height = 400, WindowStyle = unchecked((int)0x80000000)
        });
        host.RootVisual = viewer;
        viewer.Measure(new Size(width, 400));
        viewer.Arrange(new Rect(0, 0, width, 400));
        viewer.UpdateLayout();
        var table = document.Blocks.OfType<Table>().Single();
        foreach (var column in table.Columns) Assert.IsTrue(column.Width.IsAuto);
        var lastCell = table.RowGroups.Single().Rows[1].Cells[3];
        var bounds = lastCell.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
        Assert.IsFalse(bounds.IsEmpty);
        Assert.IsTrue(double.IsFinite(bounds.X) && bounds.Right <= width);
    });

    [TestMethod]
    public void HtmlLinksAndImagesRemainInertText() => RunSta(() =>
    {
        const string source = "<script>alert(1)</script> <iframe src='file:///secret'/>\n[unsafe](javascript:alert) [local](file:///secret) [web](https://example.com) ![image](file:///secret.png)";
        var document = MarkdownRenderer.Render(source);
        var paragraphs = document.Blocks.Cast<Paragraph>().ToArray();
        Assert.IsFalse(paragraphs.SelectMany(p => p.Inlines).Any(i => i is Hyperlink or InlineUIContainer));
        var text = Text(document);
        Assert.IsTrue(text.Contains("<script>alert(1)</script>", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("local (file:///secret)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("web (https://example.com)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("file:///secret.png", StringComparison.Ordinal));
    });

    [TestMethod]
    public void SourceUpdatesRefreshPreviewAndModesPreserveEditingState() => RunSta(() =>
    {
        var editor = new TextBox { Text = "# Before", AcceptsReturn = true };
        using var surface = new Noted.Tests.Services.WpfEditorTestSurface(editor);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = new FlowDocumentScrollViewer();
        using var presenter = new MarkdownPreviewPresenter(editor, editor, preview, layout);
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " edited";
        editor.Select(2, 4);
        var text = editor.Text;
        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        Assert.IsTrue(editor.CanUndo);
        foreach (var mode in new[] { MarkdownPreviewMode.Preview, MarkdownPreviewMode.SideBySide, MarkdownPreviewMode.Off })
        {
            presenter.SetMode(mode);
            Assert.AreEqual(text, editor.Text);
            Assert.AreEqual(start, editor.SelectionStart);
            Assert.AreEqual(length, editor.SelectionLength);
            Assert.IsTrue(editor.CanUndo);
        }
        presenter.SetMode(MarkdownPreviewMode.SideBySide);
        var oldDocument = preview.Document;
        editor.SelectAll();
        editor.SelectedText = "# After";
        editor.SelectedText = "# Latest";
        Assert.AreSame(oldDocument, preview.Document, "Typing should defer rendering.");
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
        timeout.Start();
        Dispatcher.PushFrame(frame);
        Assert.IsTrue(Text(preview.Document).Contains("Latest", StringComparison.Ordinal));
        presenter.SetMode(MarkdownPreviewMode.Off);
        var hiddenDocument = preview.Document;
        editor.Text = "hidden update";
        presenter.Refresh();
        Assert.AreSame(hiddenDocument, preview.Document);
        presenter.SetMode(MarkdownPreviewMode.Preview);
        Assert.IsTrue(Text(preview.Document).Contains("hidden update", StringComparison.Ordinal));
    });

    [TestMethod]
    [DataRow(50d, 100d, 0.5d)]
    [DataRow(200d, 100d, 1d)]
    [DataRow(-10d, 100d, 0d)]
    [DataRow(5d, 0d, 0d)]
    [DataRow(double.NaN, 100d, 0d)]
    public void ScrollProgressIsNormalizedAndBounded(double offset, double height, double expected) =>
        Assert.AreEqual(expected, MarkdownPreviewPresenter.ScrollProgress(offset, height));

    [TestMethod]
    [DataRow("note.md", true)]
    [DataRow("note.MARKDOWN", true)]
    [DataRow("note.txt", false)]
    [DataRow("note.md.txt", false)]
    public void PreviewModesAreContextualAndDoNotDirtyDocuments(string name, bool markdown)
    {
        var document = new OpenNoteDocument(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name), "source", "source", false);
        var workspace = new NoteEditorWorkspace();
        workspace.Add(document);
        foreach (var mode in Enum.GetValues<MarkdownPreviewMode>())
        {
            workspace.SetMarkdownPreviewMode(document, mode);
            Assert.AreEqual(markdown ? mode : MarkdownPreviewMode.Off, document.MarkdownPreviewMode);
            Assert.AreEqual("source", document.Content);
            Assert.IsFalse(document.IsDirty);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Plain text\nsecond line")]
    [DataRow("unfinished **bold and [link](\n\n```\nopen code fence")]
    public void EmptyPlainTextAndMalformedMarkdownRemainReadable(string source) => RunSta(() =>
    {
        var document = MarkdownRenderer.Render(source);
        Assert.IsTrue(document.Blocks.Count > 0);
        var text = Text(document);
        if (source.Length == 0)
            Assert.AreEqual("", text.Trim());
        else if (source.StartsWith("Plain", StringComparison.Ordinal))
            Assert.AreEqual(source.Replace("\n", Environment.NewLine), text.TrimEnd('\r', '\n'));
        else
        {
            StringAssert.Contains(text, "unfinished **bold and [link](");
            StringAssert.Contains(text, "open code fence");
        }
    });

    [TestMethod]
    public void DictionaryReadSurfaceDefersHiddenRenderingAndPreservesSourceUndo() => RunSta(() =>
    {
        var item = new DictionaryItem { Word = "word", Definition = "# Saved" };
        item.BeginEdit();
        var editor = new TextBox { AcceptsReturn = true, DataContext = item };
        editor.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(DictionaryItem.DraftDefinition))
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });
        var preview = new MarkdownView { Visibility = Visibility.Collapsed, DataContext = item };
        preview.SetBinding(MarkdownView.SourceProperty,
            new System.Windows.Data.Binding(nameof(DictionaryItem.Definition)));
        using var surface = new Noted.Tests.Services.WpfEditorTestSurface(editor, preview);
        var hiddenDocument = preview.Document;
        editor.SelectAll();
        editor.SelectedText = "**raw draft**";
        Assert.IsTrue(editor.CanUndo);
        Assert.AreEqual("**raw draft**", item.DraftDefinition);
        Assert.AreEqual("# Saved", preview.Source);
        Assert.AreSame(hiddenDocument, preview.Document);

        item.SaveEdit();
        Assert.AreEqual("**raw draft**", preview.Source);
        Assert.AreSame(hiddenDocument, preview.Document, "Hidden read mode must defer rendering.");
        preview.Visibility = Visibility.Visible;
        Assert.IsTrue(((Paragraph)preview.Document.Blocks.First()).Inlines.Any(inline => inline is Bold));
        Assert.AreEqual("**raw draft**", editor.Text);
        Assert.IsTrue(editor.CanUndo);

        preview.Visibility = Visibility.Collapsed;
        item.BeginEdit();
        editor.SelectAll();
        editor.SelectedText = "cancel this";
        item.CancelEdit();
        Assert.AreEqual("**raw draft**", editor.Text);
        preview.Visibility = Visibility.Visible;
        StringAssert.Contains(Text(preview.Document), "raw draft");
    });

    private static string CellText(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd('\r', '\n');

    private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Preview test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
