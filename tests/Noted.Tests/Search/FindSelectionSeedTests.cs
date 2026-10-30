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
        Assert.AreEqual(shouldSeed ? selected : null,
            FindSelectionSeed.GetQuery(selected, isOpen, editorFocused, "previous", () => "caret"));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(200, true)]
    [DataRow(201, false)]
    public void Seeding_EnforcesLengthLimit(int length, bool valid)
    {
        var selected = new string('x', length);
        Assert.AreEqual(valid ? selected : null, FindSelectionSeed.GetQuery(selected, false, false, "previous", () => "caret"));
        Assert.AreEqual(valid ? selected : null, FindSelectionSeed.GetQuery(selected, true, true, "previous", () => "caret"));
    }

    [TestMethod]
    [DataRow("a\rb")]
    [DataRow("a\nb")]
    [DataRow("a\r\nb")]
    [DataRow(null)]
    public void Seeding_RejectsLineBreaksAndMissingSelection(string? selected)
    {
        Assert.IsNull(FindSelectionSeed.GetQuery(selected, false, false, "previous", () => "caret"));
        Assert.IsNull(FindSelectionSeed.GetQuery(selected, true, true, "previous", () => "caret"));
    }

    [TestMethod]
    [DataRow(false, false, "", "word")]
    [DataRow(false, true, "", "word")]
    [DataRow(true, true, "", "word")]
    [DataRow(true, false, "", null)]
    [DataRow(false, true, "previous", null)]
    [DataRow(true, true, "previous", null)]
    [DataRow(true, true, " ", null)]
    public void CaretWordOnlyPopulatesAnEmptyQueryFromTheEditor(
        bool isOpen, bool editorFocused, string query, string? expected)
    {
        var calls = 0;
        var result = FindSelectionSeed.GetQuery("", isOpen, editorFocused, query, () =>
        {
            calls++;
            return "word";
        });
        Assert.AreEqual(expected, result);
        Assert.AreEqual(expected is null ? 0 : 1, calls);
    }

    [TestMethod]
    public void InvalidSelectionDoesNotFallBackToTheCaretWord()
    {
        foreach (var selection in new[] { "a\nb", new string('x', 201) })
            Assert.IsNull(FindSelectionSeed.GetQuery(selection, false, true, "", () => "caret"));
    }

    [TestMethod]
    [DataRow("", 0, null)]
    [DataRow("alpha  beta", 0, "alpha")]
    [DataRow("alpha  beta", 2, "alpha")]
    [DataRow("alpha  beta", 5, "alpha")]
    [DataRow("alpha  beta", 6, null)]
    [DataRow("alpha  beta", 7, "beta")]
    [DataRow("alpha  beta", 11, "beta")]
    [DataRow("a.b", 2, "b")]
    [DataRow("...", 1, null)]
    [DataRow("one\r\ntwo", 4, null)]
    [DataRow("naïve_42", 3, "naïve_42")]
    [DataRow("e\u0301clair", 1, "e\u0301clair")]
    public void CaretWordUsesSearchWordBoundaries(string text, int caret, string? expected)
    {
        Assert.AreEqual(expected, FindSelectionSeed.GetWordAtCaret(text, caret));
    }

    [TestMethod]
    public void CaretWordKeepsTheExistingQueryLengthLimit()
    {
        var word = new string('x', 200);
        Assert.AreEqual(word, FindSelectionSeed.GetWordAtCaret(word, 100));
        Assert.IsNull(FindSelectionSeed.GetWordAtCaret(word + "x", 100));
    }
}
