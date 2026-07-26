using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Noted.Helpers;

namespace Noted;

public partial class MicroScratchpadWindow : Window
{
    public MicroScratchpadWindow()
    {
        InitializeComponent();
        SourceInitialized += MicroScratchpadWindow_SourceInitialized;
        Loaded += MicroScratchpadWindow_Loaded;
        Closed += MicroScratchpadWindow_Closed;
    }

    private void MicroScratchpadWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.RemoveMinimizeAndMaximizeBoxes(new WindowInteropHelper(this).Handle);
    }

    private void MicroScratchpadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, Editor.Focus);
    }

    private void MicroScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        SourceInitialized -= MicroScratchpadWindow_SourceInitialized;
        Loaded -= MicroScratchpadWindow_Loaded;
        Closed -= MicroScratchpadWindow_Closed;
    }
}
