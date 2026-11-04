using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistKeyboardTests
{
    [TestMethod]
    public void CtrlNWorksWithoutSelectionAndTabReturnsToThePreviousTitleSelection() => WithWindow((window, model, service) =>
    {
        var list = (ListBox)window.FindName("ItemsList");
        list.SelectedItem = null;
        list.Focus();
        Press(list, Key.N, ModifierKeys.Control);
        Pump();
        var item = model.Items.Last();
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        Assert.AreSame(title, Keyboard.FocusedElement);
        title.Text = "new title";
        title.Select(2, 3);
        Press(title, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        Assert.IsTrue(item.IsExpanded);
        Assert.AreSame(notes, Keyboard.FocusedElement);
        notes.Text = "line one";
        notes.CaretIndex = notes.Text.Length;
        Press(notes, Key.Enter);
        Assert.HasCount(3, model.Items);
        Assert.IsTrue(notes.AcceptsReturn);
        StringAssert.Contains(notes.Text, Environment.NewLine);
        notes.SelectedText = "line two";
        Press(notes, Key.Tab, ModifierKeys.Shift);
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.AreEqual(2, title.SelectionStart);
        Assert.AreEqual(3, title.SelectionLength);
        Press(title, Key.Enter, ModifierKeys.Control);
        Assert.HasCount(3, model.Items);
        Assert.IsFalse(item.IsExpanded);
        Assert.AreEqual(item.Notes, service.Load().Last().Notes);
        Assert.IsTrue(list.IsKeyboardFocusWithin);
        Assert.IsTrue(Keyboard.FocusedElement is ListBoxItem);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtrlEnterPreservesEmptyPlaceholdersFromEitherField(bool notesField) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        if (notesField) Press(title, Key.Tab);
        Press((UIElement)Keyboard.FocusedElement, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsFalse(item.IsExpanded);
        Assert.HasCount(3, service.Load());
        EditTitle(window, item);
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.IsTrue(model.Items.Contains(item), "An established blank placeholder must survive Escape.");
    });

    [TestMethod]
    public void RepeatedEnterAndCtrlNKeepPreviousBlankPlaceholders() => WithWindow((window, model, service) =>
    {
        Create(window, model);
        for (var index = 0; index < 2; index++)
        {
            Press((UIElement)Keyboard.FocusedElement, Key.Enter);
            Pump();
        }
        Press((UIElement)Keyboard.FocusedElement, Key.N, ModifierKeys.Control);
        Pump();
        Assert.HasCount(6, model.Items);
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.HasCount(5, model.Items);
        Assert.HasCount(3, service.Load().Where(item => string.IsNullOrWhiteSpace(item.Text)).ToArray());
    });

    [TestMethod]
    [DataRow("all")]
    [DataRow("urgent")]
    [DataRow("custom")]
    [DataRow("done")]
    public void EmptyCreationCleanupUsesTheExistingTabDefaults(string tab) => WithWindow((window, model, service) =>
    {
        if (tab == "custom")
        {
            Assert.IsTrue(model.TryCreateTab("Work", out var created));
            model.SelectTab(created);
        }
        else if (tab != "all")
            model.SelectTab(model.FindTab(tab == "urgent" ? ChecklistTab.UrgentId : ChecklistTab.DoneId));
        var selected = model.SelectedTab;
        var item = Create(window, model);
        Assert.AreEqual(tab == "urgent" ? ChecklistPriority.High : ChecklistPriority.None, item.Priority);
        if (tab == "custom") Assert.AreEqual(selected!.Id, item.TabId);
        if (tab == "done") Assert.AreNotEqual(ChecklistTabKind.Done, model.SelectedTab!.Kind);
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.IsFalse(model.Items.Contains(item));
        Assert.HasCount(2, service.Load());
    });

    [TestMethod]
    [DataRow("title")]
    [DataRow("notes")]
    [DataRow("due")]
    [DataRow("priority")]
    [DataRow("checked")]
    [DataRow("tab")]
    public void EscapePreservesPopulatedAndMetadataOnlyCreations(string content) => WithWindow((window, model, service) =>
    {
        ChecklistTab? destinationTab = null;
        if (content == "tab")
        {
            Assert.IsTrue(model.TryCreateTab("Work", out destinationTab));
            model.SelectTab(model.FindTab("all"));
        }
        var item = Create(window, model);
        switch (content)
        {
            case "title": Control<TextBox>(window, item, "ItemTitleTextBox").Text = "keep"; break;
            case "notes": item.Notes = "keep notes"; break;
            case "due": item.DueDate = new DateTime(2026, 11, 3); break;
            case "priority": item.Priority = ChecklistPriority.High; break;
            case "checked": item.IsChecked = true; break;
            case "tab":
                model.MoveItemToTab(item, destinationTab!.Id);
                break;
        }
        if (model.FilteredItems.Contains(item))
        {
            item.IsExpanded = true;
            window.UpdateLayout();
            var notes = Control<TextBox>(window, item, "NotesTextBox");
            notes.Focus();
            Press(notes, Key.Escape);
        }
        else
            Assert.IsTrue(window.TryHide());
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsFalse(item.IsExpanded);
        var saved = service.Load().Last();
        Assert.AreEqual(item.Text, saved.Text);
        Assert.AreEqual(item.Notes, saved.Notes);
        Assert.AreEqual(item.DueDate, saved.DueDate);
        Assert.AreEqual(item.Priority, saved.Priority);
        Assert.AreEqual(item.TabId, saved.TabId);
        Assert.AreEqual(item.IsChecked, saved.IsChecked);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TitleToNotesNavigationKeepsRowsStableUntilTheItemIsExited(bool search) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        if (search) model.SearchQuery = "alpha";
        else model.SortMode = ChecklistSortMode.Alphabetical;
        var title = EditTitle(window, item);
        title.Text = "zulu";
        Press(title, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        Assert.IsTrue(notes.IsKeyboardFocused);
        notes.Text = "saved notes";
        Assert.AreSame(item, model.FilteredItems[0]);
        Press(notes, Key.Enter, ModifierKeys.Control);
        if (search) Assert.HasCount(0, model.FilteredItems);
        else Assert.AreEqual("bravo", model.FilteredItems[0].Text);
        Assert.AreEqual("saved notes", service.Load()[0].Notes);
    });

    [TestMethod]
    public void TabTraversesDateAndChevronWithoutActivatingThem() => WithWindow((window, model, _) =>
    {
        var item = Create(window, model);
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        Press(title, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        var details = Control<Button>(window, item, "ItemDetailsButton");
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var clicks = 0;
        details.Click += (_, _) => clicks++;
        Press(notes, Key.Tab);
        Assert.IsTrue(picker.IsKeyboardFocusWithin);
        for (var index = 0; index < 6 && !details.IsKeyboardFocused; index++)
            Press((UIElement)Keyboard.FocusedElement, Key.Tab, ModifierKeys.Shift);
        Assert.IsTrue(details.IsKeyboardFocused);
        Assert.AreEqual(0, clicks);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsChecked);
        Assert.IsNull(item.DueDate);
        Press(details, Key.Enter);
        Assert.AreEqual(1, clicks);
        Assert.IsFalse(item.IsExpanded);
    });

    [TestMethod]
    public void TabbingOutOfTheListFinishesOnlyTheOriginatingCreation() => WithWindow((window, model, service) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        var item = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var dateEditor = VisualTreeHelpers.FindDescendant<TextBox>(picker)!;
        dateEditor.Focus();
        Press(dateEditor, Key.Tab);
        Assert.IsFalse(model.Items.Contains(item));
        Assert.IsFalse(((ListBox)window.FindName("ItemsList")).IsKeyboardFocusWithin);
        Press((UIElement)Keyboard.FocusedElement, Key.Enter, ModifierKeys.Control);
        Assert.HasCount(2, model.Items);
        Assert.AreEqual("alpha", service.Load()[0].Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutsideClickCompletesCreationButClickingWithinTheRowDoesNot(bool populated) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        if (populated) item.Notes = "keep";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        PreviewClick(notes);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        PreviewClick((ListBox)window.FindName("ItemsList"));
        Pump();
        Assert.AreEqual(populated, model.Items.Contains(item));
        Assert.IsFalse(item.IsExpanded);
        Assert.HasCount(populated ? 3 : 2, service.Load());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndividualHideRoutesCompleteCreationAndReopenWithoutEditingFocus(bool shortcut) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        item.Notes = "survives hide";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        if (shortcut)
        {
            WindowManager.Checklist = window;
            WindowManager.HideChecklist();
        }
        else
        {
            var hide = (Button)window.FindName("HideChecklistButton");
            PreviewClick(hide);
            Assert.IsTrue(item.IsExpanded, "The X preview must defer completion to the hide guard.");
            hide.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Assert.IsFalse(window.IsVisible);
        Assert.IsFalse(item.IsExpanded);
        Assert.AreEqual("survives hide", service.Load().Last().Notes);
        window.Show();
        Pump();
        Assert.IsFalse(Keyboard.FocusedElement is TextBox);
        var title = EditTitle(window, item);
        title.Clear();
        item.Notes = "";
        Press(title, Key.Escape);
        Assert.IsTrue(model.Items.Contains(item));
    });

    [TestMethod]
    public void OutsideClickReachesTheIntendedItemBeforeSortingRefreshesRows() => WithWindow((window, model, _) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        var edited = model.Items[0];
        var clicked = model.Items[1];
        var title = EditTitle(window, edited);
        title.Text = "zulu";
        var details = Control<Button>(window, clicked, "ItemDetailsButton");
        PreviewClick(details);
        details.Focus();
        details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.IsTrue(clicked.IsExpanded);
        Assert.IsFalse(edited.IsExpanded);
        Assert.AreSame(clicked, ((FrameworkElement)Keyboard.FocusedElement).DataContext);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedHideKeepsCreationEditableAndRetryUsesTheLatestContent(bool populated) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        if (populated) notes.Text = "pending";
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        service.FailSave = true;
        WithWarningDismissed(window, () => Assert.IsFalse(window.TryHide()));
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(model.HasPersistenceError);
        notes.Text = "latest content";
        service.FailSave = false;
        Assert.IsTrue(window.TryHide());
        Assert.AreEqual("latest content", service.Load().Last().Notes);
    });

    [TestMethod]
    public void EmptyCleanupDoesNotDeleteContentAddedDuringTheWrite() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        service.BeforeSave = () => item.Notes = "arrived during saving";
        Assert.IsTrue(window.TryHide());
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual("arrived during saving", service.Load().Last().Notes);
    });

    [TestMethod]
    public void UnfinishedCreationAutosavesAndReloadsAsEstablishedContent() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        item.Text = "saved before completion";
        item.Notes = "multiline\nnotes";
        PumpUntil(() => service.SaveCount > 0);
        using var reloaded = new ChecklistWindowViewModel(service.Store, window.Dispatcher, action => action(), false);
        Assert.AreEqual(item.Text, reloaded.Items.Last().Text);
        Assert.AreEqual(item.Notes, reloaded.Items.Last().Notes);
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.IsTrue(model.Items.Contains(item));
    });

    [TestMethod]
    public void RepeatedEscapeDoesNotHideAfterFinishingAnItem() => WithWindow((window, model, _) =>
    {
        var item = Create(window, model);
        item.Text = "keep";
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        var list = (ListBox)window.FindName("ItemsList");
        var repeated = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repeated, [true]);
        list.RaiseEvent(repeated);
        Assert.IsTrue(repeated.Handled);
        Assert.IsTrue(window.IsVisible);
        Press(list, Key.Escape);
        Assert.IsFalse(window.IsVisible);
        Assert.IsTrue(model.Items.Contains(item));
    });

    [TestMethod]
    public void SearchAndTabNameEscapeDoNotDeleteContentOrHideTheWindow() => WithWindow((window, model, _) =>
    {
        var item = Create(window, model);
        item.Notes = "keep content when leaving the editor for search";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        Press((UIElement)Keyboard.FocusedElement, Key.F, ModifierKeys.Control);
        Pump();
        var search = (TextBox)window.FindName("SearchBox");
        Assert.IsTrue(search.IsKeyboardFocused);
        search.Text = "missing";
        Press(search, Key.Escape);
        Assert.IsFalse(model.IsSearchVisible);
        Assert.AreEqual("", model.SearchQuery);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(window.IsVisible);

        ((Button)window.FindName("AddTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("TabNamePopup");
        Assert.IsTrue(popup.IsOpen);
        Press((TextBox)window.FindName("TabNameTextBox"), Key.Escape);
        Assert.IsFalse(popup.IsOpen);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(window.IsVisible);
    });

    [TestMethod]
    public void ItemOwnedPopupsKeepCreationActive() => WithWindow((window, model, _) =>
    {
        var item = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        picker.IsDropDownOpen = true;
        Pump();
        Assert.IsTrue(picker.IsDropDownOpen);
        Press((UIElement)Keyboard.FocusedElement, Key.N, ModifierKeys.Control);
        Assert.HasCount(3, model.Items);
        PreviewClick(picker);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(item.IsExpanded);
        picker.IsDropDownOpen = false;

        var root = Control<Grid>(window, item, "ChecklistItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.IsOpen = true;
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        Press(title, Key.N, ModifierKeys.Control);
        Assert.HasCount(3, model.Items);
        root.ContextMenu.IsOpen = false;
        title.Focus();
        Press(title, Key.Escape);
        Assert.IsFalse(model.Items.Contains(item));
    });

    [TestMethod]
    public void OldFocusLossCannotRefreshTheNewlyEditedRow() => WithWindow((window, model, _) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        EditTitle(window, model.Items[0]);
        var secondItem = model.Items[1];
        var second = EditTitle(window, secondItem);
        second.Text = "aardvark";
        Pump();
        Assert.IsTrue(second.IsKeyboardFocused);
        Assert.AreSame(secondItem, model.FilteredItems[1]);
        Press(second, Key.Enter, ModifierKeys.Control);
        Assert.AreSame(secondItem, model.FilteredItems[0]);
    });

    [TestMethod]
    public void LaterChangesDuringSaveRemainEditableUntilTheNextCompletion() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        notes.Text = "requested save";
        service.BeforeSave = () => item.Notes = "later content";
        WithWarningDismissed(window, () => Press(notes, Key.Enter, ModifierKeys.Control));
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual("requested save", service.Load().Last().Notes);
        Press(notes, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsExpanded);
        Assert.AreEqual("later content", service.Load().Last().Notes);
    });

    [TestMethod]
    public void HidingRemovesOnlyTheActiveWhitespaceCreationAndLaterSavesDoNotRestoreIt() => WithWindow((window, model, service) =>
    {
        var placeholder = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.Enter, ModifierKeys.Control);
        var canceled = Create(window, model);
        canceled.Text = "  ";
        canceled.Notes = "\t\r\n";
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.HasCount(4, service.Load());
        Assert.IsTrue(window.TryHide());
        Assert.IsTrue(model.Items.Contains(placeholder));
        Assert.IsFalse(model.Items.Contains(canceled));
        model.Items[0].Notes = "later save";
        Assert.IsTrue(window.TryFlushPendingContent(out error), error);
        Assert.HasCount(3, service.Load());
        Assert.HasCount(1, service.Load().Where(item => string.IsNullOrWhiteSpace(item.Text)).ToArray());
    });

    [TestMethod]
    public void DeletedCreationCannotBypassTheHidePersistenceGuard() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        model.RemoveItem(item);
        service.FailSave = true;
        WithWarningDismissed(window, () => Assert.IsFalse(window.TryHide()));
        Assert.IsTrue(window.IsVisible);
        Assert.HasCount(3, service.Load());
        service.FailSave = false;
        Assert.IsTrue(window.TryHide());
        Assert.HasCount(2, service.Load());
    });

    private static ChecklistItem Create(ChecklistWindow window, ChecklistWindowViewModel model)
    {
        Press((UIElement)window.FindName("ItemsList"), Key.N, ModifierKeys.Control);
        Pump();
        return model.Items.Last();
    }

    private static TextBox EditTitle(ChecklistWindow window, ChecklistItem item)
    {
        var root = Control<Grid>(window, item, "ChecklistItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, "Edit title"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return Control<TextBox>(window, item, "ItemTitleTextBox");
    }

    private static T Control<T>(ChecklistWindow window, ChecklistItem item, string name) where T : DependencyObject
    {
        window.UpdateLayout();
        var list = (ListBox)window.FindName("ItemsList");
        return VisualTreeHelpers.FindDescendant<T>(list.ItemContainerGenerator.ContainerFromItem(item), name)!;
    }

    private static void Press(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        KeyboardTestInput.Press(target, key, modifiers);

    private static void PreviewClick(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    {
        RoutedEvent = Mouse.PreviewMouseDownEvent
    });

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => frame.Continue = !condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(condition());
    }

    private static void WithWarningDismissed(ChecklistWindow window, Action action)
    {
        var dismiss = window.Dispatcher.BeginInvoke(() =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<AppDialog>().ToArray()) dialog.Close();
        }, DispatcherPriority.ApplicationIdle);
        try { action(); }
        finally { dismiss.Abort(); }
    }

    private static void WithWindow(Action<ChecklistWindow, ChecklistWindowViewModel, RecordingService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingService(directory.Path);
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), service);
        var previous = WindowManager.Checklist;
        try
        {
            window.Show();
            window.Activate();
            Pump();
            action(window, (ChecklistWindowViewModel)window.DataContext, service);
        }
        finally
        {
            service.FailSave = false;
            service.BeforeSave = null;
            WindowManager.Checklist = previous;
            window.Close();
            Pump();
        }
    });

    private sealed class RecordingService : IChecklistContentService
    {
        internal ChecklistContentService Store { get; }
        internal bool FailSave { get; set; }
        internal Action? BeforeSave { get; set; }
        internal int SaveCount { get; private set; }

        internal RecordingService(string directory)
        {
            Store = new ChecklistContentService(directory);
            Store.SaveItems([new ChecklistItemState { Text = "alpha" }, new ChecklistItemState { Text = "bravo" }]);
        }

        internal IReadOnlyList<ChecklistItemState> Load() => Store.LoadItems().Items;
        public ChecklistContentLoadResult LoadItems() => Store.LoadItems();
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => Store.SaveTabs(tabs);

        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            SaveCount++;
            var before = BeforeSave;
            BeforeSave = null;
            before?.Invoke();
            if (FailSave) throw new IOException("Simulated save failure.");
            return Store.SaveItems(items);
        }
    }
}
