using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class TextLineMapTests
{
    [TestMethod]
    public void LogicalLinesHandleMixedNewlinesAndTrailingEmptyLines()
    {
        var map = new TextLineMap("one\r\ntwo\nthree\rfour\r\n");
        CollectionAssert.AreEqual(new[] { 0, 5, 9, 15, 21 }, map.Starts);
        Assert.AreEqual(0, map.LineAt(3));
        Assert.AreEqual(1, map.LineAt(5));
        Assert.AreEqual(4, map.LineAt(map.Text.Length));
        Assert.AreEqual(4, map.LineAt(500));
        Assert.AreEqual(0, new TextLineMap("").LineAt(0));
    }
}
