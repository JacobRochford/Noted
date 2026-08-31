using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class JsonCollectionFileStoreTests
{
    [TestMethod]
    public void ReplacementIsVerifiedAndPreservesPreviousItems()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("dictionary.json");
        var store = new JsonCollectionFileStore<DictionaryItemState>(path, "Dictionary content");
        store.Write([new DictionaryItemState { Word = "first" }]);

        var warning = store.Write([new DictionaryItemState { Word = "second" }]);
        var loaded = new JsonCollectionFileStore<DictionaryItemState>(
            path,
            "Dictionary content").Load();
        var backup = JsonFileStore.Read<List<DictionaryItemState>>($"{path}.bak");

        Assert.IsNull(warning);
        Assert.AreEqual("second", loaded.Items?.Single().Word);
        Assert.AreEqual("first", backup.Value?.Single().Word);
    }
}
