using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
public sealed class NoteEditorNavigationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptyFindUsesTheActivePaneWordAndKeepsAnExistingQuery(bool split) => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.txt", "primary"));
        if (split)
        {
            editor.OpenNote(CreateNote(files, "second.txt", "secondary"));
            editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        }
        var box = (TextBox)editor.FindName(split ? "SecondaryEditorTextBox" : "EditorTextBox");
        var input = (TextBox)editor.FindName("FindTextBox");
        box.Select(2, 0);
        var expected = box.Text;
        Assert.IsTrue(KeyboardTestInput.Press(box, Key.F, ModifierKeys.Control).Handled);
        Assert.AreEqual(expected, input.Text);
        Assert.AreEqual(2, box.CaretIndex);
        Assert.AreEqual(0, box.SelectionLength);
        input.Text = "previous";
        KeyboardTestInput.Press(box, Key.F, ModifierKeys.Control);
        Assert.AreEqual("previous", input.Text);
        Assert.AreEqual(expected, box.Text);
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlainTextPreviewIsOptInAndKeepsEditingStateInEitherPane(bool split) => WithEditor((editor, _, _, files) =>
    {
        var path = CreateNote(files, "source.txt", "# heading\n**bold**");
        editor.OpenNote(path);
        if (split)
        {
            editor.OpenNote(CreateNote(files, "other.txt", "other"));
            editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        }
        var document = editor.Workspace.CurrentDocument!;
        var box = (TextBox)editor.FindName(split ? "SecondaryEditorTextBox" : "EditorTextBox");
        var preview = (FlowDocumentScrollViewer)editor.FindName(split ? "SecondaryMarkdownPreview" : "MarkdownPreview");
        Assert.AreEqual(MarkdownPreviewMode.Off, document.MarkdownPreviewMode);
        Assert.AreEqual(Visibility.Collapsed, preview.Visibility);
        box.Select(box.Text.Length, 0);
        box.SelectedText = " edited";
        box.Select(2, 4);
        var caret = box.CaretIndex;
        var source = box.Text;
        var menu = (MenuItem)editor.FindName("HeaderMarkdownMenuItem");
        Assert.AreEqual(Visibility.Visible, menu.Visibility);
        Assert.IsTrue(KeyboardTestInput.Press(box, Key.V, ModifierKeys.Control | ModifierKeys.Shift).Handled);
        Assert.AreEqual(MarkdownPreviewMode.Preview, document.MarkdownPreviewMode);
        Assert.AreEqual(Visibility.Visible, preview.Visibility);
        KeyboardTestInput.Press(preview, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        foreach (var mode in new[] { MarkdownPreviewMode.SideBySide, MarkdownPreviewMode.Off })
        {
            menu.Items.OfType<MenuItem>().Single(item => (MarkdownPreviewMode)item.Tag == mode)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual(mode, document.MarkdownPreviewMode);
        }
        Assert.AreEqual(source, box.Text);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(4, box.SelectionLength);
        Assert.AreEqual(caret, box.CaretIndex);
        Assert.IsTrue(document.IsDirty);
        Assert.AreEqual("# heading\n**bold**", File.ReadAllText(path));
        box.Undo();
        Assert.AreEqual("# heading\n**bold**", box.Text);
        KeyboardTestInput.Press(box, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        KeyboardTestInput.Press(preview, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.IsTrue(box.CanRedo);
        box.Redo();
        Assert.AreEqual(source, box.Text);
        Assert.AreEqual(path, document.FilePath);
    });

    [TestMethod]
    public void NewPlainTextNoteKeepsPreviewAndSourceFormatOnFirstSave() => WithEditor((editor, _, _, files) =>
    {
        var path = CreateNote(files, "generated.txt", "");
        editor.OpenCreatedNote(path, usesGeneratedName: true, initialContent: "");
        var document = editor.Workspace.CurrentDocument!;
        Assert.AreEqual(MarkdownPreviewMode.Off, document.MarkdownPreviewMode);
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.SelectedText = "# unsaved\n**source**";
        box.Select(2, 3);
        KeyboardTestInput.Press(box, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        var destination = Path.Combine(files.NotesDirectory, "named.txt");
        editor.NoteRenameRequested += (_, request) =>
        {
            Assert.IsTrue(request.IsFirstSave);
            File.Move(request.FilePath, destination);
            request.NewFilePath = destination;
        };
        KeyboardTestInput.Press((FlowDocumentScrollViewer)editor.FindName("MarkdownPreview"), Key.S, ModifierKeys.Control);
        Assert.AreEqual(destination, document.FilePath);
        Assert.AreEqual("# unsaved\n**source**", File.ReadAllText(destination));
        Assert.IsFalse(document.IsDirty);
        Assert.IsFalse(document.UsesGeneratedName);
        Assert.AreEqual(MarkdownPreviewMode.Preview, document.MarkdownPreviewMode);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(3, box.SelectionLength);
        box.Undo();
        Assert.AreEqual("", box.Text);
        box.Redo();
        Assert.AreEqual("# unsaved\n**source**", box.Text);
    });

    [TestMethod]
    public void SaveAsReconcilesPreviewEligibilityWithoutChangingSourceOrUndo() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "source.txt", "# source"));
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.Select(box.Text.Length, 0);
        box.SelectedText = " edited";
        box.Select(1, 3);
        KeyboardTestInput.Press(box, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        foreach (var extension in new[] { ".md", ".txt", ".json", ".TXT" })
        {
            var path = Path.Combine(files.NotesDirectory, "saved" + extension);
            Assert.IsTrue(editor.TrySaveCurrentNoteAs(dialog => { dialog.FileName = path; return true; }));
            var eligible = extension != ".json";
            Assert.AreEqual(path, editor.Workspace.CurrentDocument!.FilePath);
            Assert.AreEqual("# source edited", File.ReadAllText(path));
            Assert.AreEqual(eligible ? Visibility.Visible : Visibility.Collapsed,
                ((MenuItem)editor.FindName("HeaderMarkdownMenuItem")).Visibility);
            Assert.AreEqual(eligible ? Visibility.Visible : Visibility.Collapsed,
                ((FlowDocumentScrollViewer)editor.FindName("MarkdownPreview")).Visibility);
            Assert.AreEqual(1, box.SelectionStart);
            Assert.AreEqual(3, box.SelectionLength);
        }
        box.Undo();
        Assert.AreEqual("# source", box.Text);
    });

    [TestMethod]
    public void SwitchingPlainTextDocumentsRestoresTheirOptInPreviewModes() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "first.txt", "# first");
        var second = CreateNote(files, "second.txt", "# second");
        editor.OpenNote(first);
        KeyboardTestInput.Press((TextBox)editor.FindName("EditorTextBox"), Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        editor.OpenNote(second);
        Assert.AreEqual(MarkdownPreviewMode.Off, editor.Workspace.CurrentDocument!.MarkdownPreviewMode);
        Assert.AreEqual(Visibility.Collapsed, ((FlowDocumentScrollViewer)editor.FindName("MarkdownPreview")).Visibility);
        editor.OpenNote(first);
        Assert.AreEqual(MarkdownPreviewMode.Preview, editor.Workspace.CurrentDocument!.MarkdownPreviewMode);
        Assert.AreEqual(Visibility.Visible, ((FlowDocumentScrollViewer)editor.FindName("MarkdownPreview")).Visibility);
    });

    [TestMethod]
    public void WheelZoomIsSharedAcrossSplitChangesAndPreservesEditingState() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "zoom-a.txt", string.Join("\n", Enumerable.Repeat(new string('a', 100), 100))));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var initialSize = primary.FontSize;
        Assert.IsTrue(Wheel(primary, 120, ModifierKeys.Control).Handled);
        Assert.AreEqual(initialSize + 1, primary.FontSize);
        Assert.AreEqual(primary.FontSize, secondary.FontSize);
        editor.OpenNote(CreateNote(files, "zoom-b.txt", "second note"));
        editor.OpenNote(Path.Combine(files.NotesDirectory, "zoom-a.txt"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        editor.UpdateLayout();
        primary.Select(1, 3);
        secondary.Select(0, 0);
        secondary.SelectedText = "edited ";
        secondary.Select(2, 4);
        var primaryText = primary.Text;
        var secondaryText = secondary.Text;
        var gutter = (LineNumberGutter)editor.FindName("PrimaryLineNumbers");
        var oldWidth = gutter.Width;
        var oldLineCount = primary.LineCount;
        Assert.IsTrue(Wheel(secondary, 1200, ModifierKeys.Control).Handled);
        editor.UpdateLayout();
        Assert.AreEqual(initialSize + 11, primary.FontSize);
        Assert.AreEqual(primary.FontSize, secondary.FontSize);
        Assert.IsTrue(gutter.Width > oldWidth);
        Assert.IsTrue(primary.LineCount > oldLineCount, "Larger text should wrap onto more visual lines.");
        Assert.AreEqual(primaryText, primary.Text);
        Assert.AreEqual(secondaryText, secondary.Text);
        Assert.AreEqual(1, primary.SelectionStart);
        Assert.AreEqual(3, primary.SelectionLength);
        Assert.AreEqual(2, secondary.SelectionStart);
        Assert.AreEqual(4, secondary.SelectionLength);
        secondary.Undo();
        Assert.AreEqual("second note", secondary.Text);
        secondary.Redo();
        Assert.AreEqual(secondaryText, secondary.Text);
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        Wheel(primary, -120, ModifierKeys.Control);
        Assert.AreEqual(initialSize + 10, secondary.FontSize);
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        Assert.AreEqual(primary.FontSize, secondary.FontSize);
    });

    [TestMethod]
    public void WheelZoomUsesProportionalSmallDeltasAndClampsWithoutAccumulatingOvershoot() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "zoom.txt", "unchanged"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var initialSize = primary.FontSize;
        for (var i = 0; i < 8; i++) Wheel(primary, 15, ModifierKeys.Control);
        Assert.AreEqual(initialSize + 1, primary.FontSize, 0.0001);
        Wheel(primary, -120, ModifierKeys.Control);
        Assert.AreEqual(initialSize, primary.FontSize, 0.0001);
        Wheel(primary, 12000, ModifierKeys.Control);
        Assert.AreEqual(48, primary.FontSize);
        Wheel(primary, -15, ModifierKeys.Control);
        Assert.AreEqual(47.875, primary.FontSize);
        Wheel(primary, -12000, ModifierKeys.Control);
        Assert.AreEqual(8, primary.FontSize);
        Wheel(primary, 15, ModifierKeys.Control);
        Assert.AreEqual(8.125, primary.FontSize);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(primary.CanUndo);
    });

    [TestMethod]
    public void WheelZoomDoesNotCarryIntoAnotherEditorWindow() => WithEditor((editor, directory, settings, files) =>
    {
        editor.OpenNote(CreateNote(files, "zoom.txt", "unchanged"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var initialSize = primary.FontSize;
        Wheel(primary, 1200, ModifierKeys.Control);
        Assert.IsTrue(editor.TryPrepareForClose());
        var reopened = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
            new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path), files);
        try
        {
            reopened.Show();
            Assert.AreEqual(initialSize, ((TextBox)reopened.FindName("EditorTextBox")).FontSize);
            Assert.AreEqual(initialSize, ((TextBox)reopened.FindName("SecondaryEditorTextBox")).FontSize);
        }
        finally
        {
            reopened.TryPrepareForClose();
            reopened.Close();
        }
    });

    [TestMethod]
    [DataRow(ModifierKeys.None)]
    [DataRow(ModifierKeys.Shift)]
    [DataRow(ModifierKeys.Alt)]
    [DataRow(ModifierKeys.Control | ModifierKeys.Shift)]
    [DataRow(ModifierKeys.Control | ModifierKeys.Alt)]
    public void WheelZoomLeavesOtherModifiersAndSearchInputsAlone(ModifierKeys modifiers) => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "zoom.txt", "unchanged"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var size = primary.FontSize;
        Assert.IsFalse(Wheel(primary, 15, modifiers).Handled);
        Assert.AreEqual(size, primary.FontSize);
        var search = (TextBox)editor.FindName("FindTextBox");
        Wheel(search, 120, ModifierKeys.Control);
        Assert.AreEqual(size, primary.FontSize);
    });

    private static MouseWheelEventArgs Wheel(UIElement target, int delta, ModifierKeys modifiers) => WithMouseModifiers(modifiers, () =>
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent
        };
        target.RaiseEvent(args);
        return args;
    });

    private static T WithMouseModifiers<T>(ModifierKeys modifiers, Func<T> dispatch)
    {
        var original = new byte[256];
        Assert.IsTrue(GetKeyboardState(original));
        var state = (byte[])original.Clone();
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            state[key] = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) state[0x11] = state[0xA2] = 0x80;
        if (modifiers.HasFlag(ModifierKeys.Shift)) state[0x10] = state[0xA0] = 0x80;
        if (modifiers.HasFlag(ModifierKeys.Alt)) state[0x12] = state[0xA4] = 0x80;
        try
        {
            Assert.IsTrue(SetKeyboardState(state));
            Assert.AreEqual(modifiers, Keyboard.Modifiers);
            return dispatch();
        }
        finally { Assert.IsTrue(SetKeyboardState(original)); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    private static MouseButtonEventArgs TabMouseDown(UIElement target, ModifierKeys modifiers,
        MouseButton button = MouseButton.Left) => WithMouseModifiers(modifiers, () =>
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        target.RaiseEvent(args);
        return args;
    });

    private static ListBoxItem TabItem(NoteEditorWindow editor, int index)
    {
        editor.UpdateLayout();
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        return (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromIndex(index);
    }

    [TestMethod]
    public void AltClickOpensSecondaryAndPreservesPrimaryEditingState() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "left.txt", "left"));
        editor.OpenNote(CreateNote(files, "right.txt", "right"));
        var primaryDocument = editor.Workspace.ActiveDocument;
        var primary = (TextBox)editor.FindName("EditorTextBox");
        primary.Select(primary.Text.Length, 0);
        primary.SelectedText = " edited";
        primary.Select(1, 3);
        var caret = primary.CaretIndex;

        Assert.IsTrue(TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt).Handled);

        Assert.AreSame(primaryDocument, editor.Workspace.ActiveDocument);
        Assert.AreSame(editor.Workspace.Documents[0], editor.Workspace.SecondaryDocument);
        Assert.AreEqual("right edited", primary.Text);
        Assert.AreEqual(1, primary.SelectionStart);
        Assert.AreEqual(3, primary.SelectionLength);
        Assert.AreEqual(caret, primary.CaretIndex);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        Assert.IsTrue(secondary.IsKeyboardFocused);
        Assert.AreEqual("left", secondary.Text);
        Assert.AreSame(editor.Workspace.SecondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.IsNull(typeof(NoteEditorWindow).GetField("_draggedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        primary.Undo();
        Assert.AreEqual("right", primary.Text);
        primary.Redo();
        Assert.AreEqual("right edited", primary.Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AltClickVisibleDocumentsKeepsSelectionsCaretsAndUndoRedo(bool leaveRedoPending) => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "left.txt", "left"));
        editor.OpenNote(CreateNote(files, "right.txt", "right"));
        TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt);
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        foreach (var box in new[] { primary, secondary })
        {
            box.Select(box.Text.Length, 0);
            box.SelectedText = " edited";
            if (leaveRedoPending) box.Undo();
            box.Select(1, 2);
        }
        var primaryCaret = primary.CaretIndex;
        var secondaryCaret = secondary.CaretIndex;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            Assert.IsTrue(TabMouseDown(TabItem(editor, 1), ModifierKeys.Alt).Handled);
            Assert.IsTrue(primary.IsKeyboardFocused);
            Assert.IsTrue(TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt).Handled);
            Assert.IsTrue(secondary.IsKeyboardFocused);
        }
        Assert.AreSame(editor.Workspace.Documents[1], editor.Workspace.ActiveDocument);
        Assert.AreSame(editor.Workspace.Documents[0], editor.Workspace.SecondaryDocument);
        Assert.AreEqual(primaryCaret, primary.CaretIndex);
        Assert.AreEqual(secondaryCaret, secondary.CaretIndex);
        foreach (var box in new[] { primary, secondary })
        {
            Assert.AreEqual(1, box.SelectionStart);
            Assert.AreEqual(2, box.SelectionLength);
            var original = ReferenceEquals(box, primary) ? "right" : "left";
            Assert.AreEqual(leaveRedoPending ? original : original + " edited", box.Text);
            if (leaveRedoPending)
            {
                Assert.IsTrue(box.CanRedo);
                box.Redo();
                Assert.AreEqual(original + " edited", box.Text);
            }
            Assert.IsTrue(box.CanUndo);
            box.Undo();
            Assert.AreEqual(original, box.Text);
        }
    });

    [TestMethod]
    public void AltClickReplacementKeepsOutgoingUnsavedContentAndCaret() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            editor.OpenNote(CreateNote(files, name, name));
        TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt);
        var outgoing = editor.Workspace.SecondaryDocument!;
        var primary = editor.Workspace.ActiveDocument;
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        secondary.Select(0, 0);
        secondary.SelectedText = "unsaved ";
        secondary.CaretIndex = 3;
        TabMouseDown(TabItem(editor, 1), ModifierKeys.Alt);
        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
        Assert.AreEqual("unsaved a.txt", outgoing.Content);
        Assert.IsTrue(outgoing.IsDirty);
        Assert.AreEqual(3, outgoing.CaretIndex);
        Assert.AreEqual("a.txt", File.ReadAllText(outgoing.FilePath));
        Assert.AreEqual("b.txt", secondary.Text);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt);
        Assert.AreEqual("unsaved a.txt", secondary.Text);
        Assert.AreEqual(3, secondary.CaretIndex);
        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
    });

    [TestMethod]
    public void AltClickSingleVisibleDocumentOnlyFocusesIt() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "only.txt", "only"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        primary.Select(1, 2);
        Assert.IsTrue(TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt).Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        Assert.IsTrue(primary.IsKeyboardFocused);
        Assert.AreEqual(1, primary.SelectionStart);
        Assert.AreEqual(2, primary.SelectionLength);
    });

    [TestMethod]
    [DataRow(ModifierKeys.None)]
    [DataRow(ModifierKeys.Control)]
    [DataRow(ModifierKeys.Shift)]
    [DataRow(ModifierKeys.Alt | ModifierKeys.Control)]
    [DataRow(ModifierKeys.Alt | ModifierKeys.Shift)]
    public void OtherTabModifiersKeepOrdinarySelectionAndDragPreparation(ModifierKeys modifiers) => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.txt", "a"));
        editor.OpenNote(CreateNote(files, "b.txt", "b"));
        var target = TabItem(editor, 0);
        var args = TabMouseDown(target, modifiers);
        Assert.IsFalse(args.Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        Assert.AreSame(editor.Workspace.Documents[0],
            typeof(NoteEditorWindow).GetField("_draggedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        if (modifiers == ModifierKeys.None)
        {
            args.RoutedEvent = Mouse.MouseDownEvent;
            target.RaiseEvent(args);
            Assert.AreSame(editor.Workspace.Documents[0], editor.Workspace.ActiveDocument);
        }
    });

    [TestMethod]
    public void AltClickCloseButtonAndOtherMouseButtonsDoNotOpenSplit() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.txt", "a"));
        editor.OpenNote(CreateNote(files, "b.txt", "b"));
        var target = TabItem(editor, 0);
        var close = VisualTreeHelpers.FindDescendant<Button>(target)!;
        Assert.IsNotNull(close);
        var content = VisualTreeHelpers.FindDescendant<TextBlock>(close)!;
        Assert.IsNotNull(content);
        Assert.IsFalse(TabMouseDown(content, ModifierKeys.Alt).Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        Assert.IsFalse(TabMouseDown(target, ModifierKeys.Alt, MouseButton.Right).Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreEqual("b.txt", Path.GetFileName(editor.Workspace.ActiveDocument!.FilePath));
    });

    [TestMethod]
    public void AltClickVisibleMarkdownDocumentFocusesItsPreview() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "preview.md", "# preview"));
        editor.OpenNote(CreateNote(files, "other.txt", "other"));
        TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        KeyboardTestInput.Press(secondary, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        editor.HandleNavigationShortcut(Key.D1, ModifierKeys.Control);
        TabMouseDown(TabItem(editor, 0), ModifierKeys.Alt);
        Assert.IsTrue(((FrameworkElement)editor.FindName("SecondaryMarkdownPreview")).IsKeyboardFocusWithin);
        Assert.AreEqual("other.txt", Path.GetFileName(editor.Workspace.ActiveDocument!.FilePath));
        Assert.AreEqual("# preview", secondary.Text);
    });

    [TestMethod]
    public void AltClickBlankTabAreaAndMiddleClickKeepExistingBehavior() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.txt", "a"));
        editor.OpenNote(CreateNote(files, "b.txt", "b"));
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        var primary = editor.Workspace.ActiveDocument;
        Assert.IsFalse(TabMouseDown(tabs, ModifierKeys.Alt).Handled);
        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        var target = TabItem(editor, 0);
        Assert.IsTrue(TabMouseDown(target, ModifierKeys.Alt, MouseButton.Middle).Handled);
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        });
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
    });

    [TestMethod]
    public void OpenShortcutUsesSupportedFileDialogAndReusesDirtyOpenTab() => WithEditor((editor, directory, _, files) =>
    {
        var path = CreateNote(files, "existing.md", "saved");
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: dialog =>
        {
            dialog.FileName = path;
            return true;
        }));
        var document = editor.Workspace.CurrentDocument!;
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.Text = "unsaved edits";
        box.Select(2, 4);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: dialog =>
        {
            Assert.AreEqual(files.CurrentDirectory, dialog.InitialDirectory);
            Assert.AreEqual(NoteFileExtensions.FileDialogFilter, dialog.Filter);
            Assert.IsTrue(dialog.CheckFileExists);
            Assert.IsFalse(dialog.Multiselect);
            dialog.FileName = path;
            return true;
        }));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreSame(document, editor.Workspace.CurrentDocument);
        Assert.AreEqual("unsaved edits", box.Text);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(4, box.SelectionLength);
        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: _ => false));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreEqual("saved", File.ReadAllText(path));
    });

    [TestMethod]
    public void RevealShortcutUsesCurrentPaneAndKeepsMissingFilesSafe() => WithEditor((editor, _, _, files) =>
    {
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift,
            reveal: _ => Assert.Fail("No note is active.")));
        var first = CreateNote(files, "first.md", "first");
        var second = CreateNote(files, "second.md", "second");
        editor.OpenNote(first);
        editor.OpenNote(second);
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var current = editor.Workspace.CurrentDocument!;
        ProcessStartInfo? start = null;
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift, reveal: info => start = info));
        Assert.IsNotNull(start);
        Assert.AreEqual("explorer.exe", start.FileName);
        Assert.AreEqual($"/select,\"{current.FilePath}\"", start.Arguments);
        Assert.IsTrue(start.UseShellExecute);
        File.Delete(current.FilePath);
        editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift, reveal: info => start = info);
        Assert.AreEqual($"\"{files.NotesDirectory}\"", start.Arguments);
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Alt));
    });

    [TestMethod]
    public void QuickOpenUsesManagedFilenamesAndExistingOpenPathWithoutDuplicatingTabs() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "one/same.md", "first");
        var second = CreateNote(files, "two/same.md", "second");
        CreateNote(files, "not-indexed.bin", "ignore");
        CreateNote(files, "archived.md", "archived");
        Assert.IsTrue(files.ArchiveNote("archived.md").Success);
        editor.OpenNote(first);
        var original = editor.Workspace.CurrentDocument;
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.Text = "dirty";
        var snapshot = editor.GetQuickOpenNotes();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(2, snapshot.Count(note => note.FileName == "same.md"));
        Assert.AreNotEqual(snapshot[0].DisplayPath, snapshot[1].DisplayPath);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.P, ModifierKeys.Control, chooseNote: dialog =>
        {
            var results = (ListBox)dialog.FindName("ResultsList");
            results.SelectedItem = results.Items.OfType<QuickOpenNote>().Single(note => note.FullPath == first);
            Assert.IsTrue(dialog.TrySelectCurrent());
            dialog.Close();
            return true;
        }));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreSame(original, editor.Workspace.CurrentDocument);
        Assert.AreEqual("dirty", box.Text);
        Assert.IsTrue(editor.OpenQuickNote(dialog =>
        {
            var results = (ListBox)dialog.FindName("ResultsList");
            results.SelectedItem = results.Items.OfType<QuickOpenNote>().Single(note => note.FullPath == second);
            Assert.IsTrue(dialog.TrySelectCurrent());
            dialog.Close();
            return true;
        }));
        Assert.AreEqual(2, editor.Workspace.Documents.Count);
        Assert.AreEqual(second, editor.OpenFilePath);
    });

    [TestMethod]
    public void QuickOpenFiltersInMemoryNavigatesAndCancelsWithoutOpening() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "a/alpha.md", "alpha");
        var second = CreateNote(files, "b/alpha.md", "alpha");
        CreateNote(files, "beta.txt", "alpha content must not match");
        var dialog = new QuickOpenDialog(editor.GetQuickOpenNotes()) { Owner = editor };
        CreateNote(files, "new-alpha.md", "added after the snapshot");
        Assert.IsFalse(ShowWithInput(dialog, () =>
        {
            var query = (TextBox)dialog.FindName("QueryTextBox");
            var results = (ListBox)dialog.FindName("ResultsList");
            query.Text = "ALPHA";
            Assert.AreEqual(2, results.Items.Count);
            Assert.AreEqual(first, ((QuickOpenNote)results.SelectedItem).FullPath);
            KeyboardTestInput.Press(query, Key.Down, ModifierKeys.None);
            Assert.AreEqual(second, ((QuickOpenNote)results.SelectedItem).FullPath);
            KeyboardTestInput.Press(query, Key.Up, ModifierKeys.None);
            Assert.AreEqual(first, ((QuickOpenNote)results.SelectedItem).FullPath);
            query.Text = "no results";
            Assert.AreEqual(0, results.Items.Count);
            Assert.IsFalse(dialog.TrySelectCurrent());
            Assert.AreEqual(Visibility.Visible, ((TextBlock)dialog.FindName("EmptyText")).Visibility);
            KeyboardTestInput.Press(query, Key.Escape, ModifierKeys.None);
        }));
        Assert.IsNull(dialog.SelectedFilePath);
        Assert.AreEqual(0, editor.Workspace.Documents.Count);
        Assert.IsTrue(editor.GetQuickOpenNotes().Any(note => note.FileName == "new-alpha.md"));
    });

    [TestMethod]
    public void QuickOpenRejectsStaleEntriesAndEnterSelectsTheRemainingNote() => WithEditor((editor, _, _, files) =>
    {
        var stale = CreateNote(files, "old.md", "old");
        var valid = CreateNote(files, "valid.md", "valid");
        var dialog = new QuickOpenDialog(editor.GetQuickOpenNotes()) { Owner = editor };
        File.Move(stale, Path.Combine(files.NotesDirectory, "renamed.md"));
        Assert.IsTrue(ShowWithInput(dialog, () =>
        {
            var query = (TextBox)dialog.FindName("QueryTextBox");
            Assert.IsFalse(dialog.TrySelectCurrent());
            Assert.AreEqual(1, ((ListBox)dialog.FindName("ResultsList")).Items.Count);
            Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)dialog.FindName("ErrorText")).Text));
            KeyboardTestInput.Press(query, Key.Enter, ModifierKeys.None);
        }));
        Assert.AreEqual(valid, dialog.SelectedFilePath);
        Assert.IsTrue(editor.GetQuickOpenNotes().Any(note => note.FileName == "renamed.md"));
    });

    [TestMethod]
    public void SplitToggleAndPaneFocusKeepDirtyContentAssignmentsAndSelections() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        Assert.IsTrue(KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control).Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        primary.Text = "dirty primary";
        primary.Select(1, 3);
        var primaryDocument = editor.Workspace.ActiveDocument!;
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        var secondaryDocument = editor.Workspace.SecondaryDocument!;
        Assert.IsNotNull(secondaryDocument);
        secondary.Text = "dirty secondary";
        secondary.Select(2, 4);
        KeyboardTestInput.Press(secondary, Key.D1, ModifierKeys.Control);
        Assert.IsTrue(primary.IsKeyboardFocused);
        Assert.AreSame(primaryDocument, editor.Workspace.CurrentDocument);
        Assert.AreSame(primaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.AreEqual(1, primary.SelectionStart);
        Assert.AreEqual(3, primary.SelectionLength);
        KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        Assert.AreSame(secondaryDocument, editor.Workspace.CurrentDocument);
        Assert.AreSame(secondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.AreEqual(2, secondary.SelectionStart);
        Assert.AreEqual(4, secondary.SelectionLength);
        KeyboardTestInput.Press(secondary, Key.Oem5, ModifierKeys.Control);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        Assert.IsTrue(primary.IsKeyboardFocused);
        Assert.AreEqual("dirty secondary", secondaryDocument.Content);
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        Assert.AreSame(secondaryDocument, editor.Workspace.SecondaryDocument);
        Assert.AreSame(primaryDocument, editor.Workspace.ActiveDocument);
        Assert.AreEqual("dirty primary", primary.Text);
        Assert.AreEqual("dirty secondary", secondary.Text);
        Assert.IsTrue(primaryDocument.IsDirty);
        Assert.IsTrue(secondaryDocument.IsDirty);
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.D1, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.D2, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control | ModifierKeys.Shift));
    });

    [TestMethod]
    public void SwitchingSplitFocusPreservesUndoAndHighlightsTheCurrentTab() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        secondary.Select(secondary.Text.Length, 0);
        secondary.SelectedText = " edited";

        KeyboardTestInput.Press(secondary, Key.D1, ModifierKeys.Control);
        Assert.AreSame(editor.Workspace.ActiveDocument, tabs.SelectedItem);
        KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control);
        Assert.AreSame(editor.Workspace.SecondaryDocument, tabs.SelectedItem);
        Assert.IsTrue(secondary.CanUndo);
        secondary.Undo();
        Assert.AreEqual("first", secondary.Text);
        Assert.AreEqual("second", primary.Text);
    });

    [TestMethod]
    public void NewNoteShortcutKeepsTypingInTheFocusedSplitPane() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = editor.Workspace.ActiveDocument;
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        editor.NewNoteRequested += (_, _) => editor.OpenNote(CreateNote(files, "new.md", ""));

        KeyboardTestInput.Press(secondary, Key.N, ModifierKeys.Control);

        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
        Assert.AreEqual("new.md", Path.GetFileName(editor.Workspace.SecondaryDocument!.FilePath));
        Assert.AreSame(editor.Workspace.SecondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        secondary.SelectedText = "typed into new note";
        Assert.AreEqual("typed into new note", editor.Workspace.SecondaryDocument.Content);
        Assert.AreEqual("second", ((TextBox)editor.FindName("EditorTextBox")).Text);
    });

    [TestMethod]
    public void ReopeningAnAlreadyVisibleSecondaryPreviewFocusesThePreview() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "first.md", "# first");
        editor.OpenNote(first);
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        KeyboardTestInput.Press(secondary, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        editor.HandleNavigationShortcut(Key.D1, ModifierKeys.Control);

        Assert.IsTrue(editor.OpenNote(first));

        Assert.IsTrue(((FrameworkElement)editor.FindName("SecondaryMarkdownPreview")).IsKeyboardFocusWithin);
        Assert.AreSame(editor.Workspace.SecondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
    });

    [TestMethod]
    public void DraggingAnotherTabIntoSplitKeepsThePreviouslyVisibleNote() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        var previous = editor.Workspace.ActiveDocument;
        var dragged = editor.Workspace.Documents[1];
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        editor.UpdateLayout();
        var container = (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(dragged);
        Assert.IsNotNull(container);
        var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = container
        };
        typeof(NoteEditorWindow).GetMethod("OpenTabsList_PreviewMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [tabs, mouseDown]);
        tabs.SelectedItem = dragged;
        Assert.AreSame(dragged, editor.Workspace.ActiveDocument);

        typeof(NoteEditorWindow).GetMethod("ShowSecondaryDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [dragged, true, true, true]);

        Assert.AreSame(previous, editor.Workspace.ActiveDocument);
        Assert.AreSame(dragged, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("c.md", ((TextBox)editor.FindName("EditorTextBox")).Text);
        Assert.AreEqual("b.md", ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text);
    });

    [TestMethod]
    public void MovingPrimaryToTheRightKeepsTheExistingOtherPaneAndItsEdits() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var previousPrimary = editor.Workspace.ActiveDocument;
        var previousSecondary = editor.Workspace.SecondaryDocument;
        ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text = "unsaved secondary";

        typeof(NoteEditorWindow).GetMethod("ShowSecondaryDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [previousPrimary, true, true, true]);

        Assert.AreSame(previousSecondary, editor.Workspace.ActiveDocument);
        Assert.AreSame(previousPrimary, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("unsaved secondary", ((TextBox)editor.FindName("EditorTextBox")).Text);
        Assert.AreEqual("c.md", ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text);
    });

    [TestMethod]
    public void CyclingFromSecondaryUsesCurrentTabAndChangesOnlyTheIntendedPane() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        var primaryDocument = editor.Workspace.ActiveDocument;
        Assert.AreEqual("a.md", Path.GetFileName(editor.OpenFilePath));
        secondary.Text = "keep this unsaved";
        var originalSecondary = editor.Workspace.SecondaryDocument!;
        KeyboardTestInput.Press(secondary, Key.Tab, ModifierKeys.Control);
        Assert.AreEqual("b.md", Path.GetFileName(editor.OpenFilePath));
        Assert.AreSame(primaryDocument, editor.Workspace.ActiveDocument);
        Assert.AreEqual("keep this unsaved", originalSecondary.Content);
        KeyboardTestInput.Press(secondary, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreSame(originalSecondary, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("keep this unsaved", secondary.Text);
    });

    [TestMethod]
    public void NativeEditMenuCommandsTargetFocusedPaneAndPreserveUndo() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "first"));
        editor.OpenNote(CreateNote(files, "b.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var edit = (MenuItem)editor.FindName("EditMenu");
        foreach (var box in new[] { primary, secondary })
        {
            Assert.IsTrue(box.Focus(), $"Could not focus {box.Name}; current focus: {Keyboard.FocusedElement}");
            box.Select(1, 0);
            box.SelectedText = "added";
            var changed = box.Text;
            edit.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, edit));
            var native = edit.Items.OfType<MenuItem>().Where(item => item.Command is RoutedCommand command &&
                command.OwnerType == typeof(ApplicationCommands)).ToList();
            Assert.AreEqual(6, native.Count);
            foreach (var item in native) Assert.AreSame(box, item.CommandTarget,
                $"Expected {box.Name}; focused: {((FrameworkElement?)Keyboard.FocusedElement)?.Name}; current: {editor.OpenFilePath}");
            ApplicationCommands.Undo.Execute(null, box);
            Assert.AreNotEqual(changed, box.Text);
            ApplicationCommands.Redo.Execute(null, box);
            Assert.AreEqual(changed, box.Text);
            ApplicationCommands.SelectAll.Execute(null, box);
            Assert.AreEqual(box.Text, box.SelectedText);
        }
    });

    [TestMethod]
    public void GoToLineTargetsTheFocusedPaneAndValidatesDialogInput() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "a\r\nb\r\nc"));
        editor.OpenNote(CreateNote(files, "b.md", "one\ntwo"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var primaryCaret = primary.CaretIndex;
        Exception? error = null;
        editor.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<GoToLineDialog>().Single();
            try
            {
                var input = (TextBox)dialog.FindName("LineNumberTextBox");
                input.Text = "0";
                KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None);
                Assert.IsTrue(dialog.IsVisible);
                Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)dialog.FindName("ErrorText")).Text));
                input.Text = "3";
                KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None);
            }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(KeyboardTestInput.Press(secondary, Key.G, ModifierKeys.Control).Handled);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        Assert.AreEqual(6, secondary.CaretIndex);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        Assert.AreEqual(primaryCaret, primary.CaretIndex);
        Assert.AreEqual("a\r\nb\r\nc", secondary.Text);
    });

    [TestMethod]
    public void WordWrapMatchesExistingControlsAndDoesNotRunInSearchInputs() => WithEditor((editor, directory, settings, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "a"));
        editor.OpenNote(CreateNote(files, "b.md", "b"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var before = primary.TextWrapping;
        KeyboardTestInput.Press(secondary, Key.Z, ModifierKeys.Alt);
        Assert.AreNotEqual(before, primary.TextWrapping);
        Assert.AreEqual(primary.TextWrapping, secondary.TextWrapping);
        Assert.AreEqual(primary.TextWrapping == TextWrapping.Wrap, ((MenuItem)editor.FindName("HeaderWordWrapMenuItem")).IsChecked);
        var search = (TextBox)editor.FindName("FindTextBox");
        var after = primary.TextWrapping;
        KeyboardTestInput.Press(search, Key.Z, ModifierKeys.Alt);
        Assert.AreEqual(after, primary.TextWrapping);

        var miniPad = new MiniPadWindow(new MiniPadRecoveryService(directory.Path), settings);
        var viewModel = new ScratchpadWindowViewModel(settings, new ScratchpadContentService(directory.Path));
        var scratchpad = new ScratchpadWindow(viewModel);
        try
        {
            miniPad.Show();
            var miniEditor = (TextBox)miniPad.FindName("Editor");
            var miniBefore = miniEditor.TextWrapping;
            KeyboardTestInput.Press(miniEditor, Key.Z, ModifierKeys.Alt);
            Assert.AreNotEqual(miniBefore, miniEditor.TextWrapping);
            Assert.AreEqual(miniEditor.TextWrapping == TextWrapping.Wrap,
                settings.LoadMiniPadWindows()[miniPad.InstanceId].WordWrapEnabled);
            scratchpad.Show();
            var rich = (RichTextBox)scratchpad.FindName("Editor");
            var richBefore = viewModel.WordWrapEnabled;
            KeyboardTestInput.Press(rich, Key.Z, ModifierKeys.Alt);
            Assert.AreEqual(!richBefore, viewModel.WordWrapEnabled);
            Assert.AreEqual(viewModel.WordWrapEnabled, settings.LoadScratchpadWindowState().WordWrapEnabled);
            Assert.AreEqual(viewModel.WordWrapEnabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                rich.HorizontalScrollBarVisibility);
            var richFind = (TextBox)scratchpad.FindName("FindTextBox");
            KeyboardTestInput.Press(richFind, Key.Z, ModifierKeys.Alt);
            Assert.AreEqual(!richBefore, viewModel.WordWrapEnabled);
        }
        finally
        {
            miniPad.KeepDraftOnClose();
            miniPad.Close();
            scratchpad.Close();
        }
    });

    private static bool ShowWithInput(Window dialog, Action input)
    {
        Exception? error = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
        {
            try { input(); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        var accepted = dialog.ShowDialog();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return accepted == true;
    }

    private static string CreateNote(NoteFileService files, string name, string content)
    {
        var path = Path.GetFullPath(Path.Combine(files.NotesDirectory, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WithEditor(Action<NoteEditorWindow, TemporaryTestDirectory, AppSettingsService, NoteFileService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        var editor = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
            new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path), files);
        try
        {
            editor.Show();
            action(editor, directory, settings, files);
        }
        finally
        {
            foreach (var document in editor.Workspace.Documents) editor.Workspace.MarkSaved(document);
            editor.TryPrepareForClose();
            editor.Close();
        }
    });
}
