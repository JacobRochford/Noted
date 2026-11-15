using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;

namespace Noted.Tests.Services;

public sealed partial class NoteEditorTabDropTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void EdgeScrollUsesDragPositionAndKeepsPreviewWhileScrollingBothDirections() => WithEditor((window, _) =>
    {
        var tabs = AddOverflow(window);
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(tabs)!;
        var document = window.Workspace.Documents[0];
        var active = window.Workspace.ActiveDocument;
        var focus = Keyboard.FocusedElement;
        SetField(window, "_draggedDocument", document);
        InvokePrivate(window, "BeginTabDragFeedback");
        var bottom = new Point(10, tabs.ActualHeight - 2);
        DragOver(window, tabs, document, bottom);
        var feedback = GetField<object>(window, "_tabDragFeedback");
        var label = feedback.GetType().GetProperty("Label")!;
        Assert.AreEqual(document.SidebarLabel, label.GetValue(feedback));
        var timer = GetField<DispatcherTimer>(window, "_tabScrollTimer");
        Assert.IsTrue(timer.IsEnabled);
        var before = scroll.VerticalOffset;
        InvokePrivate(window, "TabAutoScroll_Tick", null, EventArgs.Empty);
        Assert.IsTrue(scroll.VerticalOffset > before, "The actual viewport must move, not just compute a scroll direction.");
        Assert.AreEqual(document.SidebarLabel, label.GetValue(feedback));
        DragOver(window, tabs, document, bottom);
        Assert.IsTrue(timer.IsEnabled);
        Assert.AreEqual(document.SidebarLabel, label.GetValue(feedback));
        var afterDown = scroll.VerticalOffset;
        DragOver(window, tabs, document, new Point(10, 2));
        InvokePrivate(window, "TabAutoScroll_Tick", null, EventArgs.Empty);
        Assert.IsTrue(scroll.VerticalOffset < afterDown);
        Assert.AreEqual(document.SidebarLabel, label.GetValue(feedback));
        Assert.AreSame(active, window.Workspace.ActiveDocument);
        Assert.AreSame(focus, Keyboard.FocusedElement);
        InvokePrivate(window, "EndTabDragFeedback");
        Assert.IsFalse(timer.IsEnabled);
    });

    [TestMethod]
    public void LeavingSidebarStopsScrollAndReentryRestartsWithoutEndingDrag() => WithEditor((window, _) =>
    {
        var tabs = AddOverflow(window);
        var document = window.Workspace.Documents[0];
        SetField(window, "_draggedDocument", document);
        InvokePrivate(window, "BeginTabDragFeedback");
        DragOver(window, tabs, document, new Point(10, tabs.ActualHeight - 2));
        var timer = GetField<DispatcherTimer>(window, "_tabScrollTimer");
        Assert.IsTrue(timer.IsEnabled);
        var root = (FrameworkElement)window.Content;
        DragOver(window, root, document, new Point(-10, -10));
        Assert.IsFalse(timer.IsEnabled);
        Assert.AreSame(document, GetField<object>(window, "_draggedDocument"));
        DragOver(window, tabs, document, new Point(10, tabs.ActualHeight - 2));
        Assert.IsTrue(timer.IsEnabled);
        InvokePrivate(window, "EndTabDragFeedback");
    });

    [TestMethod]
    public void EdgeScrollTimerMovesViewportWithoutAnotherDragOver() => WithEditor((window, _) =>
    {
        var tabs = AddOverflow(window);
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(tabs)!;
        var document = window.Workspace.Documents[0];
        SetField(window, "_draggedDocument", document);
        InvokePrivate(window, "BeginTabDragFeedback");
        var before = scroll.VerticalOffset;
        DragOver(window, tabs, document, new Point(10, tabs.ActualHeight - 2));
        var timer = GetField<DispatcherTimer>(window, "_tabScrollTimer");
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher)
            { Interval = TimeSpan.FromSeconds(1) };
        EventHandler tick = (_, _) => frame.Continue = false;
        timer.Tick += tick;
        timeout.Tick += tick;
        try
        {
            timeout.Start();
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(scroll.VerticalOffset > before);
        }
        finally
        {
            timeout.Stop();
            timer.Tick -= tick;
            InvokePrivate(window, "EndTabDragFeedback");
        }
    });

    [TestMethod]
    public void DragWheelScrollsOverflowAndAccumulatesSmallDeltasWithoutChangingSelection() => WithEditor((window, _) =>
    {
        var tabs = AddOverflow(window);
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(tabs)!;
        var document = window.Workspace.Documents[0];
        var selected = tabs.SelectedItem;
        SetField(window, "_draggedDocument", document);
        InvokePrivate(window, "BeginTabDragFeedback");
        var center = new Point(10, tabs.ActualHeight / 2);
        var before = scroll.VerticalOffset;
        Assert.IsTrue(window.ScrollTabsDuringDrag(-60, center));
        Assert.AreEqual(before, scroll.VerticalOffset);
        Assert.IsTrue(window.ScrollTabsDuringDrag(-60, center));
        Assert.IsTrue(scroll.VerticalOffset > before);
        Assert.IsTrue(window.ScrollTabsDuringDrag(120, center));
        Assert.AreEqual(before, scroll.VerticalOffset);
        Assert.IsFalse(window.ScrollTabsDuringDrag(-120, new Point(-1, 1)));
        Assert.AreSame(selected, tabs.SelectedItem);
        InvokePrivate(window, "EndTabDragFeedback");
    });

    [TestMethod]
    public void NativeWheelHandlingIsLimitedToTheActiveDragAndSidebar() => WithEditor((window, _) =>
    {
        var tabs = AddOverflow(window);
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(tabs)!;
        SetField(window, "_draggedDocument", window.Workspace.Documents[0]);
        InvokePrivate(window, "BeginTabDragFeedback");
        var point = tabs.PointToScreen(new Point(10, tabs.ActualHeight / 2));
        var coordinates = new IntPtr(((long)(ushort)(short)point.Y << 16) | (ushort)(short)point.X);
        bool Dispatch()
        {
            object[] args = [IntPtr.Zero, 0x020A, new IntPtr(-120L << 16), coordinates, false];
            typeof(NoteEditorWindow).GetMethod("TabDragWindowMessage", PrivateInstance)!.Invoke(window, args);
            return (bool)args[4];
        }
        var before = scroll.VerticalOffset;
        Assert.IsFalse(Dispatch());
        Assert.AreEqual(before, scroll.VerticalOffset);
        SetField(window, "_isTabDragInProgress", true);
        Assert.IsTrue(Dispatch());
        Assert.IsTrue(scroll.VerticalOffset > before);
        InvokePrivate(window, "EndTabDragFeedback");
        Assert.IsNull(typeof(NoteEditorWindow).GetField("_tabDragSource", PrivateInstance)!.GetValue(window));
        SetField(window, "_isTabDragInProgress", false);
        Assert.IsFalse(Dispatch());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RightClickKeepsPaneAssignmentsAndMenuCloseTargetsInactiveDocument(bool split) => WithEditor((window, _) =>
    {
        if (split) window.ApplyPaneDrop(window.Workspace.Documents[1], true);
        var primary = window.Workspace.ActiveDocument;
        var secondary = window.Workspace.SecondaryDocument;
        var current = window.Workspace.CurrentDocument;
        var tabs = (ListBox)window.FindName("OpenTabsList");
        var target = window.Workspace.Documents[0];
        var row = (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(target);
        var grid = VisualTreeHelpers.FindDescendant<Grid>(row)!;
        var editor = (TextBox)window.FindName(split ? "SecondaryEditorTextBox" : "EditorTextBox");
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " unsaved";
        editor.Select(0, 1);
        var focus = Keyboard.FocusedElement;
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        grid.RaiseEvent(down);
        Assert.IsTrue(down.Handled);
        down.RoutedEvent = Mouse.MouseDownEvent;
        grid.RaiseEvent(down);
        Assert.AreSame(primary, window.Workspace.ActiveDocument);
        Assert.AreSame(secondary, window.Workspace.SecondaryDocument);
        Assert.AreSame(current, window.Workspace.CurrentDocument);
        Assert.AreSame(current, tabs.SelectedItem);
        Assert.AreSame(focus, Keyboard.FocusedElement);
        var menu = grid.ContextMenu;
        menu.PlacementTarget = grid;
        menu.IsOpen = true;
        try
        {
            Assert.IsTrue(menu.IsOpen);
            Assert.AreSame(target, menu.DataContext);
            Assert.AreSame(primary, window.Workspace.ActiveDocument);
            Assert.AreSame(secondary, window.Workspace.SecondaryDocument);
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Close Tab"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsFalse(window.Workspace.Documents.Contains(target));
            Assert.AreSame(primary, window.Workspace.ActiveDocument);
            Assert.AreSame(secondary, window.Workspace.SecondaryDocument);
            Assert.IsTrue(editor.Text.EndsWith(" unsaved"));
            Assert.AreEqual(1, editor.SelectionLength);
        }
        finally { menu.IsOpen = false; }
    });

    private static ListBox AddOverflow(NoteEditorWindow window)
    {
        for (var index = 0; index < 100; index++)
            window.Workspace.Add(new OpenNoteDocument(Path.GetFullPath($"overflow-{index}.txt"), "", "", false));
        var tabs = (ListBox)window.FindName("OpenTabsList");
        tabs.UpdateLayout();
        return tabs;
    }

    private static void DragOver(NoteEditorWindow window, UIElement target, OpenNoteDocument document, Point position)
    {
        var data = new DataObject(typeof(OpenNoteDocument), document);
        var args = (DragEventArgs)typeof(DragEventArgs).GetConstructors(PrivateInstance).Single()
            .Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, position]);
        args.RoutedEvent = DragDrop.PreviewDragOverEvent;
        target.RaiseEvent(args);
    }

    private static void InvokePrivate(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, arguments);

    private static T GetField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
}
