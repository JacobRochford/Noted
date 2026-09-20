using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteBrowserNavigationTests
{
    [TestMethod]
    public void NavigationMaintainsBackAndForwardHistory()
    {
        using var directory = new TemporaryTestDirectory();
        var root = directory.File("notes");
        Directory.CreateDirectory(Path.Combine(root, "first", "second"));
        var navigation = new NoteBrowserNavigation(root);

        navigation.NavigateTo("first");
        navigation.NavigateTo("second");

        Assert.AreEqual(Path.Combine(root, "first", "second"), navigation.CurrentDirectory);
        Assert.AreEqual("first / second", navigation.CurrentFolderName);
        Assert.IsTrue(navigation.CanNavigateBack);
        navigation.NavigateBack();
        Assert.AreEqual(Path.Combine(root, "first"), navigation.CurrentDirectory);
        Assert.IsTrue(navigation.CanNavigateForward);
        navigation.NavigateForward();
        Assert.AreEqual(Path.Combine(root, "first", "second"), navigation.CurrentDirectory);
    }

    [TestMethod]
    public void NavigationRejectsArchiveAndPathsOutsideTheRoot()
    {
        using var directory = new TemporaryTestDirectory();
        var root = directory.File("notes");
        Directory.CreateDirectory(Path.Combine(root, ".archive"));
        Directory.CreateDirectory(directory.File("external"));
        var navigation = new NoteBrowserNavigation(root);

        navigation.NavigateTo(".archive");
        Assert.AreEqual(root, navigation.CurrentDirectory);

        navigation.NavigateTo(Path.Combine("..", "external"));
        Assert.AreEqual(root, navigation.CurrentDirectory);
        Assert.IsFalse(navigation.CanNavigateBack);
    }

    [TestMethod]
    public void ChangingRootResetsTheCurrentDirectoryAndHistory()
    {
        using var directory = new TemporaryTestDirectory();
        var firstRoot = directory.File("first-root");
        var secondRoot = directory.File("second-root");
        Directory.CreateDirectory(Path.Combine(firstRoot, "child"));
        Directory.CreateDirectory(secondRoot);
        var navigation = new NoteBrowserNavigation(firstRoot);
        navigation.NavigateTo("child");

        navigation.ChangeRoot(secondRoot);

        Assert.AreEqual(secondRoot, navigation.RootDirectory);
        Assert.AreEqual(secondRoot, navigation.CurrentDirectory);
        Assert.IsFalse(navigation.CanNavigateBack);
        Assert.IsFalse(navigation.CanNavigateForward);
        Assert.IsFalse(navigation.CanNavigateUp);
    }
}
