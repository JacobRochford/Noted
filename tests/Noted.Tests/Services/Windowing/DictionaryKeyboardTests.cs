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
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
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
    public void HeaderActionFinishesNewEntryAndSavesEstablishedDraft() => WithWindow((window, model, service) =>
    {
        var existing = model.Items[0];
        var existingButton = Control<Button>(window, existing, "EditDefinitionButton");
        var created = Create(window, model);
        var createdButton = Control<Button>(window, created, "EditDefinitionButton");
        Assert.IsTrue(existingButton.IsVisible);
        Assert.AreEqual("Finish entry", createdButton.ToolTip);
        Assert.AreEqual("Done", ((StackPanel)createdButton.Content).Children.OfType<TextBlock>().Last().Text);

        Control<TextBox>(window, created, "WordTextBox").Text = "new word";
        Click(createdButton);
        Assert.IsFalse(created.IsEditing || created.IsExpanded);
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
    [DataRow(Key.Escape, "", "", true)]
    [DataRow(Key.Enter, "", "", true)]
    [DataRow(Key.Escape, " \t", "\r\n ", true)]
    [DataRow(Key.Enter, " \t", "\r\n ", true)]
    [DataRow(Key.Escape, "word", "", false)]
    [DataRow(Key.Enter, "word", "", false)]
    [DataRow(Key.Escape, "", "definition only", false)]
    [DataRow(Key.Enter, "", "definition only", false)]
    [DataRow(Key.Escape, "word", "definition", false)]
    [DataRow(Key.Enter, "word", "definition", false)]
    public void CompletionChecksBothFieldsAndNeverCreatesAnotherEntry(Key key, string wordText, string definitionText, bool empty) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Control<TextBox>(window, item, "WordTextBox").SelectedText = wordText;
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.SelectedText = definitionText;
        Press(definition, key, key == Key.Enter ? ModifierKeys.Control : ModifierKeys.None);
        Assert.AreEqual(!empty, model.Items.Contains(item));
        Assert.HasCount(empty ? 1 : 2, model.Items);
        Assert.HasCount(empty ? 1 : 2, service.Load());
        Assert.IsFalse(item.IsAutoSaving || item.IsEditing || item.IsExpanded);
        Assert.IsTrue(window.IsVisible);
        if (!empty)
        {
            Assert.AreEqual(wordText, service.Load().Last().Word);
            Assert.AreEqual(definitionText, service.Load().Last().Description);
        }
    });

    [TestMethod]
    [DataRow("ctrl enter")]
    [DataRow("escape")]
    [DataRow("hide")]
    [DataRow("shortcut")]
    [DataRow("x")]
    [DataRow("outside")]
    [DataRow("add")]
    public void FailedCreationCompletionKeepsItsEditorAndRetryUsesLatestText(string action) => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        Control<TextBox>(window, item, "WordTextBox").SelectedText = "word";
        Press((UIElement)Keyboard.FocusedElement, Key.Tab);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        definition.SelectedText = "definition";
        definition.Select(1, 3);
        service.FailSave = true;
        WithWarningDismissed(window, () =>
        {
            switch (action)
            {
                case "ctrl enter": Press(definition, Key.Enter, ModifierKeys.Control); break;
                case "escape": Press(definition, Key.Escape); break;
                case "hide": Assert.IsFalse(window.TryHide()); break;
                case "shortcut": WindowManager.Dictionary = window; WindowManager.HideDictionary(); break;
                case "x":
                    var hide = (Button)window.FindName("HideDictionaryButton");
                    PreviewClick(hide);
                    Assert.IsTrue(item.IsAutoSaving);
                    Click(hide);
                    break;
                case "outside":
                    var search = (TextBox)window.FindName("SearchTextBox");
                    PreviewClick(search);
                    search.Focus();
                    PreviewRelease(search);
                    Pump();
                    break;
                case "add": Click((Button)window.FindName("AddEntryButton")); break;
            }
        });
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
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
    public void FailedEmptyCreationCleanupDoesNotRemoveTheEditor() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        service.FailSave = true;
        WithWarningDismissed(window, () => Press((UIElement)Keyboard.FocusedElement, Key.Escape));
        Assert.IsTrue(model.Items.Contains(item));
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
        service.FailSave = false;
        Press((UIElement)Keyboard.FocusedElement, Key.Escape);
        Assert.IsFalse(model.Items.Contains(item));
        Assert.HasCount(1, service.Load());
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
        creation.DraftDefinition = "autosaved creation";
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
        Assert.IsTrue(creation.IsAutoSaving && creation.IsEditing && creation.IsExpanded);
        established.CancelEdit();
        Assert.IsTrue(window.TryHide());
        Assert.IsFalse(creation.IsAutoSaving || creation.IsEditing || creation.IsExpanded);
        window.ShowWindow();
        Pump();
        Assert.IsFalse(Keyboard.FocusedElement is TextBox { Name: "WordTextBox" or "DefinitionBox" });
        Assert.AreEqual("saved definition", service.Load()[0].Description);
        Assert.AreEqual("autosaved creation", service.Load()[1].Description);
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
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreSame(definition, Keyboard.FocusedElement);
        Press(definition, Key.Escape);
        Assert.IsTrue(model.Items.Contains(item));
        Assert.AreEqual("arrived during save", service.Load().Last().Description);
    });

    [TestMethod]
    public void BackupPreparationRetainsItsExistingCreationCompletionAndExpandedDetails() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        item.DraftDefinition = "definition only";
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.IsFalse(item.IsAutoSaving || item.IsEditing);
        Assert.IsTrue(item.IsExpanded);
        Assert.AreEqual("", service.Load().Last().Word);
        Assert.AreEqual("definition only", service.Load().Last().Description);
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
        Assert.IsTrue(item.IsAutoSaving && window.IsVisible);
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
    public void ANewEstablishedDraftDuringHideFlushBlocksHideBeforeCreationFinishes() => WithWindow((window, model, service) =>
    {
        var established = model.Items[0];
        Edit(window, established);
        var creation = Create(window, model);
        creation.DraftDefinition = "accepted creation";
        service.BeforeSave = () => established.DraftDefinition = "arrived during hide";
        WithWarningDismissed(window, () => Assert.IsFalse(window.TryHide()));
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(established.HasPendingEdit);
        Assert.AreEqual("arrived during hide", established.DraftDefinition);
        Assert.IsTrue(creation.IsAutoSaving && creation.IsEditing && creation.IsExpanded);
        Assert.AreEqual("saved definition", service.Load()[0].Description);
        established.CancelEdit();
        Assert.IsTrue(window.TryHide());
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
    public void OutsideClickFinishesOnlyCreationAndPreservesTheClickedHeader(bool populated) => WithWindow((window, model, service) =>
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
        Assert.AreEqual(populated, model.Items.Contains(creation));
        Assert.IsFalse(creation.IsAutoSaving || creation.IsExpanded);
        Assert.AreSame(Control<Button>(window, original, "WordHeaderButton"), Keyboard.FocusedElement);
        Assert.HasCount(populated ? 2 : 1, service.Load());
    });

    [TestMethod]
    public void DefinitionAndContextMenuDoNotPrematurelyFinishCreation() => WithWindow((window, model, service) =>
    {
        var item = Create(window, model);
        var definition = Control<TextBox>(window, item, "DefinitionBox");
        PreviewClick(definition);
        definition.Focus();
        Pump();
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
        Assert.IsFalse(item.ShowEditActions);
        var root = Control<Border>(window, item, "DictionaryItemRoot");
        root.ContextMenu.PlacementTarget = root;
        root.ContextMenu.IsOpen = true;
        Pump();
        var escape = KeyboardTestInput.Press((ItemsControl)window.FindName("ItemsList"), Key.Escape, ModifierKeys.None);
        Assert.IsFalse(escape.Handled, "The window must leave Escape to the open context menu.");
        Assert.IsTrue(item.IsAutoSaving && item.IsEditing && item.IsExpanded);
        Assert.IsTrue(window.IsVisible);
        root.ContextMenu.IsOpen = false;
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtrlNFinishesPreviousCreationWithoutPublishingAnEstablishedDraft(bool populated) => WithWindow((window, model, service) =>
    {
        var established = model.Items[0];
        Edit(window, established);
        established.DraftDefinition = "unsaved draft";
        var previous = Create(window, model);
        if (populated) previous.DraftDefinition = "definition only";
        var next = Create(window, model);
        Assert.AreNotSame(previous, next);
        Assert.IsTrue(next.IsEditing && next.IsAutoSaving && next.IsExpanded);
        Assert.AreSame(Control<TextBox>(window, next, "WordTextBox"), Keyboard.FocusedElement);
        Assert.AreEqual(populated, model.Items.Contains(previous));
        Assert.HasCount(populated ? 3 : 2, model.Items);
        Assert.HasCount(populated ? 2 : 1, service.Load());
        Assert.IsTrue(established.HasPendingEdit);
        Assert.AreEqual("saved definition", service.Load()[0].Description);
    });

    [TestMethod]
    [DataRow("header")]
    [DataRow("edit")]
    [DataRow("save")]
    public void OutsideCompletionWaitsForMouseUpAndPreservesTheIntendedButtonAction(string action) => WithWindow((window, model, service) =>
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
        Assert.IsTrue(creation.IsAutoSaving && creation.IsExpanded);
        Assert.IsTrue(button.IsVisible);
        Assert.IsTrue(button.IsDescendantOf(window));
        Assert.AreEqual(0, clicks);
        PreviewRelease(button);
        Click(button);
        Assert.AreEqual(1, clicks);
        Pump();
        Assert.AreEqual(1, clicks);
        Assert.IsFalse(creation.IsAutoSaving || creation.IsEditing || creation.IsExpanded);
        Assert.IsTrue(established.IsExpanded);
        Assert.AreEqual(action == "edit", established.IsEditing);
        if (action == "save") Assert.AreEqual("explicitly saved", service.Load()[0].Description);
        Assert.AreEqual("creation", service.Load().Last().Description);
    });

    [TestMethod]
    public void ReturningToTheCreationBeforeQueuedCompletionDoesNotInterruptTheNextGesture() => WithWindow((window, model, service) =>
    {
        var creation = Create(window, model);
        creation.DraftDefinition = "keep editing";
        var header = Control<Button>(window, model.Items[0], "WordHeaderButton");
        PreviewClick(header);
        PreviewRelease(header);
        Click(header);
        var definition = Control<TextBox>(window, creation, "DefinitionBox");
        PreviewClick(definition);
        definition.Focus();
        Pump();
        Assert.IsTrue(creation.IsAutoSaving && creation.IsExpanded);
        Assert.AreSame(definition, Keyboard.FocusedElement);
        PreviewRelease(definition);
        Pump();
        Assert.IsTrue(creation.IsAutoSaving && creation.IsExpanded);
        Assert.AreSame(definition, Keyboard.FocusedElement);
    });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void EmptyCreationHideDoesNotAttemptAnotherWriteAfterTheVerifiedSnapshot(bool group, bool previouslyAutosaved) => WithWindow((window, model, service) =>
    {
        var creation = Create(window, model);
        if (previouslyAutosaved)
        {
            creation.DraftDefinition = "temporarily populated";
            Assert.IsTrue(model.TryFlushPendingItems(out _));
        }
        creation.DraftDefinition = " \t";
        var before = service.SaveCount;
        service.FailAfterSaveCount = before + 1;
        WithWarningDismissed(window, () =>
        {
            if (group)
            {
                WindowManager.Dictionary = window;
                WindowManager.HideAll();
            }
            else Assert.IsTrue(window.TryHide());
        });
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(before + 1, service.SaveCount);
        Assert.IsFalse(model.HasPersistenceError);
        Assert.IsFalse(window.IsVisible || model.Items.Contains(creation));
        Assert.HasCount(1, service.Load());
        if (group)
        {
            WindowManager.ToggleNotedWindows();
            Assert.IsTrue(window.IsVisible);
        }
    });

    [TestMethod]
    public void EmptyCleanupStillSchedulesContentChangedDuringCollectionRemoval() => WithWindow((window, model, service) =>
    {
        var creation = Create(window, model);
        var established = model.Items[0];
        model.Items.CollectionChanged += (_, e) =>
        {
            if (e.OldItems?.Contains(creation) == true) established.Definition = "newer accepted content";
        };
        var before = service.SaveCount;
        Press((UIElement)Keyboard.FocusedElement, Key.Enter, ModifierKeys.Control);
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(before + 2, service.SaveCount);
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
