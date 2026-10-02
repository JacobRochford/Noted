using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class IndentationPersistenceTests
{
    [TestMethod]
    public void GlobalDefaultsAndWindowOverridesPersistIndependently()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        Assert.AreEqual(new IndentationSettings(), settings.LoadDefaultIndentation());
        var local = new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 4 };
        settings.SaveMiniPadWindowState(new() { Indentation = local });
        settings.SaveScratchpadWindowState(new() { Indentation = local });
        settings = new AppSettingsService(directory.Path);
        Assert.AreEqual(new IndentationSettings(), settings.LoadDefaultIndentation());
        var defaults = new IndentationSettings { TabSize = 8 };
        settings.SaveDefaultIndentation(defaults);
        settings = new AppSettingsService(directory.Path);
        Assert.AreEqual(defaults, settings.LoadDefaultIndentation());
        Assert.AreEqual(local, settings.LoadMiniPadWindowState().Indentation);
        Assert.AreEqual(local, settings.LoadScratchpadWindowState().Indentation);
    }

    [TestMethod]
    public void ScratchpadSeedsDefaultsAndRetainsItsOverrideAcrossLayoutChangesAndRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var defaults = new IndentationSettings { TabSize = 8 };
        settings.SaveDefaultIndentation(defaults);
        var model = new ScratchpadWindowViewModel(settings, new ScratchpadContentService(directory.Path));
        Assert.AreEqual(defaults, model.Indentation);
        var local = defaults with { Mode = IndentationMode.Tabs };
        model.SetIndentation(local);
        Assert.IsTrue(model.TrySaveWindowLayout(10, 20, 500, 500));
        settings.SaveDefaultIndentation(new());
        var restored = new ScratchpadWindowViewModel(new AppSettingsService(directory.Path), new ScratchpadContentService(directory.Path));
        Assert.AreEqual(local, restored.Indentation);
        Assert.AreEqual(new IndentationSettings(), settings.LoadDefaultIndentation());
    }

    [TestMethod]
    public void SessionRestoresPerDocumentSettingsAndOnlySeedsLegacyEntriesFromDefaults()
    {
        using var directory = new TemporaryTestDirectory();
        var first = directory.File("first.txt");
        var second = directory.File("second.txt");
        File.WriteAllText(first, "\tfirst");
        File.WriteAllText(second, "  second");
        var local = new IndentationSettings { Mode = IndentationMode.Tabs };
        var defaults = new IndentationSettings { TabSize = 8 };
        var sessions = new NoteEditorSessionService(directory.Path);
        sessions.Save(new()
        {
            Tabs = [new() { FilePath = first, Indentation = local }, new() { FilePath = second }],
            ActiveFilePath = first,
            SecondaryFilePath = second
        });
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            new NoteRecoveryService(directory.Path), sessions, () => defaults);
        var loaded = persistence.LoadWorkspace(true);
        Assert.AreEqual(local, loaded.Documents[0].Indentation);
        Assert.AreEqual(defaults, loaded.Documents[1].Indentation);
        var workspace = new NoteEditorWorkspace();
        foreach (var document in loaded.Documents) workspace.Add(document);
        sessions.Save(NoteEditorPersistence.CreateSession(workspace));

        // An indentation-only change must not be skipped by session equality.
        loaded.Documents[0].Indentation = local with { TabSize = 2 };
        sessions.Save(NoteEditorPersistence.CreateSession(workspace));
        var restored = new NoteEditorSessionService(directory.Path).Load().Session;
        Assert.AreEqual(2, restored.Tabs[0].Indentation!.TabSize);
        Assert.AreEqual(defaults, restored.Tabs[1].Indentation);
        Assert.AreEqual("\tfirst", loaded.Documents[0].Content);
        Assert.IsFalse(loaded.Documents[0].IsDirty);
        Assert.AreEqual("\tfirst", File.ReadAllText(first));
    }
}
