using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class ResizeEdgeHitTestTests
{
    [TestMethod]
    public void CornersExtendAlongBothEdgesWithoutCoveringTheInterior()
    {
        Assert.AreEqual(ResizeEdge.Left | ResizeEdge.Top, Hit(23, 5));
        Assert.AreEqual(ResizeEdge.Left | ResizeEdge.Top, Hit(5, 23));
        Assert.AreEqual(ResizeEdge.Right | ResizeEdge.Bottom, Hit(395, 277));
        Assert.AreEqual(ResizeEdge.Right | ResizeEdge.Bottom, Hit(377, 295));
        Assert.AreEqual(ResizeEdge.None, Hit(15, 15));
        Assert.AreEqual(ResizeEdge.Top, Hit(100, 9));
        Assert.AreEqual(ResizeEdge.None, Hit(-1, 5));
    }

    private static ResizeEdge Hit(double x, double y) =>
        ResizeEdgeHitTest.Find(new Point(x, y), 400, 300, ResizeEdgeHitTest.DefaultThickness);
}
