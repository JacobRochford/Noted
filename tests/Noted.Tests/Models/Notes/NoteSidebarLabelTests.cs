using Noted.Helpers;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class NoteSidebarLabelTests
{
    [TestMethod]
    public void GeneratedDraftLabelChangesWithoutChangingIdentityOrTitle()
    {
        var workspace = new NoteEditorWorkspace();
        var document = new OpenNoteDocument(Path.GetFullPath("2026-11-14_12-00-00.txt"), "", "", false,
            usesGeneratedName: true, initialFileContent: "");
        workspace.Add(document);
        var path = document.FilePath;
        var title = document.TabLabel;
        var identity = document.DocumentId;
        var notifications = new List<string?>();
        document.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        workspace.UpdateContent(document, "\n  A useful label  \nmore content", 0);
        Assert.AreEqual("•  A useful label", document.SidebarLabel);
        Assert.AreEqual("•  " + title, document.TabLabel);
        Assert.AreEqual(path, document.FilePath);
        Assert.AreEqual(identity, document.DocumentId);
        workspace.UpdateContent(document, "B", 0);
        Assert.AreEqual("•  B", document.SidebarLabel);
        Assert.IsTrue(notifications.Count(name => name == nameof(document.SidebarLabel)) >= 2);
        workspace.MarkSaved(document);
        Assert.AreEqual(document.TabLabel, document.SidebarLabel);
    }

    [TestMethod]
    [DataRow("2026-11-14 12:00:00\r\n\r\nC", "C")]
    [DataRow("2026-11-14 12:00:01\nC", "2026-11-14 12:00:01")]
    [DataRow("2026-11-14 12:00:00 \nC", "2026-11-14 12:00:00")]
    [DataRow("heading\n2026-11-14 12:00:00", "heading")]
    [DataRow("2026-11-14 12:00:00\n2026-11-14 12:00:00\nC", "2026-11-14 12:00:00")]
    public void OnlyUnchangedInitialTimestampIsSkipped(string content, string expected) =>
        Assert.AreEqual(expected, NoteSidebarLabel.GetContentLabel(content, "2026-11-14 12:00:00\r\n\r\n"));

    [TestMethod]
    public void LabelExtractionIsBoundedAndDoesNotSkipOrdinaryInitialContent()
    {
        Assert.AreEqual(new string('a', 120), NoteSidebarLabel.GetContentLabel(new string('a', 10000), ""));
        Assert.AreEqual("", NoteSidebarLabel.GetContentLabel(new string('\n', 2048) + "hidden", ""));
        Assert.AreEqual("header", NoteSidebarLabel.GetContentLabel("header\nC", "header\n"));
        Assert.AreEqual("2026-11-14 12:00:00", NoteSidebarLabel.GetContentLabel("2026-11-14 12:00:00\nC", null));
    }

    [TestMethod]
    public void CustomNamedDraftAndRenamedGeneratedDraftKeepFileLabels()
    {
        var workspace = new NoteEditorWorkspace();
        var document = new OpenNoteDocument(Path.GetFullPath("named.txt"), "content", "", true,
            initialFileContent: "");
        workspace.Add(document);
        Assert.AreEqual("•  named", document.SidebarLabel);
        workspace.UpdateCreationMetadata(document, true, "");
        Assert.AreEqual("•  content", document.SidebarLabel);
        workspace.UpdatePath(document, Path.GetFullPath("renamed.txt"), true, true);
        Assert.AreEqual("•  renamed", document.SidebarLabel);
    }
}
