using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistAutosaveTests
{
    [TestMethod]
    public void TypingSchedulesPersistenceWithoutLosingFocusOrSelection() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.SelectAll();
        title.SelectedText = "typed title";
        title.Select(2, 3);

        Assert.AreEqual("typed title", item.Text);
        PumpUntil(() => service.SaveCount > 0);

        Assert.AreEqual("typed title", service.Saved[0].Text);
        Assert.IsTrue(title.IsKeyboardFocused);
        Assert.AreEqual(2, title.SelectionStart);
        Assert.AreEqual(3, title.SelectionLength);
        Assert.IsTrue(title.CanUndo);
        title.Undo();
        Assert.AreEqual("alpha", item.Text);
        title.Redo();
        Assert.AreEqual("typed title", item.Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TypingDefersSearchAndAlphabeticalChangesUntilFocusLeaves(bool search) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        if (search)
            model.SearchQuery = "alpha";
        else
            model.SortMode = ChecklistSortMode.Alphabetical;
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.SelectAll();
        title.SelectedText = "zulu";
        title.Select(1, 2);

        Assert.AreSame(item, model.FilteredItems[0]);
        Assert.AreSame(title, Title(window, item));
        Assert.IsTrue(title.IsKeyboardFocused);
        Assert.AreEqual(1, title.SelectionStart);
        Assert.AreEqual(2, title.SelectionLength);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual("zulu", service.Saved[0].Text);

        // transfer real keyboard focus to exercise the XAML LostFocus handler.
        var searchBox = (TextBox)window.FindName("SearchBox");
        model.IsSearchVisible = true;
        Layout(window);
        Assert.IsTrue(searchBox.Focus());

        if (search)
            Assert.HasCount(0, model.FilteredItems);
        else
            Assert.AreEqual("bravo", model.FilteredItems[0].Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EnterAndEscapeKeepTheirEditingBehavior(bool editNewItem) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.SelectAll();
        title.SelectedText = "changed";
        Assert.IsTrue(InvokeKeyHandler(window, title, Key.Enter).Handled);
        PumpUntil(() => model.Items.Count == 3 && Keyboard.FocusedElement is TextBox box && box.Text.Length == 0);
        Assert.AreEqual("changed", item.Text);
        var blank = model.Items[1];
        var blankTitle = Title(window, blank);
        Assert.AreSame(blankTitle, Keyboard.FocusedElement);

        title = editNewItem ? blankTitle : Title(window, item);
        Assert.IsTrue(title.Focus());
        title.SelectAll();
        title.SelectedText = "kept after Escape";
        Assert.IsTrue(InvokeKeyHandler(window, title, Key.Escape).Handled);

        Assert.AreEqual("kept after Escape", editNewItem ? blank.Text : item.Text);
        Assert.IsNull(Keyboard.FocusedElement);
        Assert.HasCount(3, model.Items);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual(editNewItem ? "changed" : "kept after Escape", service.Saved[0].Text);
        Assert.AreEqual(editNewItem ? "kept after Escape" : "", service.Saved[1].Text);
    });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RepeatedBackspaceInHandlerKeepsEmptyItems(bool newItem, bool withProperties) => WithWindow((window, model, service) =>
    {
        var item = newItem ? model.InsertItemAfter(model.Items[0])! : model.Items[0];
        if (withProperties)
        {
            item.Notes = "keep these notes";
            item.Priority = ChecklistPriority.High;
            item.DueDate = new DateTime(2026, 10, 22);
        }
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.Clear();
        var items = model.Items.ToArray();
        var filtered = model.FilteredItems.ToArray();
        var list = (ListBox)window.FindName("ItemsList");
        list.SelectedItem = item;

        for (var repeat = 0; repeat < 5; repeat++)
            Assert.IsFalse(InvokeKeyHandler(window, title, Key.Back).Handled);

        CollectionAssert.AreEqual(items, model.Items.ToArray());
        CollectionAssert.AreEqual(filtered, model.FilteredItems.ToArray());
        Assert.AreSame(item, list.SelectedItem);
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.AreEqual(0, title.SelectionStart);
        Assert.AreEqual(0, title.SelectionLength);
        Assert.AreEqual("", item.Text);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        var saved = service.Saved[model.Items.IndexOf(item)];
        Assert.AreEqual("", saved.Text);
        Assert.AreEqual(item.Notes, saved.Notes);
        Assert.AreEqual(item.Priority, saved.Priority);
        Assert.AreEqual(item.DueDate, saved.DueDate);
    });

    [TestMethod]
    public void RoutedBackspaceDeletesTextAndKeepsEmptyItem() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.Select(title.Text.Length, 0);

        void Backspace()
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(title), 0, Key.Back)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            title.RaiseEvent(args);
            Assert.IsTrue(args.Handled);
            Assert.AreEqual(title.Text, item.Text);
            Assert.HasCount(2, model.Items);
            Assert.IsTrue(model.Items.Contains(item));
            Assert.IsTrue(title.IsKeyboardFocused);
        }

        Backspace();
        Assert.AreEqual("alph", title.Text);
        title.Select(1, 2);
        Backspace();
        Assert.AreEqual("ah", title.Text);
        title.SelectAll();
        Backspace();
        Assert.AreEqual("", title.Text);
        Backspace();
        Backspace();
        Assert.AreEqual("", title.Text);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual("", service.Saved[0].Text);
    });

    [TestMethod]
    public void ExplicitDeleteItemMenuActionStillRemovesOnlyItsItem() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var other = model.Items[1];
        item.Notes = "intentional deletion includes notes";
        var title = Title(window, item);
        Assert.IsTrue(title.Focus());
        title.Clear();
        FrameworkElement? row = title;
        while (row is not null && row.ContextMenu is null)
            row = VisualTreeHelper.GetParent(row) as FrameworkElement;
        Assert.IsNotNull(row?.ContextMenu);
        var menu = row.ContextMenu;
        menu.PlacementTarget = row;
        var delete = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, "Delete item"));

        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.HasCount(1, model.Items);
        Assert.AreSame(other, model.Items[0]);
        Assert.IsFalse(model.Items.Contains(item));
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.HasCount(1, service.Saved);
        Assert.AreEqual("bravo", service.Saved[0].Text);
    });

    [TestMethod]
    [DataRow("hide")]
    [DataRow("close")]
    [DataRow("shutdown")]
    public void LifecycleFlushIncludesTheStillFocusedTitle(string action) => WithWindow((window, model, service) =>
    {
        var title = Title(window, model.Items[0]);
        Assert.IsTrue(title.Focus());
        title.SelectAll();
        title.SelectedText = "pending title";
        Assert.AreEqual(0, service.SaveCount);
        Assert.IsTrue(title.IsKeyboardFocused);

        if (action == "hide")
            typeof(ChecklistWindow).GetMethod("RequestHide", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        else if (action == "close")
            window.Close();
        else
        {
            Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
            window.PrepareForApplicationShutdown();
        }

        Assert.AreEqual("pending title", service.Saved[0].Text);
    });

    [TestMethod]
    public void NotesAndPropertiesStillScheduleTheirExistingSnapshots() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.Notes = "description";
        item.Priority = ChecklistPriority.High;
        item.DueDate = new DateTime(2026, 10, 22);
        item.IsChecked = true;

        PumpUntil(() => service.SaveCount > 0);

        var saved = service.Saved[0];
        Assert.AreEqual("description", saved.Notes);
        Assert.AreEqual(ChecklistPriority.High, saved.Priority);
        Assert.AreEqual(item.DueDate, saved.DueDate);
        Assert.IsTrue(saved.IsChecked);
    });

    private static void WithWindow(Action<ChecklistWindow, ChecklistWindowViewModel, RecordingContentService> action)
    {
        WpfTestHost.Invoke(() =>
        {
            using var directory = new TemporaryTestDirectory();
            var service = new RecordingContentService();
            var window = new ChecklistWindow(new AppSettingsService(directory.Path), service);
            var content = (FrameworkElement)window.Content;
            content.DataContext = window.DataContext;
            window.Content = null;
            using var source = new HwndSource(new HwndSourceParameters("Noted checklist autosave regression")
            {
                Width = 600, Height = 700, WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = content;
                Layout(window);
                action(window, (ChecklistWindowViewModel)window.DataContext, service);
            }
            finally
            {
                Keyboard.ClearFocus();
                source.RootVisual = null;
                window.Content = content;
                window.Close();
            }
        });
    }

    private static void Layout(ChecklistWindow window)
    {
        var content = (FrameworkElement)PresentationSource.FromVisual((ListBox)window.FindName("ItemsList")).RootVisual;
        content.Measure(new Size(600, 700));
        content.Arrange(new Rect(0, 0, 600, 700));
        content.UpdateLayout();
    }

    private static TextBox Title(ChecklistWindow window, ChecklistItem item)
    {
        Layout(window);
        var list = (ListBox)window.FindName("ItemsList");
        var container = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(item);
        return VisualTreeHelpers.FindDescendant<TextBox>(container, "ItemTitleTextBox")!;
    }

    private static KeyEventArgs InvokeKeyHandler(ChecklistWindow window, TextBox title, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(title), 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        // this checks the handler only; it does not exercise WPF keyboard routing or physical modifiers.
        typeof(ChecklistWindow).GetMethod("ItemTextBox_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [title, args]);
        return args;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => frame.Continue = !condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(condition(), "The expected dispatcher work did not complete within five seconds.");
    }

    private sealed class RecordingContentService : IChecklistContentService
    {
        internal int SaveCount { get; private set; }
        internal IReadOnlyList<ChecklistItemState> Saved { get; private set; } = [];

        public ChecklistContentLoadResult LoadItems() => new(
            [new ChecklistItemState { Text = "alpha" }, new ChecklistItemState { Text = "bravo" }], [], []);

        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            SaveCount++;
            Saved = items.ToList();
            return new(null);
        }

        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
