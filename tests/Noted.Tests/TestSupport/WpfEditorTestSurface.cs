using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Noted.Tests.Services;

internal sealed class WpfEditorTestSurface : IDisposable
{
    internal WpfEditorTestSurface(TextBoxBase editor, FrameworkElement? footer = null)
    {
        Source = new HwndSource(new HwndSourceParameters("Noted editor undo test")
        {
            Width = 400,
            Height = 200,
            WindowStyle = unchecked((int)0x80000000)
        });

        try
        {
            var content = new DockPanel();
            if (footer is not null)
            {
                DockPanel.SetDock(footer, Dock.Bottom);
                content.Children.Add(footer);
            }
            content.Children.Add(editor);
            var root = new AdornerDecorator { Width = 400, Height = 200, Child = content };
            Source.RootVisual = root;

            // WPF clears change-block undo records until the control has a TextView.
            // Create the template and perform layout before any selection or edit.
            root.Measure(new Size(400, 200));
            root.Arrange(new Rect(0, 0, 400, 200));
            root.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        catch
        {
            Source.Dispose();
            throw;
        }
    }

    internal HwndSource Source { get; }

    public void Dispose() => Source.Dispose();
}
