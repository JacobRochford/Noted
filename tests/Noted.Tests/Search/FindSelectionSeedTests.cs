using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class FindSelectionSeedTests
{
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    public void Seeding_RespectsFindVisibilityAndEditorFocus(bool isOpen, bool editorFocused, bool shouldSeed)
    {
        const string selected = "  Mixed Case\t ";
        Assert.AreEqual(shouldSeed ? selected : null, FindSelectionSeed.GetQuery(selected, isOpen, editorFocused));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(200, true)]
    [DataRow(201, false)]
    public void Seeding_EnforcesLengthLimit(int length, bool valid)
    {
        var selected = new string('x', length);
        Assert.AreEqual(valid ? selected : null, FindSelectionSeed.GetQuery(selected, false, false));
        Assert.AreEqual(valid ? selected : null, FindSelectionSeed.GetQuery(selected, true, true));
    }

    [TestMethod]
    [DataRow("a\rb")]
    [DataRow("a\nb")]
    [DataRow("a\r\nb")]
    [DataRow(null)]
    public void Seeding_RejectsLineBreaksAndMissingSelection(string? selected)
    {
        Assert.IsNull(FindSelectionSeed.GetQuery(selected, false, false));
        Assert.IsNull(FindSelectionSeed.GetQuery(selected, true, true));
    }
}
