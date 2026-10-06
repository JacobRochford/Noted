using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
                var root = new Border { Child = list, Width = 320, Height = 240 };
                for (var index = 0; index < itemCount; index++)
                    list.Items.Add(new ListBoxItem
                    {
                        Height = 35,
                        Cursor = Cursors.Hand,
                        Content = new TextBlock { Text = "Item " + index }
                    });
                root.Measure(new Size(320, 240));
                root.Arrange(new Rect(0, 0, 320, 240));
                root.UpdateLayout();

                var target = (DependencyObject)root.InputHitTest(new Point(100, 210));
                Assert.IsFalse(WindowDragHitTest.CanDrag(target, root), "List backgrounds require opt in.");
                WindowDragHitTest.SetAllowBackgroundDrag(list, true);
                Assert.AreEqual(itemCount < 30, WindowDragHitTest.CanDrag(target, root));
                var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0) { Cursor = Cursors.Arrow };
                WindowDragHitTest.QueryCursor(target, root, cursor);
                Assert.AreEqual(itemCount < 30 ? Cursors.SizeAll : Cursors.Arrow, cursor.Cursor);

                if (itemCount > 0)
                {
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.IsFalse(WindowDragHitTest.CanDrag((TextBlock)item.Content, root));
                }
                if (itemCount == 30)
                {
                    var scrollBar = VisualTreeHelpers.FindDescendant<ScrollBar>(list, "PART_VerticalScrollBar");
                    Assert.IsNotNull(scrollBar);
                    Assert.IsTrue(scrollBar.IsVisible);
                    Assert.IsFalse(WindowDragHitTest.CanDrag(scrollBar, root));
                }
            }
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
}
