using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class NoteNameFormatterTests
{
    [TestMethod]
    public void FormatMakesGeneratedNamesReadable()
    {
        var displayName = NoteNameFormatter.Format(
            "2026-08-31_19-45-08.txt",
            keepExtensionForCustomName: false);

        Assert.AreEqual("Aug 31, 2026 at 7:45:08 PM", displayName);
    }

    [TestMethod]
    public void FormatRecognizesOlderGeneratedNameVariants()
    {
        Assert.AreEqual(
            "Aug 31, 2026 at 7:45:08 PM",
            NoteNameFormatter.Format(
                "Note_2026-08-31_19-45-08_01.txt",
                keepExtensionForCustomName: false));
    }

    [TestMethod]
    public void FormatCanKeepOrHideTheExtensionForCustomNames()
    {
        Assert.AreEqual(
            "project ideas.txt",
            NoteNameFormatter.Format("project ideas.txt", keepExtensionForCustomName: true));
        Assert.AreEqual(
            "project ideas",
            NoteNameFormatter.Format("project ideas.txt", keepExtensionForCustomName: false));
    }

    [TestMethod]
    public void IsGeneratedDoesNotTreatOrdinaryNamesAsGenerated()
    {
        Assert.IsTrue(NoteNameFormatter.IsGenerated("2026-08-31_19-45-08.txt"));
        Assert.IsFalse(NoteNameFormatter.IsGenerated("meeting at 7-45.txt"));
    }
}
