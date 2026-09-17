using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class SearchViewportPolicyTests
{
    [TestMethod]
    public void VisibleResultInsideSafeViewport_DoesNotScroll()
    {
        var result = Resolve(offset: 20, viewport: 10, extent: 100, start: 24, end: 25, context: 1);

        Assert.IsFalse(result.RequiresScroll);
        Assert.AreEqual(20, result.Offset);
    }

    [TestMethod]
    public void ResultSlightlyAboveViewport_RevealsWithOneRowOfContext()
    {
        var result = Resolve(offset: 20, viewport: 10, extent: 100, start: 20, end: 21, context: 1);

        Assert.IsTrue(result.RequiresScroll);
        Assert.AreEqual(19, result.Offset);
    }

    [TestMethod]
    public void ResultSlightlyBelowViewport_RevealsWithOneRowOfContext()
    {
        var result = Resolve(offset: 20, viewport: 10, extent: 100, start: 29, end: 30, context: 1);

        Assert.IsTrue(result.RequiresScroll);
        Assert.AreEqual(21, result.Offset);
    }

    [TestMethod]
    public void FarJump_PlacesResultAtUpperThirdOfViewport()
    {
        var result = Resolve(offset: 0, viewport: 12, extent: 100, start: 76, end: 77, context: 1);

        Assert.IsTrue(result.RequiresScroll);
        Assert.AreEqual(72, result.Offset, 0.001);
    }

    [TestMethod]
    public void HorizontalReveal_UsesTheSameBoundedPolicy()
    {
        var result = Resolve(offset: 40, viewport: 200, extent: 900, start: 230, end: 260, context: 16);

        Assert.IsTrue(result.RequiresScroll);
        Assert.AreEqual(76, result.Offset, 0.001);
    }

    [TestMethod]
    public void RequestedOffset_IsClampedToExtent()
    {
        var result = Resolve(offset: 0, viewport: 10, extent: 25, start: 24, end: 25, context: 1);

        Assert.IsTrue(result.RequiresScroll);
        Assert.AreEqual(15, result.Offset);
    }

    private static SearchViewportAdjustment Resolve(
        double offset,
        double viewport,
        double extent,
        double start,
        double end,
        double context) =>
        SearchViewportPolicy.Resolve(new SearchViewportAxis(
            offset,
            viewport,
            extent,
            start,
            end,
            context));
}
