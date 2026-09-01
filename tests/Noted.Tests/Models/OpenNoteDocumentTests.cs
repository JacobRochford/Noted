using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class OpenNoteDocumentTests
{
    [TestMethod]
    public void DisplayNameFormatsGeneratedFileNames()
    {
        var document = new OpenNoteDocument(
            @"C:\Notes\2026-08-31_19-45-08.txt",
            content: string.Empty,
            savedContent: string.Empty,
            isDirty: false);

        Assert.AreEqual("Aug 31, 2026 at 7:45:08 PM", document.DisplayName);
    }

    [TestMethod]
    public void DisplayNameKeepsCustomNamesWithoutTheirExtension()
    {
        var document = new OpenNoteDocument(
            @"C:\Notes\project ideas.txt",
            content: string.Empty,
            savedContent: string.Empty,
            isDirty: false);

        Assert.AreEqual("project ideas", document.DisplayName);
    }
}
