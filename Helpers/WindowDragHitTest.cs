using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Noted.Helpers;

internal static class WindowDragHitTest
{
    // Opt in only list hosts whose unused space has no left-button content gesture.
    // This does not opt in their item containers or other interactive descendants.
    public static readonly DependencyProperty AllowBackgroundDragProperty = DependencyProperty.RegisterAttached(
        "AllowBackgroundDrag", typeof(bool), typeof(WindowDragHitTest), new PropertyMetadata(false));

    public static bool GetAllowBackgroundDrag(DependencyObject element) =>
        (bool)element.GetValue(AllowBackgroundDragProperty);

    public static void SetAllowBackgroundDrag(DependencyObject element, bool value) =>
        element.SetValue(AllowBackgroundDragProperty, value);

    internal static bool CanDrag(DependencyObject? source, DependencyObject surface)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current == surface)
                return true;

            if (current is Hyperlink ||
                ItemsControl.ItemsControlFromItemContainer(current) is not null)
                return false;

            var listBackground = current is ItemsControl && GetAllowBackgroundDrag(current);
            if (current is Control && !listBackground && current is not ScrollViewer)
                return false;

            if (current is ScrollViewer { PanningMode: not PanningMode.None })
                return false;

            if (current is FrameworkElement element)
            {
                // AllowDrop can be inherited from the list host. Judge its owner below,
                // rather than treating the list's scrolling/template visuals as drop regions.
                var ownsDropInteraction = element.AllowDrop &&
                    DependencyPropertyHelper.GetValueSource(element, UIElement.AllowDropProperty).BaseValueSource != BaseValueSource.Inherited;
                if (!listBackground && (element.ContextMenu is not null || ownsDropInteraction))
                    return false;

                // Custom clickable text and shapes also advertise their own input cursor.
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
