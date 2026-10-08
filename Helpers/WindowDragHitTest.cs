using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Noted.Helpers;

internal static class WindowDragHitTest
{
    public static readonly DependencyProperty AllowBackgroundDragProperty = DependencyProperty.RegisterAttached(
        "AllowBackgroundDrag", typeof(bool), typeof(WindowDragHitTest), new PropertyMetadata(false));

    public static bool GetAllowBackgroundDrag(DependencyObject element) =>
        (bool)element.GetValue(AllowBackgroundDragProperty);

    public static void SetAllowBackgroundDrag(DependencyObject element, bool value) =>
        element.SetValue(AllowBackgroundDragProperty, value);

    internal static bool CanDrag(DependencyObject? source, DependencyObject surface)
    {
        var backgroundDragHost = FindBackgroundDragHost(source, surface);
        if (backgroundDragHost is not null)
            return CanDragThroughBackgroundHost(source, surface, backgroundDragHost);

        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current == surface)
                return true;

            if (current is Hyperlink ||
                ItemsControl.ItemsControlFromItemContainer(current) is not null)
                return false;

            if (current is Control && current is not ScrollViewer)
                return false;

            if (current is ScrollViewer { PanningMode: not PanningMode.None })
                return false;

            if (current is FrameworkElement element)
            {
                var ownsDropInteraction = element.AllowDrop &&
                    DependencyPropertyHelper.GetValueSource(element, UIElement.AllowDropProperty).BaseValueSource != BaseValueSource.Inherited;
                if (element.ContextMenu is not null || ownsDropInteraction)
                    return false;

                if (element.Cursor is { } cursor && cursor != Cursors.Arrow && cursor != Cursors.SizeAll)
                    return false;
            }

            if (current is FrameworkContentElement { ContextMenu: not null })
                return false;
        }

        return false;
    }

    internal static void QueryCursor(DependencyObject? source, DependencyObject surface, QueryCursorEventArgs e)
    {
        if (Mouse.Captured is not null && Mouse.Captured != surface || !CanDrag(source, surface))
            return;

        e.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private static ItemsControl? FindBackgroundDragHost(DependencyObject? source, DependencyObject surface)
    {
        for (var current = source; current is not null && current != surface; current = GetParent(current))
        {
            // don't allow background dragging on list items
        
            if (ItemsControl.ItemsControlFromItemContainer(current) is not null)
                return null;

            if (current is ItemsControl itemsControl && GetAllowBackgroundDrag(itemsControl))
                return itemsControl;
        }

        return null;
    }

    private static bool CanDragThroughBackgroundHost(
        DependencyObject? source,
        DependencyObject surface,
        ItemsControl backgroundDragHost)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current == backgroundDragHost)
                return CanDrag(GetParent(backgroundDragHost), surface);

            if (current == surface)
                return false;

            if (current is Hyperlink ||
                ItemsControl.ItemsControlFromItemContainer(current) is not null)
                return false;

            // allow ScrollViewer backgrounds, but no other controls
            if (current is Control && current is not ScrollViewer)
                return false;

            if (current is FrameworkElement element)
            {
                var ownsDropInteraction = element.AllowDrop &&
                    DependencyPropertyHelper.GetValueSource(element, UIElement.AllowDropProperty).BaseValueSource != BaseValueSource.Inherited;
                if (element.ContextMenu is not null || ownsDropInteraction)
                    return false;

                var cursorSource = DependencyPropertyHelper.GetValueSource(element, FrameworkElement.CursorProperty);
                var ownsCursor = cursorSource.BaseValueSource != BaseValueSource.Inherited &&
                    cursorSource.BaseValueSource != BaseValueSource.Default;
                if (ownsCursor && element.Cursor is { } cursor && cursor != Cursors.Arrow && cursor != Cursors.SizeAll)
                    return false;
            }

            if (current is FrameworkContentElement { ContextMenu: not null })
                return false;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual or Visual3D)
            return VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
        if (element is ContentElement content)
            return ContentOperations.GetParent(content) ??
                (element as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(element);
        return LogicalTreeHelper.GetParent(element);
    }
}
