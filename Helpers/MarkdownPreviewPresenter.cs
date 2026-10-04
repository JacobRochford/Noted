using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Models;

namespace Noted.Helpers;

// Owns presentation only. The source TextBox remains the owner of editing and undo.
internal sealed class MarkdownPreviewPresenter : IDisposable
{
    private readonly TextBox _editor;
    private readonly FrameworkElement _source;
    private readonly FlowDocumentScrollViewer _preview;
    private readonly Grid _layout;
    private readonly DispatcherTimer _timer;
    private bool _previewOwnsScroll;
    private string? _renderedText;

    internal MarkdownPreviewPresenter(TextBox editor, FrameworkElement source,
        FlowDocumentScrollViewer preview, Grid layout)
    {
        _editor = editor;
        _source = source;
        _preview = preview;
        _layout = layout;
        _timer = new DispatcherTimer(DispatcherPriority.Background, editor.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += Timer_Tick;
        editor.TextChanged += Editor_TextChanged;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
        preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Preview_ScrollChanged));
        editor.PreviewMouseDown += Source_Input;
        editor.PreviewMouseWheel += Source_Input;
        editor.PreviewKeyDown += Source_Input;
        preview.PreviewMouseDown += Preview_Input;
        preview.PreviewMouseWheel += Preview_Input;
        preview.PreviewKeyDown += Preview_Input;
    }

    internal MarkdownPreviewMode Mode { get; private set; }

    internal void SetMode(MarkdownPreviewMode mode)
    {
        _previewOwnsScroll = false;
        Mode = Enum.IsDefined(mode) ? mode : MarkdownPreviewMode.Off;
        _source.Visibility = Mode == MarkdownPreviewMode.Preview ? Visibility.Collapsed : Visibility.Visible;
        _preview.Visibility = Mode == MarkdownPreviewMode.Off ? Visibility.Collapsed : Visibility.Visible;
        _layout.ColumnDefinitions[0].Width = Mode == MarkdownPreviewMode.Preview
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _layout.ColumnDefinitions[1].Width = Mode == MarkdownPreviewMode.Off
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (Mode == MarkdownPreviewMode.Off)
            _timer.Stop();
        else
        {
            Refresh();
            if (Mode == MarkdownPreviewMode.SideBySide)
            {
                _preview.UpdateLayout();
                SynchronizeFromEditor();
            }
        }
    }

    internal void Refresh()
    {
        _timer.Stop();
        if (Mode == MarkdownPreviewMode.Off || _renderedText == _editor.Text)
            return;
        var scroll = FindScrollViewer(_preview);
        var progress = ScrollProgress(scroll?.VerticalOffset ?? 0, scroll?.ScrollableHeight ?? 0);
        _renderedText = _editor.Text;
        _preview.Document = MarkdownRenderer.Render(_renderedText);
        _preview.UpdateLayout();
        scroll = FindScrollViewer(_preview);
        if (scroll is not null)
            scroll.ScrollToVerticalOffset(progress * scroll.ScrollableHeight);
        if (Mode == MarkdownPreviewMode.SideBySide && !_previewOwnsScroll)
            SynchronizeFromEditor();
    }

    internal static double ScrollProgress(double offset, double scrollableHeight) =>
        double.IsFinite(offset) && double.IsFinite(scrollableHeight) && scrollableHeight > 0
            ? Math.Clamp(offset / scrollableHeight, 0, 1) : 0;

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // A running timer uses the latest source text, bounding updates during continuous typing.
        if (Mode != MarkdownPreviewMode.Off && !_timer.IsEnabled)
            _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Refresh();
    private void Source_Input(object sender, InputEventArgs e) => _previewOwnsScroll = false;
    private void Preview_Input(object sender, InputEventArgs e) => _previewOwnsScroll = true;

    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Mode == MarkdownPreviewMode.SideBySide && !_previewOwnsScroll)
            SynchronizeFromEditor();
    }

    private void SynchronizeFromEditor()
    {
        var scroll = FindScrollViewer(_preview);
        if (scroll is not null)
            scroll.ScrollToVerticalOffset(ScrollProgress(_editor.VerticalOffset,
                _editor.ExtentHeight - _editor.ViewportHeight) * scroll.ScrollableHeight);
    }

    private void Preview_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only the pane receiving user input drives synchronization, including deferred scroll events.
        if (Mode != MarkdownPreviewMode.SideBySide || !_previewOwnsScroll || e.VerticalChange == 0)
            return;
        var scroll = FindScrollViewer(_preview);
        if (scroll is not null)
            _editor.ScrollToVerticalOffset(ScrollProgress(scroll.VerticalOffset, scroll.ScrollableHeight) *
                Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, index));
            if (result is not null) return result;
        }
        return null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _editor.TextChanged -= Editor_TextChanged;
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
        _preview.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Preview_ScrollChanged));
        _editor.PreviewMouseDown -= Source_Input;
        _editor.PreviewMouseWheel -= Source_Input;
        _editor.PreviewKeyDown -= Source_Input;
        _preview.PreviewMouseDown -= Preview_Input;
        _preview.PreviewMouseWheel -= Preview_Input;
        _preview.PreviewKeyDown -= Preview_Input;
    }
}
