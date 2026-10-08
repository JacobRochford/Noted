using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class WindowDragHitTestTests
{
    [TestMethod]
    public void BackgroundRequiresAnAncestorWithinTheDragSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            var child = new Border();
            var surface = new Grid();
            surface.Children.Add(child);
            Assert.IsTrue(WindowDragHitTest.CanDrag(child, surface));
            Assert.IsFalse(WindowDragHitTest.CanDrag(new Border(), surface));
            Assert.IsFalse(WindowDragHitTest.CanDrag(null, surface));
        });
    }

    [TestMethod]
    public void ControlsAndNestedInteractiveContentAreExcluded()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var control in new Control[]
            {
                new Button(), new CheckBox(), new TextBox(), new TextBox { IsReadOnly = true },
                new RichTextBox(), new ComboBox(), new ScrollBar(), new Thumb(),
                new ListBoxItem(), new TabItem(), new Slider(), new UserControl()
            })
            {
                var surface = new Grid();
                surface.Children.Add(control);
                Assert.IsFalse(WindowDragHitTest.CanDrag(control, surface), control.GetType().Name);
                var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0) { Cursor = Cursors.IBeam };
                WindowDragHitTest.QueryCursor(control, surface, cursor);
                Assert.AreEqual(Cursors.IBeam, cursor.Cursor, "Interactive targets retain their cursor.");
                if (control is ContentControl contentControl)
                {
                    var nested = new Border { Child = new TextBlock { Text = "Nested" } };
                    contentControl.Content = nested;
                    contentControl.ApplyTemplate();
                    Assert.IsFalse(WindowDragHitTest.CanDrag(nested.Child, surface), control.GetType().Name);
                }
            }

            var link = new Hyperlink(new Run("Link"));
            var text = new TextBlock(link);
            var root = new Border { Child = text };
            Assert.IsFalse(WindowDragHitTest.CanDrag(link.Inlines.FirstInline, root));
        });
    }

    [TestMethod]
    public void CustomMouseAndContextMenuRegionsAreExcluded()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var region in new Border[]
            {
                new() { ContextMenu = new ContextMenu() },
                new() { Cursor = Cursors.Hand },
                new() { Cursor = Cursors.IBeam },
                new() { AllowDrop = true }
            })
            {
                var nested = new TextBlock { Text = "Target" };
                region.Child = nested;
                var root = new Grid();
                root.Children.Add(region);
                Assert.IsFalse(WindowDragHitTest.CanDrag(nested, root));
            }
        });
    }

    [TestMethod]
    public void ListBackgroundOptInPreservesItemsScrollbarsAndCursorDecisions()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var itemCount in new[] { 0, 2, 30 })
            {
                var list = new ListBox
                {
                    Background = Brushes.Transparent,
                    ContextMenu = new ContextMenu(),
                    AllowDrop = true
                };
                ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
                ScrollViewer.SetPanningMode(list, PanningMode.Both);
                var root = new Border { Child = list, Width = 320, Height = 240 };
                for (var index = 0; index < itemCount; index++)
                    list.Items.Add(new ListBoxItem
                    {
                        Height = 35,
                        Cursor = Cursors.Hand,
                        Content = new TextBlock { Text = "Item " + index }
                    });
                // InputHitTest requires a presentation source for effective visibility.
                using var source = new HwndSource(new HwndSourceParameters("Noted list drag hit test")
                {
                    Width = 320,
                    Height = 240,
                    WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = root;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.Measure(new Size(320, 240));
                root.Arrange(new Rect(0, 0, 320, 240));
                root.UpdateLayout();

                Assert.IsTrue(root.IsVisible, "The list hit test requires an effectively visible visual tree.");
                var target = (DependencyObject)root.InputHitTest(new Point(100, 210));
                Assert.IsNotNull(target, $"Expected a list hit target with {itemCount} items.");
                Assert.IsFalse(WindowDragHitTest.CanDrag(target, root), "List backgrounds require opt in.");
                WindowDragHitTest.SetAllowBackgroundDrag(list, true);
                Assert.AreEqual(itemCount < 30, WindowDragHitTest.CanDrag(target, root),
                    $"Unexpected drag decision for {target.GetType().Name} with {itemCount} items.");
                var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = Mouse.QueryCursorEvent,
                    Cursor = Cursors.Arrow
                };
                WindowDragHitTest.QueryCursor(target, root, cursor);
                Assert.AreEqual(itemCount < 30 ? Cursors.SizeAll : Cursors.Arrow, cursor.Cursor);

                if (itemCount > 0)
                {
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.IsFalse(WindowDragHitTest.CanDrag((TextBlock)item.Content, root));
                }
                if (itemCount == 30)
                {
                    var scrollBar = FindVisibleVerticalScrollBar(list);
                    Assert.IsNotNull(scrollBar);
                    Assert.IsTrue(scrollBar.IsVisible);
                    Assert.IsFalse(WindowDragHitTest.CanDrag(scrollBar, root));
                }
            }
        });
    }

    [TestMethod]
    public void ListBackgroundOptInStillRequiresAnUnobstructedPathToTheSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            var list = new ListBox { ContextMenu = new ContextMenu(), AllowDrop = true };
            WindowDragHitTest.SetAllowBackgroundDrag(list, true);
            var region = new Border { Child = list };
            var root = new Border { Child = region };
            Assert.IsTrue(WindowDragHitTest.CanDrag(list, root));
            Assert.IsFalse(WindowDragHitTest.CanDrag(list, new Border()));
            region.Cursor = Cursors.Hand;
            Assert.IsFalse(WindowDragHitTest.CanDrag(list, root));
            region.Cursor = null;
            region.ContextMenu = new ContextMenu();
            Assert.IsFalse(WindowDragHitTest.CanDrag(list, root));
        });
    }

    [TestMethod]
    public void GeneratedItemsControlRowsRemainInteractiveWithoutChildControls()
    {
        WpfTestHost.Invoke(() =>
        {
            var items = new ItemsControl();
            items.Items.Add("Dictionary row");
            WindowDragHitTest.SetAllowBackgroundDrag(items, true);
            var root = new Border { Child = items, Width = 320, Height = 240 };
            root.Measure(new Size(320, 240));
            root.Arrange(new Rect(0, 0, 320, 240));
            root.UpdateLayout();
            var container = items.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.IsNotNull(container);
            Assert.IsFalse(WindowDragHitTest.CanDrag(container, root));
            Assert.IsFalse(WindowDragHitTest.CanDrag(VisualTreeHelpers.FindDescendant<TextBlock>(container), root));
            Assert.IsTrue(WindowDragHitTest.CanDrag(items, root));
        });
    }

    [TestMethod]
    public void ScrollViewerBackgroundDoesNotOptInItsContentInteraction()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox();
            var scroll = new ScrollViewer { Content = editor };
            var root = new Border { Child = scroll };
            Assert.IsTrue(WindowDragHitTest.CanDrag(scroll, root));
            Assert.IsFalse(WindowDragHitTest.CanDrag(editor, root));
            scroll.PanningMode = PanningMode.Both;
            Assert.IsFalse(WindowDragHitTest.CanDrag(scroll, root));
        });
    }

    private static ScrollBar? FindVisibleVerticalScrollBar(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollBar { Orientation: Orientation.Vertical, IsVisible: true } scrollBar)
                return scrollBar;
            if (FindVisibleVerticalScrollBar(child) is { } descendant)
                return descendant;
        }
        return null;
    }

}
