using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class JsonFileStoreTests
{
    [TestMethod]
    public void PreserveCorruptFileMovesOriginalBytesToUniqueFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("state.json");
        var originalBytes = "{broken"u8.ToArray();
        File.WriteAllBytes(path, originalBytes);

        var result = JsonFileStore.PreserveCorruptFile(path, JsonFileReadStatus.Corrupt);

        Assert.IsNotNull(result.PreservedPath);
        Assert.IsNull(result.Error);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(File.Exists(result.PreservedPath));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(result.PreservedPath));
    }

    [TestMethod]
    public void PreserveCorruptFileLeavesNonCorruptFileUntouched()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("state.json");
        var originalBytes = "{}"u8.ToArray();
        File.WriteAllBytes(path, originalBytes);

        var result = JsonFileStore.PreserveCorruptFile(path, JsonFileReadStatus.Success);

        Assert.IsNull(result.PreservedPath);
        Assert.IsNull(result.Error);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void PreserveCorruptFileDoesNothingWhenFileIsMissing()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("missing.json");

        var result = JsonFileStore.PreserveCorruptFile(path, JsonFileReadStatus.Corrupt);

        Assert.IsNull(result.PreservedPath);
        Assert.IsNull(result.Error);
        Assert.IsFalse(File.Exists(path));
    }
}
