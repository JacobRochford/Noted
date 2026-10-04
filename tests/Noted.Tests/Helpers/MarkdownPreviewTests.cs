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
