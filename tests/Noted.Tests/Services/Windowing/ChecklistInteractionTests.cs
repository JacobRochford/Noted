using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class ChecklistInteractionTests
{
    [TestMethod]
    public void BrowsingAndCtrlEnterDoNotStartEditingOrExpandRows() => WithWindow((window, model, service) =>
    {
        var first = model.Items[0];
        var second = model.Items[1];
        Row(window, first).Focus();
        Press(Row(window, first), Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(first.IsExpanded);
        Assert.IsFalse(first.IsEditingTitle);
        Assert.IsFalse(Control<TextBox>(window, first, "ItemTitleTextBox").IsVisible);
        Press(Row(window, first), Key.Down);
        Assert.AreSame(Row(window, second), Keyboard.FocusedElement);
        Assert.IsFalse(second.IsExpanded);
        Press(Row(window, second), Key.Up);
        Assert.AreSame(Row(window, first), Keyboard.FocusedElement);
        Press(Row(window, first), Key.Enter);
        Assert.IsTrue(first.IsExpanded);
        Assert.IsFalse(first.IsEditingTitle);
        Assert.AreEqual(0, service.SaveCount);
    });

    [TestMethod]
    public void EstablishedTitleEnterKeepsExpansionAndUndoAndReturnsFocusToItsRow() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.IsExpanded = true;
        var title = EditTitle(window, item);
        title.SelectAll();
        title.SelectedText = "changed";
        Assert.IsTrue(title.CanUndo);
        Press(title, Key.Enter);
        Assert.HasCount(2, model.Items);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.AreSame(Row(window, item), Keyboard.FocusedElement);
        Assert.AreEqual("changed", service.Load()[0].Text);
        Assert.AreSame(title, EditTitle(window, item));
        Assert.IsTrue(title.CanUndo);
        title.Undo();
        Assert.AreEqual("alpha", item.Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreationEnterCreatesNextAndCtrlEnterPreservesTheBlankPlaceholder(bool notes) => WithWindow((window, model, service) =>
    {
        var first = Create(window, model);
        Press(Control<TextBox>(window, first, "ItemTitleTextBox"), Key.Enter);
        Pump();
        var second = model.Items.Last();
        Assert.AreNotSame(first, second);
        Assert.IsFalse(first.IsEditingTitle);
        Assert.HasCount(4, model.Items);
        if (notes) Press(Control<TextBox>(window, second, "ItemTitleTextBox"), Key.Tab);
        Press((UIElement)Keyboard.FocusedElement, Key.Enter, ModifierKeys.Control);
        Assert.HasCount(4, model.Items);
        Assert.HasCount(4, service.Load());
        Assert.IsFalse(second.IsEditingTitle);
        Assert.IsFalse(second.IsExpanded);
        Assert.AreSame(Row(window, second), Keyboard.FocusedElement);
    });

    [TestMethod]
    public void DirectNotesEditingSavesAndCollapsesWithoutOpeningTheTitleEditor() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var notes = EditNotes(window, item);
        Assert.IsFalse(item.IsEditingTitle);
        notes.Text = "direct notes";
        notes.CaretIndex = notes.Text.Length;
        Press(notes, Key.Enter);
        StringAssert.Contains(notes.Text, Environment.NewLine);
        Assert.HasCount(2, model.Items);
        Press(notes, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.AreEqual(item.Notes, service.Load()[0].Notes);
        Assert.AreSame(Row(window, item), Keyboard.FocusedElement);
    });

    [TestMethod]
    public void ExplicitTitleTabRestoresSelectionAndIndependentNotesTabDoesNotOpenTitle() => WithWindow((window, model, service) =>
    {
        var first = model.Items[0];
        var title = EditTitle(window, first);
        title.Select(1, 2);
        Press(title, Key.Tab);
        var notes = Control<TextBox>(window, first, "NotesTextBox");
        Assert.AreSame(notes, Keyboard.FocusedElement);
        Press(notes, Key.Tab, ModifierKeys.Shift);
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.AreEqual(1, title.SelectionStart);
        Assert.AreEqual(2, title.SelectionLength);
        Press(title, Key.Enter);
        notes.Focus();
        Press(notes, Key.Tab, ModifierKeys.Shift);
        Assert.IsFalse(first.IsEditingTitle);
        Assert.AreSame(Control<CheckBox>(window, first, "ItemCompletionCheckBox"), Keyboard.FocusedElement);
    });

    [TestMethod]
    public void TabVisitsOnlySelectedRowControlsAndLeavesWithoutActivatingButtons() => WithWindow((window, model, service) =>
    {
        var first = model.Items[0];
        var second = model.Items[1];
        first.IsExpanded = true;
        Row(window, first).Focus();
        var visited = new List<UIElement>();
        for (var index = 0; index < 12 && Keyboard.FocusedElement is UIElement focused; index++)
        {
            if (!ReferenceEquals(focused, Row(window, first)) &&
                !Row(window, first).IsAncestorOf(focused)) break;
            visited.Add(focused);
            Press(focused, Key.Tab);
        }
        Assert.IsTrue(visited.Contains(Control<Button>(window, first, "ItemDetailsButton")));
        Assert.IsTrue(visited.Contains(Control<TextBox>(window, first, "NotesTextBox")));
        Assert.IsTrue(visited.OfType<DatePickerTextBox>().Any());
        Assert.IsFalse(second.IsExpanded);
        Assert.IsFalse(second.IsEditingTitle);
        Assert.IsFalse(Row(window, second).IsKeyboardFocusWithin);
        Assert.IsFalse(((ListBox)window.FindName("ItemsList")).IsKeyboardFocusWithin,
            string.Join(" -> ", visited.OfType<FrameworkElement>().Select(control => control.GetType().Name + ":" + control.Name)));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartingAnotherEditorCompletesTheOriginAndPreservesTheDestinationAfterSorting(bool notesDestination) => WithWindow((window, model, service) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        var first = model.Items[0];
        var second = model.Items[1];
        var title = EditTitle(window, first);
        title.Text = "zulu";
        second.IsExpanded = true;
        if (notesDestination) Control<TextBox>(window, second, "NotesTextBox").Focus();
        else EditTitle(window, second);
        Pump();
        Assert.IsFalse(first.IsEditingTitle);
        Assert.IsFalse(first.IsExpanded);
        var destination = Control<TextBox>(window, second, notesDestination ? "NotesTextBox" : "ItemTitleTextBox");
        Assert.AreSame(destination, Keyboard.FocusedElement);
        Assert.AreEqual(!notesDestination, second.IsEditingTitle);
        destination.Text = "destination edit";
        Press(destination, Key.Enter, ModifierKeys.Control);
        Assert.AreEqual(notesDestination ? "destination edit" : "", service.Load()[1].Notes);
        Assert.IsFalse(second.IsExpanded);
    });

    [TestMethod]
    [DataRow("tab")]
    [DataRow("search")]
    [DataRow("sort")]
    [DataRow("title")]
    [DataRow("notes")]
    [DataRow("hide")]
    [DataRow("create tab")]
    public void FailedNotesCompletionKeepsItsEditorAndBlocksViewOrSessionChanges(string action) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var notes = EditNotes(window, item);
        notes.Text = "pending notes";
        notes.Select(2, 3);
        var tab = model.SelectedTab;
        service.FailSave = true;
        WithWarningDismissed(window, () =>
        {
            switch (action)
            {
                case "tab": model.SelectTab(model.FindTab("done")); break;
                case "search": model.SearchQuery = "no match"; break;
                case "sort": model.SortMode = ChecklistSortMode.Alphabetical; break;
                case "title": EditTitleFromMenu(window, model.Items[1]); break;
                case "notes": EditNotes(window, model.Items[1]); break;
                case "hide": Assert.IsFalse(window.TryHide()); break;
                case "create tab":
                    Assert.IsFalse(model.TryCreateTab("Work", out var created));
                    Assert.IsNull(created);
                    break;
            }
        });
        Pump();
        Assert.AreSame(tab, model.SelectedTab);
        Assert.AreEqual("", model.SearchQuery);
        Assert.AreEqual(ChecklistSortMode.Manual, model.SortMode);
        Assert.IsTrue(model.FilteredItems.Contains(item));
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.IsFalse(model.Items[1].IsEditingTitle);
        Assert.AreSame(notes, Keyboard.FocusedElement);
        Assert.AreEqual(2, notes.SelectionStart);
        Assert.AreEqual(3, notes.SelectionLength);
        service.FailSave = false;
        notes.SelectedText = "latest";
        Press(notes, Key.Enter, ModifierKeys.Control);
        Assert.AreEqual(item.Notes, service.Load()[0].Notes);
        Assert.IsFalse(item.IsExpanded);
    });

    [TestMethod]
    public void PriorityChangeCannotRemoveAnActiveNotesEditorBeforeSuccessfulCompletion() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.Priority = ChecklistPriority.High;
        model.SelectTab(model.FindTab("urgent"));
        var notes = EditNotes(window, item);
        notes.Text = "keep accessible";
        item.Priority = ChecklistPriority.Low;
        Assert.IsTrue(model.FilteredItems.Contains(item));
        service.FailSave = true;
        WithWarningDismissed(window, () => Press(notes, Key.Enter, ModifierKeys.Control));
        Assert.IsTrue(model.FilteredItems.Contains(item));
        Assert.AreSame(notes, Keyboard.FocusedElement);
        service.FailSave = false;
        Press(notes, Key.Enter, ModifierKeys.Control);
        Pump();
        Assert.IsFalse(model.FilteredItems.Contains(item));
        Assert.AreEqual(ChecklistPriority.Low, service.Load()[0].Priority);
        Assert.AreSame(window.FindName("ItemsList"), Keyboard.FocusedElement);
    });

    [TestMethod]
    [DataRow("all")]
    [DataRow("urgent")]
    [DataRow("custom")]
    public void CompletedItemsKeepTheirMembershipOrderAndSavedAssignment(string tabId) => WithWindow((window, model, service) =>
    {
        Assert.IsTrue(model.TryCreateTab("Work", out var custom));
        var first = model.Items[0];
        var second = model.Items[1];
        first.TabId = second.TabId = custom!.Id;
        first.Priority = second.Priority = ChecklistPriority.High;
        var tab = tabId == "custom" ? custom : model.FindTab(tabId);
        model.SelectTab(tab);
        var original = model.FilteredItems.ToArray();
        first.IsChecked = true;
        CollectionAssert.AreEqual(original, model.FilteredItems.ToArray());
        Assert.AreEqual(2, model.TotalCount);
        Assert.AreEqual(1, model.CompletedCount);
        Assert.AreEqual(1, model.RemainingCount);
        model.SelectTab(model.FindTab("done"));
        Assert.HasCount(1, model.FilteredItems);
        Assert.AreSame(first, model.FilteredItems[0]);
        model.SelectTab(tab);
        CollectionAssert.AreEqual(original, model.FilteredItems.ToArray());
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.IsTrue(service.Load()[0].IsChecked);
        Assert.AreEqual(custom.Id, service.Load()[0].TabId);
        foreach (var sort in Enum.GetValues<ChecklistSortMode>())
        {
            model.SortMode = sort;
            var sorted = model.FilteredItems.ToArray();
            first.IsChecked = false;
            CollectionAssert.AreEqual(sorted, model.FilteredItems.ToArray());
            first.IsChecked = true;
            CollectionAssert.AreEqual(sorted, model.FilteredItems.ToArray());
        }
        Assert.IsTrue(window.TryFlushPendingContent(out error), error);
        using var reloaded = new ChecklistWindowViewModel(service, Dispatcher.CurrentDispatcher, action => action(), false);
        reloaded.SelectTab(reloaded.FindTab(tab!.Id));
        Assert.HasCount(2, reloaded.FilteredItems);
        Assert.IsTrue(reloaded.FilteredItems[0].IsChecked);
        Assert.AreEqual(custom.Id, reloaded.FilteredItems[0].TabId);
    });

    [TestMethod]
    public void DoneUncheckFocusesTheNextRowAndHeldSpaceDoesNotUncheckAnotherItem() => WithWindow((window, model, service) =>
    {
        foreach (var item in model.Items) item.IsChecked = true;
        model.SelectTab(model.FindTab("done"));
        var first = model.Items[0];
        var second = model.Items[1];
        Row(window, first).Focus();
        Press(Row(window, first), Key.Space);
        Pump();
        Assert.HasCount(1, model.FilteredItems);
        Assert.AreSame(Row(window, second), Keyboard.FocusedElement);
        Repeat(Row(window, second), Key.Space);
        Repeat(Row(window, second), Key.Enter);
        Assert.IsTrue(second.IsChecked);
        Assert.IsFalse(second.IsExpanded);
    });

    [TestMethod]
    public void FilteredBulkActionsAndGlobalClearKeepTheirExistingScopes() => WithWindow((window, model, service) =>
    {
        foreach (var item in model.Items) item.IsChecked = true;
        model.SearchQuery = "alpha";
        model.UncheckAll();
        Assert.IsFalse(model.Items[0].IsChecked);
        Assert.IsTrue(model.Items[1].IsChecked);
        model.CheckAll();
        Assert.IsTrue(model.Items.All(item => item.IsChecked));
        model.ClearCompleted();
        Assert.HasCount(0, model.Items);
    });

    [TestMethod]
    public void ValidTypedDateIsSavedBeforeCollapseAndInvalidDateBlocksHideAndTabChange() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        EditNotes(window, item);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)!;
        editor.Focus();
        editor.Text = "not a date";
        Press(editor, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(item.IsExpanded);
        Assert.AreEqual("not a date", editor.Text);
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.IsTrue(Control<TextBlock>(window, item, "DueDateErrorText").IsVisible);
        var tab = model.SelectedTab;
        model.SelectTab(model.FindTab("done"));
        Assert.AreSame(tab, model.SelectedTab);
        Assert.IsFalse(window.TryHide());
        Assert.AreEqual("not a date", editor.Text);
        Press(editor, Key.Down, ModifierKeys.Alt);
        Assert.IsFalse(picker.IsDropDownOpen);
        Assert.AreEqual("not a date", editor.Text);
        var calendarButton = VisualTreeHelpers.FindDescendant<Button>(picker, "PART_Button")!;
        var open = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        calendarButton.RaiseEvent(open);
        Assert.IsTrue(open.Handled);
        Assert.AreEqual("not a date", editor.Text);
        window.Close();
        Assert.IsTrue(window.IsVisible);
        var date = new DateTime(2027, 3, 14);
        editor.Text = date.ToString("d", picker.Language.GetSpecificCulture());
        Press(editor, Key.Enter, ModifierKeys.Control);
        Assert.AreEqual(date, item.DueDate);
        Assert.AreEqual(date, service.Load()[0].DueDate);
        Assert.IsFalse(item.IsExpanded);
    });

    [TestMethod]
    public void CollapsingAnEmptyCreationRemovesItButNeverDeletesAnEstablishedBlank() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Press(Control<TextBox>(window, item, "ItemTitleTextBox"), Key.Tab);
        Click(Control<Button>(window, item, "ItemDetailsButton"));
        Assert.IsFalse(model.Items.Contains(item));
        Assert.HasCount(2, service.Load());
        var established = model.Items[0];
        EditTitle(window, established).Clear();
        Press(Control<TextBox>(window, established, "ItemTitleTextBox"), Key.Tab);
        Click(Control<Button>(window, established, "ItemDetailsButton"));
        Assert.IsTrue(model.Items.Contains(established));
        Assert.IsFalse(established.IsExpanded);
        Assert.AreEqual("", service.Load()[0].Text);
    });

    [TestMethod]
    public void F2InNotesDoesNotOpenTitleAndReadingExpansionDoesNotSchedulePersistence() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.IsExpanded = true;
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual(0, service.SaveCount);
        var notes = EditNotes(window, item);
        Press(notes, Key.F2);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.AreSame(notes, Keyboard.FocusedElement);
    });

    [TestMethod]
    public void HeaderClicksToggleOncePerDoubleClickAndChildControlsDoNotToggleExpansion() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.DueDate = new DateTime(2027, 3, 14);
        var title = Control<TextBlock>(window, item, "ItemTitleText");
        MouseClick(title);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        MouseClick(title, 2);
        Assert.IsTrue(item.IsExpanded);
        MouseClick(Control<CheckBox>(window, item, "ItemCompletionCheckBox"));
        Assert.IsTrue(item.IsExpanded);
        MouseClick(Control<Border>(window, item, "ItemDueDateChip"));
        Assert.IsTrue(item.IsExpanded);
        MouseClick(title);
        Assert.IsTrue(item.IsEditingTitle);
        Assert.IsTrue(item.IsExpanded);
        MouseClick(Control<TextBox>(window, item, "NotesTextBox"));
        Assert.IsTrue(item.IsExpanded);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedCollapsePreservesTheActiveEditorAndAnEmptyCreation(bool creation) => WithWindow((window, model, service) =>
    {
        var item = creation ? Create(window, model) : model.Items[0];
        var notes = EditNotes(window, item);
        if (!creation) notes.Text = "pending notes";
        service.FailSave = true;
        WithWarningDismissed(window, () => Click(Control<Button>(window, item, "ItemDetailsButton")));
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(item.IsExpanded);
        Assert.AreSame(notes, Keyboard.FocusedElement);
        service.FailSave = false;
        Click(Control<Button>(window, item, "ItemDetailsButton"));
        Assert.AreEqual(!creation, model.Items.Contains(item));
        Assert.IsFalse(item.IsExpanded);
    });

    [TestMethod]
    public void RemovingTheActiveItemClearsEditingAndFocusesASurvivingRow() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var next = model.Items[1];
        EditTitle(window, item);
        model.RemoveItem(item);
        Pump();
        Assert.IsFalse(item.IsEditingTitle);
        Assert.IsFalse(model.FilteredItems.Contains(item));
        Assert.AreSame(Row(window, next), Keyboard.FocusedElement);
        Assert.IsTrue(window.TryHide());
        Assert.HasCount(1, service.Load());
        Assert.AreEqual(next.Text, service.Load()[0].Text);
    });

    [TestMethod]
    public void MovingAnActiveNotesItemToAnotherTabKeepsItAccessibleUntilSaved() => WithWindow((window, model, service) =>
    {
        Assert.IsTrue(model.TryCreateTab("Work", out var tab));
        var item = model.Items[0];
        item.TabId = tab!.Id;
        model.SelectTab(tab);
        var notes = EditNotes(window, item);
        notes.Text = "moved content";
        model.MoveItemToTab(item, null);
        service.FailSave = true;
        WithWarningDismissed(window, () => Press(notes, Key.Enter, ModifierKeys.Control));
        Assert.IsTrue(model.FilteredItems.Contains(item));
        Assert.AreSame(notes, Keyboard.FocusedElement);
        service.FailSave = false;
        Press(notes, Key.Enter, ModifierKeys.Control);
        Pump();
        Assert.IsFalse(model.FilteredItems.Contains(item));
        Assert.IsNull(service.Load()[0].TabId);
        Assert.AreEqual("moved content", service.Load()[0].Notes);
    });

    [TestMethod]
    public void ClickingAnotherRowsControlPreservesThatDestinationAfterCompletionAndSorting() => WithWindow((window, model, service) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        var origin = model.Items[0];
        var destination = model.Items[1];
        EditTitle(window, origin).Text = "zulu";
        var button = Control<Button>(window, destination, "ItemDetailsButton");
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        button.Focus();
        Click(button);
        Pump();
        Assert.IsFalse(origin.IsEditingTitle);
        Assert.IsFalse(origin.IsExpanded);
        Assert.IsTrue(destination.IsExpanded);
        Assert.IsFalse(destination.IsEditingTitle);
        Assert.AreSame(Control<Button>(window, destination, "ItemDetailsButton"), Keyboard.FocusedElement);
    });

    [TestMethod]
    public void SearchExclusionCompletesNotesAndFocusesTheSurvivingRow() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        EditNotes(window, item).Text = "saved before filtering";
        model.SearchQuery = "bravo";
        Pump();
        Assert.IsFalse(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.AreEqual("saved before filtering", service.Load()[0].Notes);
        Assert.AreSame(Row(window, model.Items[1]), Keyboard.FocusedElement);
    });

    [TestMethod]
    public void StartingAnotherEditLeavesUnrelatedReadingRowsExpanded() => WithWindow((window, model, service) =>
    {
        var reading = model.AddItem()!;
        reading.Text = "reading";
        reading.IsExpanded = true;
        var first = model.Items[0];
        EditNotes(window, first).Text = "first edit";
        EditNotes(window, model.Items[1]);
        Pump();
        Assert.IsFalse(first.IsExpanded);
        Assert.IsTrue(reading.IsExpanded);
        Assert.IsFalse(reading.IsEditingTitle);
    });

    [TestMethod]
    public void TitleEditingStateDoesNotScheduleAutosaveAndKeyboardMenuCanClearPriority() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        EditTitle(window, item);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual(0, service.SaveCount);
        Press(Control<TextBox>(window, item, "ItemTitleTextBox"), Key.Enter);
        item.Priority = ChecklistPriority.High;
        Press(Row(window, item), Key.F10, ModifierKeys.Shift);
        var root = Control<Grid>(window, item, "ChecklistItemRoot");
        Assert.IsTrue(root.ContextMenu.IsOpen);
        var none = root.ContextMenu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Tag, "None"));
        none.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        root.ContextMenu.IsOpen = false;
        Assert.AreEqual(ChecklistPriority.None, item.Priority);
        Assert.IsFalse(item.IsEditingTitle);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TabCanLeaveAnEmptyListInEitherDirection(bool backward) => WithWindow((window, model, service) =>
    {
        model.Items.Clear();
        var list = (ListBox)window.FindName("ItemsList");
        list.Focus();
        Press(list, Key.F2);
        Assert.HasCount(0, model.Items);
        Press(list, Key.Tab, backward ? ModifierKeys.Shift : ModifierKeys.None);
        Assert.IsFalse(list.IsKeyboardFocusWithin);
        Assert.IsTrue(window.IsVisible);
    });

    [TestMethod]
    public void BoundSearchFailureRestoresTheQueryAndEditorAndSuccessfulRetryKeepsSearchFocus() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var notes = EditNotes(window, item);
        notes.Text = "saved notes";
        model.IsSearchVisible = true;
        window.UpdateLayout();
        var search = (TextBox)window.FindName("SearchBox");
        service.FailSave = true;
        WithWarningDismissed(window, () =>
        {
            search.Focus();
            search.Text = "missing";
        });
        Pump();
        Assert.AreEqual("", model.SearchQuery);
        Assert.AreEqual("", search.Text);
        Assert.AreSame(notes, Keyboard.FocusedElement);
        Assert.IsTrue(item.IsExpanded);
        service.FailSave = false;
        search.Focus();
        search.Text = "bravo";
        search.Select(1, 2);
        Pump();
        Assert.AreSame(search, Keyboard.FocusedElement);
        Assert.AreEqual(1, search.SelectionStart);
        Assert.AreEqual(2, search.SelectionLength);
        Assert.AreEqual("bravo", model.SearchQuery);
        Assert.IsFalse(model.FilteredItems.Contains(item));
        Assert.AreEqual("saved notes", service.Load()[0].Notes);
    });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SaveWarningCannotCompleteEditingAgainOrLoseTheCurrentSelection(bool creation, bool notesField) => WithWindow((window, model, service) =>
    {
        var item = creation ? Create(window, model) : model.Items[0];
        item.IsExpanded = true;
        var editor = notesField ? EditNotes(window, item) : EditTitle(window, item);
        editor.SelectAll();
        editor.SelectedText = "requested save";
        editor.Select(1, 3);
        service.BeforeSave = () =>
        {
            if (notesField) item.Notes = "later content";
            else item.Text = "later content";
        };
        WithWarningDismissed(window, () => Press(editor, Key.Enter, ModifierKeys.Control));
        Pump();
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual(creation || !notesField, item.IsEditingTitle);
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.AreEqual("later content", editor.Text);
        Assert.AreEqual(1, editor.SelectionStart);
        Assert.AreEqual(3, editor.SelectionLength);
        Press(editor, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        var saved = service.Load()[model.Items.IndexOf(item)];
        Assert.AreEqual("later content", notesField ? saved.Notes : saved.Text);
    });

    [TestMethod]
    [DataRow("alpha")]
    [DataRow("")]
    [DataRow("   ")]
    public void TitleClickCollapsedRowOnlyExpandsAndWaitsForAnIntentionalEdit(string text) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.Text = text;
        var display = Control<TextBlock>(window, item, "ItemTitleText");
        MouseClick(display);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.IsFalse(Control<TextBox>(window, item, "ItemTitleTextBox").IsVisible);
        Assert.IsFalse(Keyboard.FocusedElement is TextBox);
        Assert.AreEqual(text, item.Text);
    });

    [TestMethod]
    public void TitleClickExpandedRowReusesTheEditorWithoutSelectingAllAndKeepsUndo() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.IsExpanded = true;
        var editor = Control<TextBox>(window, item, "ItemTitleTextBox");
        MouseClick(Control<TextBlock>(window, item, "ItemTitleText"));
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(item.IsEditingTitle);
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.AreEqual(0, editor.SelectionLength);
        Assert.AreEqual("alpha", editor.Text);
        editor.SelectedText = "edited ";
        Assert.IsTrue(editor.CanUndo);
        Press(editor, Key.Enter);
        Assert.HasCount(2, model.Items);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.AreEqual(item.Text, service.Load()[0].Text);
        MouseClick(Control<TextBlock>(window, item, "ItemTitleText"));
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.AreEqual(0, editor.SelectionLength);
        Assert.IsTrue(editor.CanUndo);
        editor.Undo();
        Assert.AreEqual("alpha", item.Text);
    });

    [TestMethod]
    [DataRow("", false)]
    [DataRow("", true)]
    [DataRow("   ", false)]
    public void TitleClickPlaceholderIsDisplayOnlyAndNeverDisposesEstablishedEmptyItems(string text, bool completed) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.Text = text;
        item.IsChecked = completed;
        var display = Control<TextBlock>(window, item, "ItemTitleText");
        Assert.AreEqual("Add a title...", display.Text);
        Assert.IsTrue(display.ActualWidth > 0);
        MouseClick(display);
        MouseClick(display);
        var editor = Control<TextBox>(window, item, "ItemTitleTextBox");
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.AreEqual(text, editor.Text);
        Assert.AreEqual(0, editor.SelectionLength);
        Press(editor, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(model.FilteredItems.Contains(item));
        Assert.AreEqual(text, service.Load()[0].Text);
        Assert.AreEqual("", service.Load()[0].Notes);
        Assert.AreEqual("Add a title...", Control<TextBlock>(window, item, "ItemTitleText").Text);
        MouseClick(Control<TextBlock>(window, item, "ItemTitleText"));
        MouseClick(Control<TextBlock>(window, item, "ItemTitleText"));
        Press(Control<TextBox>(window, item, "ItemTitleTextBox"), Key.Escape);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.HasCount(2, service.Load());
    });

    [TestMethod]
    public void TitleClickPreservedBlankCreationRemainsEstablishedWhenEditedAndExited() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Press(Control<TextBox>(window, item, "ItemTitleTextBox"), Key.Enter, ModifierKeys.Control);
        var display = Control<TextBlock>(window, item, "ItemTitleText");
        Assert.AreEqual("Add a title...", display.Text);
        MouseClick(display);
        MouseClick(display);
        Press(Control<TextBox>(window, item, "ItemTitleTextBox"), Key.Escape);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsFalse(item.IsExpanded);
        Assert.HasCount(3, service.Load());
        Assert.AreEqual("", service.Load().Last().Text);
    });

    [TestMethod]
    public void TitleClickDoubleClickAndHeaderControlsKeepExpansionSeparateFromEditing() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var display = Control<TextBlock>(window, item, "ItemTitleText");
        MouseClick(display);
        MouseClick(display, 2);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        MouseClick(Control<Grid>(window, item, "ItemDragArea"));
        Assert.IsFalse(item.IsExpanded);
        Click(Control<Button>(window, item, "ItemDetailsButton"));
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        MouseClick(Control<CheckBox>(window, item, "ItemCompletionCheckBox"));
        Assert.IsTrue(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
        EditNotes(window, item);
        Assert.IsFalse(item.IsEditingTitle);
        MouseClick(display);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(item.IsEditingTitle);
        Click(Control<Button>(window, item, "ItemDetailsButton"));
        Assert.IsFalse(item.IsExpanded);
        Assert.IsFalse(item.IsEditingTitle);
    });

    [TestMethod]
    public void TitleClickMouseDownLeavesRoomForDraggingBeforeEditingOnRelease() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.IsExpanded = true;
        var display = Control<TextBlock>(window, item, "ItemTitleText");
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(down, 1);
        display.RaiseEvent(down);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.IsFalse(down.Handled);
        display.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        });
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(item.IsEditingTitle);
    });

    [TestMethod]
    public void TitleClickFailedPreviousNotesSaveKeepsItsEditorAndSuccessfulRetryEditsTheDestination() => WithWindow((window, model, service) =>
    {
        model.SortMode = ChecklistSortMode.Alphabetical;
        var origin = model.Items[0];
        var destination = model.Items[1];
        var notes = EditNotes(window, origin);
        notes.Text = "pending notes";
        origin.Text = "zulu";
        destination.IsExpanded = true;
        service.FailSave = true;
        WithWarningDismissed(window, () => MouseClick(Control<TextBlock>(window, destination, "ItemTitleText")));
        Pump();
        Assert.AreSame(notes, Keyboard.FocusedElement);
        Assert.IsTrue(origin.IsExpanded);
        Assert.IsFalse(destination.IsEditingTitle);
        service.FailSave = false;
        MouseClick(Control<TextBlock>(window, destination, "ItemTitleText"), pumpBeforeRelease: true);
        Pump();
        Assert.IsFalse(origin.IsExpanded);
        Assert.IsTrue(destination.IsExpanded);
        Assert.IsTrue(destination.IsEditingTitle);
        Assert.AreSame(Control<TextBox>(window, destination, "ItemTitleTextBox"), Keyboard.FocusedElement);
        Assert.AreEqual("pending notes", service.Load()[0].Notes);
    });

    [TestMethod]
    public void TitleClickRetainsTitleNotesNavigationAndLeavesTheListAfterDateControls() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.IsExpanded = true;
        item.DueDate = new DateTime(2027, 3, 14);
        MouseClick(Control<TextBlock>(window, item, "ItemTitleText"));
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        title.Select(1, 2);
        Press(title, Key.Tab);
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        Assert.AreSame(notes, Keyboard.FocusedElement);
        Press(notes, Key.Tab, ModifierKeys.Shift);
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.AreEqual(1, title.SelectionStart);
        Assert.AreEqual(2, title.SelectionLength);
        Press(title, Key.Tab);
        Press(notes, Key.Tab);
        Assert.IsTrue(Control<DatePicker>(window, item, "ItemDueDatePicker").IsKeyboardFocusWithin);
        for (var index = 0; index < 4 && ((ListBox)window.FindName("ItemsList")).IsKeyboardFocusWithin; index++)
            Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        Assert.IsFalse(((ListBox)window.FindName("ItemsList")).IsKeyboardFocusWithin);
        Assert.IsFalse(item.IsEditingTitle);
        Assert.IsNotNull(item.DueDate);
        Assert.IsNull(VisualTreeHelpers.FindDescendant<Button>(Row(window, item), "ItemEditTitleButton"));
    });

    [TestMethod]
    public void TitleClickCoordinatesChooseTheCaretWhileF2AndContextMenuRetainTheirBehavior() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        item.Text = "a longer title with several words";
        item.IsExpanded = true;
        typeof(ChecklistWindow).GetMethod("BeginTitleEditing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [item, new Point(0, 0)]);
        var title = Control<TextBox>(window, item, "ItemTitleTextBox");
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.AreEqual(0, title.SelectionLength);
        Assert.AreEqual(0, title.CaretIndex);
        Press(title, Key.Enter);
        EditTitle(window, item);
        Assert.AreEqual(title.Text.Length, title.SelectionLength);
        Press(title, Key.Enter);
        EditTitleFromMenu(window, item);
        Assert.AreSame(title, Keyboard.FocusedElement);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(item.IsEditingTitle);
        Assert.AreEqual(title.Text.Length, title.SelectionLength);
    });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void DateSpaceOpensCalendarAfterTabWithoutFinishingEditing(bool creation, bool hasDate) => WithWindow((window, model, service) =>
    {
        var item = creation ? Create(window, model) : model.Items[0];
        if (hasDate) item.DueDate = new DateTime(2027, 3, 14);
        var expectedDate = item.DueDate;
        var notes = EditNotes(window, item);
        Press(notes, Key.Tab);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)!;
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.AreEqual(editor.Text.Length, editor.SelectionLength);
        Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Space, ModifierKeys.None).Handled);
        Pump();
        Assert.IsTrue(picker.IsDropDownOpen);
        Assert.IsTrue(item.IsExpanded);
        Assert.AreEqual(creation, item.IsEditingTitle);
        Assert.IsFalse(item.IsChecked);
        Assert.AreEqual(expectedDate, item.DueDate);
        Assert.HasCount(creation ? 3 : 2, model.Items);
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Pump();
        Assert.IsFalse(picker.IsDropDownOpen);
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
    });

    [TestMethod]
    public void DateSpaceKeepsSpacesAvailableInsideAManuallyTypedDate() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var notes = EditNotes(window, item);
        Press(notes, Key.Tab);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)!;
        var date = new DateTime(2027, 3, 14);
        var month = date.ToString("MMMM", picker.Language.GetSpecificCulture());
        editor.SelectedText = month;
        editor.CaretIndex = editor.Text.Length;
        Press(editor, Key.Space);
        Assert.AreEqual(month + " ", editor.Text);
        Assert.AreEqual(editor.Text.Length, editor.CaretIndex);
        Assert.IsFalse(picker.IsDropDownOpen);
        editor.SelectedText = date.ToString("dd yyyy", picker.Language.GetSpecificCulture());
        Press(editor, Key.Enter, ModifierKeys.Control);
        Assert.AreEqual(date, item.DueDate);
        Assert.AreEqual(date, service.Load()[0].DueDate);
        Assert.IsFalse(item.IsExpanded);
    });

    [TestMethod]
    public void DateSpacePreservesSelectedInvalidInputAndShowsValidation() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        var notes = EditNotes(window, item);
        Press(notes, Key.Tab);
        var picker = Control<DatePicker>(window, item, "ItemDueDatePicker");
        var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)!;
        editor.SelectedText = "not a date";
        editor.SelectAll();
        Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Space, ModifierKeys.None).Handled);
        Assert.IsFalse(picker.IsDropDownOpen);
        Assert.AreEqual("not a date", editor.Text);
        Assert.AreSame(editor, Keyboard.FocusedElement);
        Assert.IsTrue(Control<TextBlock>(window, item, "DueDateErrorText").IsVisible);
        editor.Clear();
    });

    private static ChecklistItem Create(ChecklistWindow window, ChecklistWindowViewModel model)
    {
        Press((ListBox)window.FindName("ItemsList"), Key.N, ModifierKeys.Control);
        Pump();
        return model.Items.Last();
    }

    private static TextBox EditTitle(ChecklistWindow window, ChecklistItem item)
    {
        Row(window, item).Focus();
        Press(Row(window, item), Key.F2);
        return Control<TextBox>(window, item, "ItemTitleTextBox");
    }

    private static void EditTitleFromMenu(ChecklistWindow window, ChecklistItem item)
    {
        var root = Control<Grid>(window, item, "ChecklistItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, "Edit title"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static TextBox EditNotes(ChecklistWindow window, ChecklistItem item)
    {
        item.IsExpanded = true;
        var notes = Control<TextBox>(window, item, "NotesTextBox");
        notes.Focus();
        return notes;
    }

    private static ListBoxItem Row(ChecklistWindow window, ChecklistItem item)
    {
        window.UpdateLayout();
        return (ListBoxItem)((ListBox)window.FindName("ItemsList")).ItemContainerGenerator.ContainerFromItem(item);
    }

    private static T Control<T>(ChecklistWindow window, ChecklistItem item, string name) where T : DependencyObject =>
        VisualTreeHelpers.FindDescendant<T>(Row(window, item), name)!;

    private static void Press(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        KeyboardTestInput.Press(target, key, modifiers);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void MouseClick(UIElement target, int count = 1, bool pumpBeforeRelease = false)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(down, count);
        target.RaiseEvent(down);
        if (pumpBeforeRelease)
        {
            VisualTreeHelpers.FindAncestor<ListBoxItem>(target)?.Focus();
            Pump();
        }
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        });
    }

    private static void Repeat(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
        target.RaiseEvent(args);
        Assert.IsTrue(args.Handled);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

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
            // invalid test input must not cancel cleanup and keep focus in a later test.
            var list = (ListBox)window.FindName("ItemsList");
            foreach (var item in ((ChecklistWindowViewModel)window.DataContext).FilteredItems.ToArray())
            {
                var row = list.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
                var picker = VisualTreeHelpers.FindDescendant<DatePicker>(row, "ItemDueDatePicker");
                if (picker is null) continue;
                picker.IsDropDownOpen = false;
                VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)?.Clear();
            }
            window.Close();
            Pump();
        }
    });

    private sealed class RecordingService : IChecklistContentService
    {
        private readonly ChecklistContentService _store;
        internal bool FailSave { get; set; }
        internal Action? BeforeSave { get; set; }
        internal int SaveCount { get; private set; }

        internal RecordingService(string directory)
        {
            _store = new ChecklistContentService(directory);
            _store.SaveItems([new ChecklistItemState { Id = Guid.NewGuid(), Text = "alpha" },
                new ChecklistItemState { Id = Guid.NewGuid(), Text = "bravo" }]);
        }

        internal IReadOnlyList<ChecklistItemState> Load() => _store.LoadItems().Items;
        public ChecklistContentLoadResult LoadItems() => _store.LoadItems();
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => _store.SaveTabs(tabs);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            SaveCount++;
            var before = BeforeSave;
            BeforeSave = null;
            before?.Invoke();
            if (FailSave) throw new IOException("Simulated save failure.");
            return _store.SaveItems(items);
        }
    }
}
