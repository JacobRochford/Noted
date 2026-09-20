using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class NoteEditorWorkspaceTests
{
    [TestMethod]
    public void DocumentChangesFlowThroughWorkspaceOperations()
    {
        var workspace = new NoteEditorWorkspace();
        var document = CreateDocument("draft.txt", content: "saved");
        var documentId = document.DocumentId;
        workspace.Add(document);

        workspace.UpdateContent(document, "changed", caretIndex: 50);
        workspace.UpdateViewState(document, caretIndex: 3, verticalOffset: 12, markdownPreviewEnabled: true);
        workspace.UpdatePath(
            document,
            Path.Combine(TestRoot, "renamed.txt"),
            clearGeneratedName: true,
            markPresent: true);

        Assert.AreEqual("changed", document.Content);
        Assert.AreEqual(3, document.CaretIndex);
        Assert.AreEqual(12, document.VerticalOffset);
        Assert.IsTrue(document.MarkdownPreviewEnabled);
        Assert.IsTrue(document.IsDirty);
        Assert.AreEqual(documentId, document.DocumentId);

        workspace.MarkSaved(document);

        Assert.AreEqual("changed", document.SavedContent);
        Assert.IsFalse(document.IsDirty);
        Assert.IsFalse(document.IsMissing);
    }

    [TestMethod]
    public void ActiveAndSecondaryDocumentsRemainDistinctAndOwned()
    {
        var workspace = new NoteEditorWorkspace();
        var primary = CreateDocument("primary.txt");
        var secondary = CreateDocument("secondary.txt");
        workspace.Add(primary);
        workspace.Add(secondary);
        workspace.Activate(primary);

        workspace.ShowSecondary(secondary, focus: true);

        Assert.AreSame(primary, workspace.ActiveDocument);
        Assert.AreSame(secondary, workspace.SecondaryDocument);
        Assert.AreSame(secondary, workspace.CurrentDocument);
        Assert.ThrowsExactly<InvalidOperationException>(() => workspace.ShowSecondary(primary, focus: false));
        Assert.ThrowsExactly<InvalidOperationException>(() => workspace.Focus(CreateDocument("foreign.txt")));

        workspace.CloseSecondary();

        Assert.IsNull(workspace.SecondaryDocument);
        Assert.AreSame(primary, workspace.CurrentDocument);
    }

    [TestMethod]
    public void ClosingActiveDocumentSelectsTheNextDocumentAndRepairsPaneState()
    {
        var workspace = new NoteEditorWorkspace();
        var first = CreateDocument("first.txt");
        var second = CreateDocument("second.txt");
        workspace.Add(first);
        workspace.Add(second);
        workspace.Activate(first);
        workspace.ShowSecondary(second, focus: false);

        var result = workspace.Close(first);

        Assert.IsTrue(result.ActiveDocumentChanged);
        Assert.IsTrue(result.SecondaryDocumentClosed);
        Assert.AreSame(second, result.ActiveDocument);
        Assert.AreSame(second, workspace.ActiveDocument);
        Assert.IsNull(workspace.SecondaryDocument);
        Assert.AreSame(second, workspace.CurrentDocument);
    }

    [TestMethod]
    public void DuplicatePathsAreRejectedCaseInsensitively()
    {
        var workspace = new NoteEditorWorkspace();
        workspace.Add(CreateDocument("same.txt"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            workspace.Add(CreateDocument("SAME.TXT")));
    }

    [TestMethod]
    public void MutableDocumentPropertiesAreNotPubliclySettable()
    {
        var mutableProperties = new[]
        {
            nameof(OpenNoteDocument.Content),
            nameof(OpenNoteDocument.SavedContent),
            nameof(OpenNoteDocument.CaretIndex),
            nameof(OpenNoteDocument.VerticalOffset),
            nameof(OpenNoteDocument.MarkdownPreviewEnabled),
            nameof(OpenNoteDocument.UsesGeneratedName),
            nameof(OpenNoteDocument.InitialFileContent),
            nameof(OpenNoteDocument.IsDirty),
            nameof(OpenNoteDocument.IsMissing)
        };

        foreach (var propertyName in mutableProperties)
        {
            var property = typeof(OpenNoteDocument).GetProperty(propertyName);
            Assert.IsNotNull(property, propertyName);
            Assert.IsFalse(property.SetMethod?.IsPublic == true, propertyName);
        }

        Assert.IsNull(typeof(OpenNoteDocument).GetMethod(nameof(OpenNoteDocument.UpdateFilePath)));
    }

    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "NotedWorkspaceTests");

    private static OpenNoteDocument CreateDocument(string fileName, string content = "") =>
        new(
            Path.Combine(TestRoot, fileName),
            content,
            savedContent: content,
            isDirty: false);
}
