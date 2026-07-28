using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Noted.Helpers;

internal static class VisualTreeHelpers
{
    internal static T? FindDescendant<T>(
        DependencyObject? parent,
        string? elementName = null)
        where T : DependencyObject
    {
        if (parent is null)
            return null;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                if (string.IsNullOrEmpty(elementName) ||
                    child is FrameworkElement element && element.Name == elementName)
                {
                    return match;
                }
            }

            var descendant = FindDescendant<T>(child, elementName);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    internal static T? FindAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T match)
                return match;

            var logicalParent = LogicalTreeHelper.GetParent(current);
            if (logicalParent is not null)
                current = logicalParent;
            else if (current is Visual || current is Visual3D)
                current = VisualTreeHelper.GetParent(current);
            else
                current = null;
        }

        return null;
    }
}
