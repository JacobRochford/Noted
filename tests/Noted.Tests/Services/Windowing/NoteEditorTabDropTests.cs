using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed partial class NoteEditorTabDropTests
{
    [TestMethod]
    public void WholeSinglePaneAcceptsSplitAndSplitLayoutRejectsNoOps() => WithEditor((window, _) =>
    {
        var area = (FrameworkElement)window.FindName("EditorWorkspace");
        var document = window.Workspace.Documents[0];
        Assert.AreEqual(true, window.GetPaneDropDestination(document, new Point(1, 1)));
        Assert.AreEqual(true, window.GetPaneDropDestination(document, new Point(area.ActualWidth - 1, 1)));
        Assert.IsNull(window.GetPaneDropDestination(document, new Point(-1, 1)));
        var foreign = new OpenNoteDocument(Path.GetFullPath("foreign.txt"), "", "", false);
        Assert.IsNull(window.GetPaneDropDestination(foreign, new Point(1, 1)));
        window.ApplyPaneDrop(document, true);
        window.UpdateLayout();
        var right = (FrameworkElement)window.FindName("SecondaryEditorPane");
        var rightPoint = right.TranslatePoint(new Point(1, 1), area);
        Assert.IsNull(window.GetPaneDropDestination(document, rightPoint));
        Assert.AreEqual(false, window.GetPaneDropDestination(document, new Point(1, 1)));
        Assert.IsNull(window.GetPaneDropDestination(window.Workspace.ActiveDocument!, new Point(1, 1)));
    });

    [TestMethod]
    public void ReplacementAndSwapRetainEditorsSelectionsUndoAndPersistAssignments() => WithEditor((window, session) =>
    {
        var original = window.Workspace.ActiveDocument!;
        var first = window.Workspace.Documents[0];
        window.ApplyPaneDrop(first, true);
        var box = (TextBox)window.FindName("SecondaryEditorTextBox");
        box.Select(box.Text.Length, 0);
        box.SelectedText = " edited";
        box.Select(1, 2);
        window.ApplyPaneDrop(window.Workspace.Documents[1], true);
        Assert.AreEqual("a edited", first.Content);
        window.ApplyPaneDrop(first, true);
        Assert.AreSame(box, window.FindName("SecondaryEditorTextBox"));
        Assert.AreEqual(1, box.SelectionStart);
        Assert.AreEqual(2, box.SelectionLength);
        window.ApplyPaneDrop(first, false);
        Assert.AreSame(first, window.Workspace.ActiveDocument);
        Assert.AreSame(original, window.Workspace.SecondaryDocument);
        Assert.AreSame(box, window.FindName("EditorTextBox"));
        Assert.IsTrue(box.IsKeyboardFocused);
        box.Undo();
        Assert.AreEqual("a", box.Text);
        box.Redo();
        Assert.AreEqual("a edited", box.Text);
        var saved = session.Load().Session!;
        Assert.AreEqual(first.FilePath, saved.ActiveFilePath);
        Assert.AreEqual(original.FilePath, saved.SecondaryFilePath);
    });

    [TestMethod]
    public void SidebarHomeEndAndArrowsDoNotWrapOrTakeFocusIntoEditor() => WithEditor((window, _) =>
    {
        var tabs = (ListBox)window.FindName("OpenTabsList");
        tabs.Focus();
        KeyboardTestInput.Press(tabs, Key.Home, ModifierKeys.None);
        Assert.AreEqual(0, tabs.SelectedIndex);
        KeyboardTestInput.Press((UIElement)Keyboard.FocusedElement, Key.Up, ModifierKeys.None);
        Assert.AreEqual(0, tabs.SelectedIndex);
        KeyboardTestInput.Press((UIElement)Keyboard.FocusedElement, Key.End, ModifierKeys.None);
        Assert.AreEqual(2, tabs.SelectedIndex);
        KeyboardTestInput.Press((UIElement)Keyboard.FocusedElement, Key.Down, ModifierKeys.None);
        Assert.AreEqual(2, tabs.SelectedIndex);
        Assert.IsTrue(tabs.IsKeyboardFocusWithin);
        KeyboardTestInput.Press(tabs, Key.Home, ModifierKeys.None);
        Assert.AreSame(window.Workspace.Documents[0], window.Workspace.CurrentDocument);
    });

    [TestMethod]
    public void InsertionUsesRowHalvesAndScrollingRequiresOverflowAndAnEdge() => WithEditor((window, _) =>
    {
        var tabs = (ListBox)window.FindName("OpenTabsList");
        var row = (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromIndex(1);
        var top = row.TranslatePoint(new Point(), tabs).Y;
        Assert.AreEqual(1, window.GetTabInsertion(new Point(5, top + 1)).Index);
        Assert.AreEqual(2, window.GetTabInsertion(new Point(5, top + row.ActualHeight - 1)).Index);
        Assert.AreEqual(0, window.GetTabScrollDirection(new Point(5, 1)));
        for (var index = 0; index < 100; index++)
            window.Workspace.Add(new OpenNoteDocument(Path.GetFullPath($"overflow-{index}.txt"), "", "", false));
        tabs.UpdateLayout();
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(tabs)!;
        Assert.IsTrue(scroll.ScrollableHeight > 0);
        Assert.AreEqual(1, window.GetTabScrollDirection(new Point(5, tabs.ActualHeight - 1)));
        Assert.AreEqual(0, window.GetTabScrollDirection(new Point(5, 1)));
        Assert.AreEqual(0, window.GetTabScrollDirection(new Point(5, tabs.ActualHeight / 2)));
        Assert.AreEqual(0, window.GetTabScrollDirection(new Point(-1, 1)));
        scroll.ScrollToEnd(); tabs.UpdateLayout();
        Assert.AreEqual(-1, window.GetTabScrollDirection(new Point(5, 1)));
        Assert.AreEqual(0, window.GetTabScrollDirection(new Point(5, tabs.ActualHeight - 1)));
    });

    [TestMethod]
    [DataRow(0, 0, 0)]
    [DataRow(0, 1, 0)]
    [DataRow(0, 3, 2)]
    [DataRow(2, 0, 0)]
    [DataRow(2, 2, 2)]
    [DataRow(1, 3, 2)]
    public void InsertionAccountsForRemovalOfDraggedRow(int source, int insertion, int expected) =>
        Assert.AreEqual(expected, NoteEditorWindow.GetReorderIndex(source, insertion, 3));

    [TestMethod]
    public void ContextActionOnPrimarySwapsPanesAndOnSecondaryOnlyFocuses() => WithEditor((window, _) =>
    {
        var primary = window.Workspace.ActiveDocument!;
        var other = window.Workspace.Documents[0];
        InvokeContextAction(window, primary);
        Assert.AreSame(primary, window.Workspace.SecondaryDocument);
        Assert.AreSame(other, window.Workspace.ActiveDocument);
        InvokeContextAction(window, primary);
        Assert.AreSame(other, window.Workspace.ActiveDocument);
        Assert.IsTrue(((TextBox)window.FindName("SecondaryEditorTextBox")).IsKeyboardFocused);
        InvokeContextAction(window, other);
        Assert.AreSame(primary, window.Workspace.ActiveDocument);
        Assert.AreSame(other, window.Workspace.SecondaryDocument);
    });

    private static void InvokeContextAction(NoteEditorWindow window, OpenNoteDocument document)
    {
        var tabs = (ListBox)window.FindName("OpenTabsList");
        var row = (DependencyObject)tabs.ItemContainerGenerator.ContainerFromItem(document);
        var grid = VisualTreeHelpers.FindDescendant<Grid>(row)!;
        var menu = grid.ContextMenu;
        Assert.IsNotNull(menu);
        menu.DataContext = document;
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    [TestMethod]
    public void ReorderPersistsOrderWithoutChangingPanesContentOrFocusAndRejectsInvalidMoves() => WithEditor((window, session) =>
    {
        var document = window.Workspace.Documents[0];
        window.ApplyPaneDrop(document, true);
        var primary = window.Workspace.ActiveDocument;
        var editor = (TextBox)window.FindName("SecondaryEditorTextBox");
        editor.Select(0, 0);
        editor.SelectedText = "unsaved ";
        var focused = Keyboard.FocusedElement;
        Assert.IsTrue(window.ApplyTabReorder(document, 3));
        Assert.AreSame(document, window.Workspace.Documents[2]);
        Assert.AreSame(primary, window.Workspace.ActiveDocument);
        Assert.AreSame(document, window.Workspace.SecondaryDocument);
        Assert.AreSame(focused, Keyboard.FocusedElement);
        Assert.AreEqual("unsaved a", document.Content);
        CollectionAssert.AreEqual(window.Workspace.Documents.Select(item => item.FilePath).ToArray(),
            session.Load().Session!.Tabs.Select(item => item.FilePath).ToArray());
        Assert.IsFalse(window.ApplyTabReorder(document, 2));
        Assert.IsFalse(window.ApplyTabReorder(document, 3));
        Assert.IsFalse(window.ApplyTabReorder(document, -1));
        Assert.IsFalse(window.ApplyTabReorder(new OpenNoteDocument(Path.GetFullPath("foreign.txt"), "", "", false), 0));
        Assert.AreSame(focused, Keyboard.FocusedElement);
    });

    [TestMethod]
    public void FeedbackCleanupDoesNotChangeDocumentsAndAllowsAnotherDrag() => WithEditor((window, _) =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Invoke(string method) => typeof(NoteEditorWindow).GetMethod(method, flags)!.Invoke(window, null);
        var order = window.Workspace.Documents.ToArray();
        var primary = window.Workspace.ActiveDocument;
        var focus = Keyboard.FocusedElement;
        Invoke("BeginTabDragFeedback");
        var feedback = typeof(NoteEditorWindow).GetField("_tabDragFeedback", flags)!.GetValue(window);
        Assert.IsNotNull(feedback);
        Assert.IsFalse(((UIElement)feedback).IsHitTestVisible);
        Invoke("ClearTabDragFeedback");
        Assert.AreSame(feedback, typeof(NoteEditorWindow).GetField("_tabDragFeedback", flags)!.GetValue(window));
        Invoke("EndTabDragFeedback");
        Assert.IsNull(typeof(NoteEditorWindow).GetField("_tabDragFeedback", flags)!.GetValue(window));
        Invoke("BeginTabDragFeedback");
        Assert.IsNotNull(typeof(NoteEditorWindow).GetField("_tabDragFeedback", flags)!.GetValue(window));
        Invoke("EndTabDragFeedback");
        CollectionAssert.AreEqual(order, window.Workspace.Documents.ToArray());
        Assert.AreSame(primary, window.Workspace.ActiveDocument);
        Assert.AreSame(focus, Keyboard.FocusedElement);
    });

    [TestMethod]
    [DataRow(96)]
    [DataRow(144)]
    [DataRow(192)]
    public void PaneOverlayRendersAtLogicalBoundsAcrossBitmapDpi(int dpi) => WithEditor((window, _) =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        byte[] Pixel()
        {
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96),
                (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            image.Render(window);
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(150 * dpi / 96, 150 * dpi / 96, 1, 1), pixel, 4, 0);
            return pixel;
        }
        var before = Pixel();
        typeof(NoteEditorWindow).GetMethod("BeginTabDragFeedback", flags)!.Invoke(window, null);
        var feedback = typeof(NoteEditorWindow).GetField("_tabDragFeedback", flags)!.GetValue(window)!;
        feedback.GetType().GetProperty("Pane")!.SetValue(feedback, new Rect(100, 100, 120, 120));
        ((UIElement)feedback).InvalidateVisual();
        window.UpdateLayout();
        var after = Pixel();
        Assert.IsFalse(before.SequenceEqual(after), "The preview must tint the expected pane interior.");
        typeof(NoteEditorWindow).GetMethod("EndTabDragFeedback", flags)!.Invoke(window, null);
    });

    private static void WithEditor(Action<NoteEditorWindow, NoteEditorSessionService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var session = new NoteEditorSessionService(directory.Path);
        var window = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
            new NoteRecoveryService(directory.Path), session);
        try
        {
            foreach (var name in new[] { "a", "b", "c" })
            {
                var path = directory.File(name + ".txt");
                File.WriteAllText(path, name);
                window.OpenNote(path);
            }
            window.UpdateLayout();
            action(window, session);
        }
        finally
        {
            foreach (var document in window.Workspace.Documents) window.Workspace.MarkSaved(document);
            window.TryPrepareForClose();
            window.Close();
        }
    });
}
