using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class DictionaryAutosaveTests
{
    [TestMethod]
    public void DeactivationFlushesNewContentWithoutFinishingAnyEditors() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService();
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var existing = model.Items.Single();
            existing.BeginEdit();
            existing.DraftDefinition = "unsaved existing edit";
            var item = model.AddItem(beginEdit: true, autoSave: true);
            item.DraftWord = "new word";
            item.DraftDefinition = "new definition";
            var blank = model.AddItem(beginEdit: true, autoSave: true);
            var editor = new TextBox { DataContext = item, Text = item.DraftWord };
            editor.Select(2, 3);
            Invoke(window, "WordTextBox_LostFocus", editor, new RoutedEventArgs());
            Invoke(window, "OnDeactivated", EventArgs.Empty);

            Assert.AreEqual(1, service.SaveCount);
            Assert.AreEqual(2, service.Saved.Count);
            Assert.AreEqual("saved definition", service.Saved[0].Description);
            Assert.AreEqual("new word", service.Saved[1].Word);
            Assert.IsTrue(item.IsEditing && item.IsAutoSaving && item.IsExpanded);
            Assert.IsTrue(blank.IsEditing && model.Items.Contains(blank));
            Assert.IsTrue(existing.IsEditing && existing.HasPendingEdit);
            Assert.AreEqual("unsaved existing edit", existing.DraftDefinition);
            Assert.AreEqual(2, editor.SelectionStart);
            Assert.AreEqual(3, editor.SelectionLength);
            Invoke(window, "OnDeactivated", EventArgs.Empty);
            Assert.AreEqual(1, service.SaveCount, "A second flush must not duplicate a successful write.");
        }
        finally
        {
            foreach (var item in model.Items.Where(item => item.HasPendingEdit)) item.CancelEdit();
            window.Close();
        }
    });

    [TestMethod]
    public void OutsideClickFinishesNewEntriesRemovesBlankAndFlushes() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService();
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var item = model.AddItem(beginEdit: true, autoSave: true);
            item.DraftWord = "new word";
            var blank = model.AddItem(beginEdit: true, autoSave: true);
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = window
            };
            Invoke(window, "OnPreviewMouseDown", mouse);
            Assert.IsFalse(item.IsEditing);
            Assert.IsFalse(item.IsAutoSaving);
            Assert.IsFalse(model.Items.Contains(blank));
            Assert.AreEqual(1, service.SaveCount);
            Assert.AreEqual("new word", service.Saved.Last().Word);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void FailedDeactivationSaveKeepsEditorAndPendingContentForRetry() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService { FailSave = true };
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var item = model.AddItem(beginEdit: true, autoSave: true);
            item.DraftWord = "retain me";
            Invoke(window, "OnDeactivated", EventArgs.Empty);
            Assert.IsTrue(model.HasPersistenceError);
            Assert.IsTrue(item.IsEditing && item.IsAutoSaving);
            Assert.AreEqual("retain me", item.DraftWord);
            Assert.AreEqual(0, service.Saved.Count);
            service.FailSave = false;
            Invoke(window, "OnDeactivated", EventArgs.Empty);
            Assert.AreEqual("retain me", service.Saved.Last().Word);
            Assert.IsFalse(model.HasPersistenceError);
            Assert.IsTrue(item.IsEditing);
        }
        finally { service.FailSave = false; window.Close(); }
    });

    [TestMethod]
    public void SavingAnEditKeepsDefinitionExpandedAndOtherDraftsUnpublished() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService();
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var item = model.Items.Single();
            item.BeginEdit();
            item.DraftDefinition = "saved edit";
            var other = model.AddItem(beginEdit: true);
            other.DraftWord = "unsaved other";
            Invoke(window, "SaveEntry", item);
            Assert.IsFalse(item.IsEditing);
            Assert.IsTrue(item.IsExpanded);
            Assert.AreEqual("saved edit", service.Saved.Single().Description);
            Assert.IsTrue(other.IsEditing);
            model.RemoveItem(other);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void FailedEditSaveKeepsContentEditableAndRetrySavesIt() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService { FailSave = true };
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var item = model.Items.Single();
            item.BeginEdit();
            item.DraftDefinition = "retain edit";
            Invoke(window, "SaveEntry", item);
            Assert.IsTrue(item.IsEditing && item.IsExpanded);
            Assert.AreEqual("retain edit", item.DraftDefinition);
            Assert.AreEqual("saved definition", item.Definition);
            Assert.IsTrue(item.HasPendingEdit);
            Assert.IsTrue(model.HasPersistenceError);
            Assert.IsFalse(window.TryFlushPendingContent(out var pendingError));
            StringAssert.Contains(pendingError!, "Save or cancel");
            service.FailSave = false;
            Invoke(window, "SaveEntry", item);
            Assert.IsFalse(item.IsEditing);
            Assert.IsFalse(model.HasPersistenceError);
            Assert.AreEqual("retain edit", service.Saved.Single().Description);
        }
        finally { service.FailSave = false; window.Close(); }
    });

    [TestMethod]
    [DataRow("deactivate")]
    [DataRow("hide")]
    [DataRow("close")]
    [DataRow("shutdown")]
    public void LifecycleAfterFailedSaveAndCancelFlushesOnlyAcceptedContent(string action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService { FailSave = true };
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var item = model.Items.Single();
            item.BeginEdit();
            item.DraftWord = "canceled word";
            item.DraftDefinition = "canceled definition";
            Invoke(window, "SaveEntry", item);
            Assert.IsTrue(item.HasPendingEdit);
            Invoke(window, "CancelEntry_Click", new Button { DataContext = item }, new RoutedEventArgs());
            Assert.IsFalse(item.IsEditing);
            Assert.IsTrue(model.HasPersistenceError);
            service.FailSave = false;
            var added = model.AddItem(beginEdit: true, autoSave: true);
            added.DraftWord = "authorized new entry";

            if (action == "deactivate")
                Invoke(window, "OnDeactivated", EventArgs.Empty);
            else if (action == "hide")
                Invoke(window, "RequestHide");
            else if (action == "close")
                window.Close();
            else
            {
                Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
                window.PrepareForApplicationShutdown();
            }

            Assert.HasCount(2, service.Saved);
            Assert.AreEqual("existing", service.Saved[0].Word);
            Assert.AreEqual("saved definition", service.Saved[0].Description);
            Assert.AreEqual("authorized new entry", service.Saved[1].Word);
        }
        finally
        {
            service.FailSave = false;
            foreach (var item in model.Items.Where(item => item.HasPendingEdit)) item.CancelEdit();
            window.Close();
        }
    });

    [TestMethod]
    public void AddEntryFinishesAndFlushesInitialEntriesWithoutPublishingExistingDrafts() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService();
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            var existing = model.Items.Single();
            existing.BeginEdit();
            existing.DraftDefinition = "unsaved existing edit";
            var previous = model.AddItem(beginEdit: true, autoSave: true);
            previous.DraftWord = "previous new word";
            var blank = model.AddItem(beginEdit: true, autoSave: true);
            Invoke(window, "AddEntry");
            Assert.IsFalse(previous.IsEditing);
            Assert.IsFalse(model.Items.Contains(blank));
            Assert.AreEqual(1, service.SaveCount);
            Assert.AreEqual("previous new word", service.Saved.Last().Word);
            Assert.AreEqual("saved definition", service.Saved.First().Description);
            Assert.IsTrue(existing.IsEditing && existing.HasPendingEdit);
            var added = model.Items.Last();
            Assert.AreNotSame(previous, added);
            Assert.IsTrue(added.IsEditing && added.IsAutoSaving && added.IsExpanded);
            Assert.AreEqual("", added.DraftWord);
        }
        finally
        {
            foreach (var item in model.Items.Where(item => item.HasPendingEdit)) item.CancelEdit();
            window.Close();
        }
    });

    private static void Invoke(object target, string name, params object[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private sealed class RecordingContentService : IDictionaryContentService
    {
        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public IReadOnlyList<DictionaryItemState> Saved { get; private set; } = [];

        public DictionaryContentLoadResult LoadItems() => new(
            [new DictionaryItemState { Word = "existing", Description = "saved definition" }], []);

        public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
        {
            SaveCount++;
            if (FailSave) throw new IOException("Simulated save failure.");
            Saved = items.ToList();
            return new DictionaryContentSaveResult(null);
        }
    }
}
