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
[TestCategory("DesktopInteraction")]
public sealed class DictionaryKeyboardTests
{
    [TestMethod]
    [DataRow(Key.S)]
    [DataRow(Key.Enter)]
    public void BackgroundHitKeepsDraftAndSelectionAndCommandsTargetThatEntry(Key saveKey) => WithWindow((window, model, service) =>
    {
        var existing = model.Items[0];
        existing.IsExpanded = true;
        var background = Control<Button>(window, existing, "ExpandedItemBackground");
        var outside = background.TranslatePoint(new Point(2, background.ActualHeight / 2), window);
        Assert.AreSame(background, VisualTreeHelpers.FindAncestor<Button>(window.InputHitTest(outside) as DependencyObject));
        var definition = VisualTreeHelpers.FindDescendant<MarkdownView>(Control<Border>(window, existing, "DictionaryItemRoot"))!;
        var inside = definition.TranslatePoint(new Point(2, definition.ActualHeight / 2), window);
        Assert.AreSame(definition, VisualTreeHelpers.FindAncestor<MarkdownView>(window.InputHitTest(inside) as DependencyObject));
        Click(background);
        Assert.IsFalse(existing.IsExpanded);

        Edit(window, existing);
        Control<TextBox>(window, existing, "WordTextBox").Text = "existing draft";
        var draft = Create(window, model);
        var word = Control<TextBox>(window, draft, "WordTextBox");
        word.Text = "new draft";
        word.Select(1, 3);
        Click(Control<Button>(window, draft, "ExpandedItemBackground"));
        var row = Control<Border>(window, draft, "DictionaryItemRoot");
        Assert.IsTrue(row.IsKeyboardFocused);
        Assert.IsTrue(draft.IsEditing && draft.IsExpanded && existing.IsEditing);
        Assert.IsTrue(word.IsInactiveSelectionHighlightEnabled);
        Assert.AreEqual(1, word.SelectionStart);
        Assert.AreEqual(3, word.SelectionLength);
        word.Focus();
        Assert.AreEqual(1, word.SelectionStart);
        Assert.AreEqual(3, word.SelectionLength);
        Click(Control<Button>(window, draft, "ExpandedItemBackground"));
        Press(row, saveKey, ModifierKeys.Control);
        Assert.AreEqual("new draft", service.Load().Last().Word);
        Assert.AreEqual(saveKey == Key.S, draft.IsExpanded);
        Assert.IsTrue(existing.IsEditing);
        Assert.AreEqual("original", service.Load().First().Word);
        Click(Control<Button>(window, existing, "ExpandedItemBackground"));
        Press(Control<Border>(window, existing, "DictionaryItemRoot"), Key.Escape);
        Assert.IsFalse(existing.IsEditing);
        Assert.AreEqual("original", existing.Word);
    });

    [TestMethod]
    public void CtrlNCreatesAndFocusesWordAndFieldNavigationKeepsSelectionAndMultilineInput() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        var word = Control<TextBox>(window, item, "WordTextBox");
        Assert.AreSame(word, Keyboard.FocusedElement);
        word.SelectedText = "new word";
        word.Select(1, 3);
        Press(word, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        Assert.AreSame(definition, Keyboard.FocusedElement);
        definition.SelectedText = "first line";
        Press(definition, Key.Enter);
        definition.SelectedText = "second line";
        StringAssert.Contains(definition.Text, Environment.NewLine);
        Assert.IsTrue(item.IsNew && item.IsEditing && item.IsExpanded);
        Press(definition, Key.Tab, ModifierKeys.Shift);
        Assert.AreSame(word, Keyboard.FocusedElement);
        Assert.AreEqual(1, word.SelectionStart);
        Assert.AreEqual(3, word.SelectionLength);
        Press(word, Key.Enter, ModifierKeys.Control);
        Assert.HasCount(2, model.Items);
        Assert.IsFalse(item.IsEditing || item.IsAutoSaving || item.IsExpanded);
        Assert.AreEqual(item.Definition, service.Load().Last().Description);
        Assert.IsTrue(Keyboard.FocusedElement is Button);
    });

    [TestMethod]
    public void HeaderActionSavesNewAndEstablishedDrafts() => WithWindow((window, model, service) =>
    {
        var existing = model.Items[0];
        var existingButton = Control<Button>(window, existing, "EditDefinitionButton");
        var created = Create(window, model);
        var createdButton = Control<Button>(window, created, "EditDefinitionButton");
        Assert.IsTrue(existingButton.IsVisible);
        Assert.AreEqual("Save changes", createdButton.ToolTip);
        Assert.AreEqual("Save", ((StackPanel)createdButton.Content).Children.OfType<TextBlock>().Last().Text);

        Control<TextBox>(window, created, "WordTextBox").Text = "new word";
        Click(createdButton);
        Assert.IsFalse(created.IsEditing);
        Assert.AreEqual("new word", service.Load().Last().Word);

        Edit(window, existing);
        existingButton = Control<Button>(window, existing, "EditDefinitionButton");
        Assert.AreEqual("Save changes", existingButton.ToolTip);
        Assert.AreEqual("Save", ((StackPanel)existingButton.Content).Children.OfType<TextBlock>().Last().Text);
        Control<TextBox>(window, existing, "WordTextBox").Text = "saved word";
        Click(existingButton);
        Assert.IsFalse(existing.IsEditing);
        Assert.IsTrue(existing.IsExpanded);
        Assert.AreEqual("saved word", service.Load()[0].Word);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HeaderActionFitsAtMinimumWindowWidth(bool creating) => WithWindow((window, model, _) =>
    {
        window.Width = window.MinWidth;
        var item = creating ? Create(window, model) : model.Items[0];
        if (!creating) Edit(window, item);
        var root = Control<Border>(window, item, "DictionaryItemRoot");
        var button = Control<Button>(window, item, "EditDefinitionButton");
        window.UpdateLayout();

        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        Assert.IsTrue(button.ActualWidth > 30, "The action must grow to fit its label.");
        Assert.IsTrue(bounds.Right <= root.ActualWidth + 0.5, "The action must stay inside the item row.");
    });

    [TestMethod]
    [DataRow(Key.Escape, "", "")]
    [DataRow(Key.Escape, "word", "definition")]
    [DataRow(Key.Enter, "", "definition only")]
    [DataRow(Key.Enter, "word", "definition")]
    public void NewEntryKeyboardCommandsSaveOrDiscardExplicitly(Key key, string wordText, string definitionText) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Control<TextBox>(window, item, "WordTextBox").SelectedText = wordText;
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.SelectedText = definitionText;
        Press(definition, key, key == Key.Enter ? ModifierKeys.Control : ModifierKeys.None);
        var saved = key == Key.Enter;
        Assert.AreEqual(saved, model.Items.Contains(item));
        Assert.HasCount(saved ? 2 : 1, model.Items);
        Assert.HasCount(saved ? 2 : 1, service.Load());
        Assert.IsFalse(item.IsEditing);
        Assert.IsTrue(window.IsVisible);
        if (saved)
        {
            Assert.AreEqual(wordText, service.Load().Last().Word);
            Assert.AreEqual(definitionText, service.Load().Last().Description);
        }
    });

    [TestMethod]
    public void FailedNewEntrySaveKeepsItsEditorAndRetryUsesLatestText() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Control<TextBox>(window, item, "WordTextBox").SelectedText = "word";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.SelectedText = "definition";
        definition.Select(1, 3);
        service.FailSave = true;
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(item.IsNew && item.IsEditing && item.IsExpanded);
        Assert.HasCount(2, model.Items);
        Assert.AreSame(Control<TextBox>(window, item, "DefinitionBox"), Keyboard.FocusedElement);
        Assert.AreEqual(1, definition.SelectionStart);
        Assert.AreEqual(3, definition.SelectionLength);
        service.FailSave = false;
        definition.SelectedText = "latest";
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsEditing || item.IsAutoSaving || item.IsExpanded);
        Assert.AreEqual(item.Definition, service.Load().Last().Description);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EstablishedDraftKeepsSaveCancelSemanticsAndOrdinaryFocusLossDoesNotPublishIt(bool blank) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        if (blank)
        {
            item.Word = item.Definition = "";
            Assert.IsTrue(window.TryFlushPendingContent(out _));
        }
        Edit(window, item);
        var word = Control<TextBox>(window, item, "WordTextBox");
        word.SelectAll();
        word.SelectedText = "draft word";
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.Text = "draft definition";
        var search = (TextBox)window.FindName("SearchTextBox");
        PreviewClick(search);
        search.Focus();
        PreviewRelease(search);
        Pump();
        Assert.IsTrue(item.HasPendingEdit && item.ShowEditActions);
        Assert.AreEqual(blank ? "" : "original", item.Word);
        Assert.AreEqual(blank ? "" : "saved definition", service.Load()[0].Description);
        definition.Focus();
        Press(definition, Key.Escape);
        Assert.IsFalse(item.IsEditing);
        Assert.IsTrue(item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual(item.Word, item.DraftWord);
        Assert.AreEqual(item.Definition, item.DraftDefinition);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtrlEnterExplicitlySavesEstablishedDraftAndCollapsesEvenWhenBothFieldsAreBlank(bool blank) => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        Edit(window, item);
        Control<TextBox>(window, item, "WordTextBox").Text = blank ? "" : "accepted word";
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.Text = blank ? "" : "accepted definition";
        definition.Focus();
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsEditing || item.IsExpanded);
        Assert.HasCount(1, model.Items);
        Assert.AreEqual(item.Word, service.Load()[0].Word);
        Assert.AreEqual(item.Definition, service.Load()[0].Description);
    });

    [TestMethod]
    public void FailedEstablishedSaveKeepsDraftAndCancelNeverRetriesIt() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        Edit(window, item);
        var word = Control<TextBox>(window, item, "WordTextBox");
        word.SelectedText = "failed draft";
        service.FailSave = true;
        Press(word, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(item.HasPendingEdit && item.IsExpanded);
        Assert.AreSame(word, Keyboard.FocusedElement);
        Assert.AreEqual("original", item.Word);
        Press(word, Key.Escape);
        service.FailSave = false;
        Assert.IsTrue(window.TryFlushPendingContent(out _));
        Assert.AreEqual("original", service.Load()[0].Word);
    });

    [TestMethod]
    [DataRow("x")]
    [DataRow("shortcut")]
    public void UnsavedEstablishedDraftBlocksHideWithoutFinishingAnotherCreation(string route) => WithWindow((window, model, service) =>
    {
        var established = model.Items[0];
        Edit(window, established);
        established.DraftDefinition = "unsaved draft";
        var creation = Create(window, model);
        creation.DraftDefinition = "unsaved creation";
        WithWarningDismissed(window, () =>
        {
            if (route == "x")
            {
                var hide = (Button)window.FindName("HideDictionaryButton");
                PreviewClick(hide);
                Click(hide);
            }
            else
            {
                WindowManager.Dictionary = window;
                WindowManager.HideDictionary();
            }
        });
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(established.HasPendingEdit);
        Assert.IsTrue(creation.IsNew && creation.IsEditing && creation.IsExpanded);
        Assert.IsTrue(model.TryCancelEntry(established, out _));
        WithWarningDismissed(window, () => Assert.IsFalse(window.TryHide()));
        Assert.IsTrue(creation.IsEditing && creation.IsExpanded);
        Assert.AreEqual("saved definition", service.Load().Single().Description);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ContentArrivingDuringSaveKeepsCreationActiveUntilAnotherCompletion(bool initiallyEmpty) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        if (!initiallyEmpty) item.DraftDefinition = "requested";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        service.BeforeSave = () => item.DraftDefinition = "arrived during save";
        WithWarningDismissed(window, () => Press(definition, Key.Enter, ModifierKeys.Control));
        Assert.IsTrue(item.IsEditing && item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreSame(definition, Keyboard.FocusedElement);
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual("arrived during save", service.Load().Last().Description);
    });

    [TestMethod]
    public void BackupPreparationPreservesCreationWithoutCommittingIt() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        item.DraftDefinition = "definition only";
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.IsTrue(item.IsNew && item.IsEditing);
        Assert.IsTrue(item.IsExpanded);
        Assert.AreEqual("original", service.Load().Single().Word);
        Assert.AreEqual("saved definition", service.Load().Single().Description);
    });

    [TestMethod]
    public void SearchAndContextMenuEscapeDoNotHideOrCompleteCreationAndRepeatedEscapeDoesNotHide() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Press((UIElement)Keyboard.FocusedElement, Key.F, ModifierKeys.Control);
        var search = (TextBox)window.FindName("SearchTextBox");
        search.Text = "missing";
        Press(search, Key.Escape);
        Assert.AreEqual("", model.SearchText);
        Assert.IsTrue(item.IsNew && window.IsVisible);
        var root = Control<Border>(window, item, "DictionaryItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.IsOpen = true;
        Press(Control<TextBox>(window, item, "WordTextBox"), Key.N, ModifierKeys.Control);
        Assert.HasCount(2, model.Items);
        root.ContextMenu.IsOpen = false;
        var word = Control<TextBox>(window, item, "WordTextBox");
        word.Focus();
        Press(word, Key.Escape);
        var target = (UIElement)Keyboard.FocusedElement;
        var repeat = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repeat, [true]);
        target.RaiseEvent(repeat);
        Assert.IsTrue(repeat.Handled);
        Assert.IsTrue(window.IsVisible);
        Press(target, Key.Escape);
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public void SearchCannotRemoveCreationAndFieldTypingDoesNotRefreshItsEditor() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        model.SearchText = "no match";
        var word = Control<TextBox>(window, item, "WordTextBox");
        word.Focus();
        word.SelectedText = "new word";
        Assert.AreSame(word, Control<TextBox>(window, item, "WordTextBox"));
        Assert.AreSame(word, Keyboard.FocusedElement);
        Press(word, Key.Tab);
        Assert.AreSame(Control<TextBox>(window, item, "DefinitionBox"), Keyboard.FocusedElement);
        Assert.IsTrue(model.FilteredItems.Contains(item));
    });

    [TestMethod]
    public void LaterEstablishedDraftChangesStayEditableAfterTheRequestedValuesWereSaved() => WithWindow((window, model, service) =>
    {
        var item = model.Items[0];
        Edit(window, item);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.Focus();
        definition.SelectedText = "requested";
        service.BeforeSave = () => item.DraftDefinition = "later draft";
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(item.HasPendingEdit && item.IsEditing && item.IsExpanded);
        Assert.AreSame(definition, Keyboard.FocusedElement);
        Assert.AreEqual("later draft", item.DraftDefinition);
        Assert.AreNotEqual(item.DraftDefinition, service.Load()[0].Description);
        Press(definition, Key.Enter, ModifierKeys.Control);
        Assert.IsFalse(item.IsEditing || item.IsExpanded);
        Assert.AreEqual("later draft", service.Load()[0].Description);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutsideClickPreservesCreationAndTheClickedHeader(bool populated) => WithWindow((window, model, service) =>
    {
        var original = model.Items[0];
        var creation = Create(window, model);
        if (populated) creation.DraftDefinition = "definition only";
        var header = Control<Button>(window, original, "WordHeaderButton");
        PreviewClick(header);
        header.Focus();
        PreviewRelease(header);
        Click(header);
        Pump();
        Assert.IsTrue(original.IsExpanded);
        Assert.IsFalse(original.IsEditing);
        Assert.IsTrue(model.Items.Contains(creation));
        Assert.IsTrue(creation.IsEditing && creation.IsExpanded);
        Assert.AreSame(Control<Button>(window, original, "WordHeaderButton"), Keyboard.FocusedElement);
        Assert.HasCount(1, service.Load());
    });

    [TestMethod]
    public void DefinitionAndContextMenuDoNotPrematurelyFinishCreation() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        PreviewClick(definition);
        definition.Focus();
        Pump();
        Assert.IsTrue(item.IsNew && item.IsEditing && item.IsExpanded);
        Assert.IsTrue(item.ShowEditActions);
        var root = Control<Border>(window, item, "DictionaryItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.IsOpen = true;
        Pump();
        var escape = KeyboardTestInput.Press((ItemsControl)window.FindName("ItemsList"), Key.Escape, ModifierKeys.None);
        Assert.IsFalse(escape.Handled, "The window must leave Escape to the open context menu.");
        Assert.IsTrue(item.IsNew && item.IsEditing && item.IsExpanded);
        Assert.IsTrue(window.IsVisible);
        root.ContextMenu.IsOpen = false;
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtrlNPreservesPreviousCreationAndAnEstablishedDraft(bool populated) => WithWindow((window, model, service) =>
    {
        var established = model.Items[0];
        Edit(window, established);
        established.DraftDefinition = "unsaved draft";
        var previous = Create(window, model);
        if (populated) previous.DraftDefinition = "definition only";
        var next = Create(window, model);
        Assert.AreNotSame(previous, next);
        Assert.IsTrue(next.IsEditing && next.IsNew && next.IsExpanded);
        Assert.AreSame(Control<TextBox>(window, next, "WordTextBox"), Keyboard.FocusedElement);
        Assert.IsTrue(model.Items.Contains(previous));
        Assert.HasCount(3, model.Items);
        Assert.HasCount(1, service.Load());
        Assert.IsTrue(established.HasPendingEdit);
        Assert.AreEqual("saved definition", service.Load()[0].Description);
    });

    [TestMethod]
    [DataRow("header")]
    [DataRow("edit")]
    [DataRow("save")]
    public void ReferenceClicksPreserveCreationAndTheIntendedButtonAction(string action) => WithWindow((window, model, service) =>
    {
        var established = model.Items[0];
        if (action == "save")
        {
            Edit(window, established);
            established.DraftDefinition = "explicitly saved";
        }
        var creation = Create(window, model);
        creation.DraftDefinition = "creation";
        var button = action == "save"
            ? VisualTreeHelpers.FindDescendant<StackPanel>(Control<Border>(window, established, "DictionaryItemRoot"), "EditActionsPanel")!.Children.OfType<Button>().First()
            : Control<Button>(window, established, action == "edit" ? "EditDefinitionButton" : "WordHeaderButton");
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        PreviewClick(button);
        button.Focus();
        Pump();
        Assert.IsTrue(creation.IsNew && creation.IsExpanded);
        Assert.IsTrue(button.IsVisible);
        Assert.IsTrue(button.IsDescendantOf(window));
        Assert.AreEqual(0, clicks);
        PreviewRelease(button);
        Click(button);
        Assert.AreEqual(1, clicks);
        Pump();
        Assert.AreEqual(1, clicks);
        Assert.IsTrue(creation.IsEditing && creation.IsExpanded);
        Assert.IsTrue(established.IsExpanded);
        Assert.AreEqual(action == "edit", established.IsEditing);
        if (action == "save") Assert.AreEqual("explicitly saved", service.Load()[0].Description);
        Assert.HasCount(1, service.Load());
    });

    [TestMethod]
    public void CancelStillSchedulesContentChangedDuringCollectionRemoval() => WithWindow((window, model, service) =>
    {
        var creation = Create(window, model);
        var established = model.Items[0];
        model.Items.CollectionChanged += (_, e) =>
        {
            if (e.OldItems?.Contains(creation) == true) established.Definition = "newer accepted content";
        };
        var before = service.SaveCount;
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(before + 1, service.SaveCount);
        Assert.AreEqual("newer accepted content", service.Load()[0].Description);
    });

    private static DictionaryItem Create(DictionaryWindow window, DictionaryWindowViewModel model)
    {
        Press((ItemsControl)window.FindName("ItemsList"), Key.N, ModifierKeys.Control);
        Pump();
        return model.Items.Last();
    }

    private static void Edit(DictionaryWindow window, DictionaryItem item)
    {
        Click(Control<Button>(window, item, "EditDefinitionButton"));
        Pump();
    }

    private static T Control<T>(DictionaryWindow window, DictionaryItem item, string name) where T : DependencyObject
    {
        window.UpdateLayout();
        return VisualTreeHelpers.FindDescendant<T>(((ItemsControl)window.FindName("ItemsList")).ItemContainerGenerator.ContainerFromItem(item), name)!;
    }

    private static void Press(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None) => KeyboardTestInput.Press(target, key, modifiers);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PreviewClick(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    {
        RoutedEvent = Mouse.PreviewMouseDownEvent
    });
    private static void PreviewRelease(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    {
        RoutedEvent = Mouse.PreviewMouseUpEvent
    });
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void WithWarningDismissed(DictionaryWindow window, Action action)
    {
        var dismiss = window.Dispatcher.BeginInvoke(() =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<AppDialog>().ToArray()) dialog.Close();
        }, DispatcherPriority.ApplicationIdle);
        try { action(); }
        finally { dismiss.Abort(); }
    }

    private static void WithWindow(Action<DictionaryWindow, DictionaryWindowViewModel, RecordingService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingService(directory.Path);
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service);
        var previous = WindowManager.Dictionary;
        try
        {
            window.Show();
            window.Activate();
            Pump();
            action(window, (DictionaryWindowViewModel)window.DataContext, service);
        }
        finally
        {
            service.FailSave = false;
            service.FailAfterSaveCount = null;
            service.BeforeSave = null;
            foreach (var item in ((DictionaryWindowViewModel)window.DataContext).Items.Where(item => item.HasPendingEdit).ToArray()) item.CancelEdit();
            WindowManager.Dictionary = previous;
            window.Close();
            Pump();
        }
    });

    internal sealed class RecordingService : IDictionaryContentService
    {
        private readonly DictionaryContentService _store;
        internal bool FailSave { get; set; }
        internal int? FailAfterSaveCount { get; set; }
        internal Action? BeforeSave { get; set; }
        internal int SaveCount { get; private set; }
        internal RecordingService(string path)
        {
            _store = new DictionaryContentService(path);
            _store.SaveItems([new DictionaryItemState { Word = "original", Description = "saved definition" }]);
        }
        internal IReadOnlyList<DictionaryItemState> Load() => _store.LoadItems().Items;
        public DictionaryContentLoadResult LoadItems() => _store.LoadItems();
        public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
        {
            SaveCount++;
            var before = BeforeSave;
            BeforeSave = null;
            before?.Invoke();
            if (FailSave || SaveCount > FailAfterSaveCount) throw new IOException("Simulated save failure.");
            return _store.SaveItems(items);
        }
    }
}
