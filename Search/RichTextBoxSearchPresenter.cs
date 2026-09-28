using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace Noted.Search;

internal sealed class RichTextBoxSearchPresenter : IDisposable
{
    private readonly RichTextBox _editor;
    private readonly RichTextBoxSearchOverlay _overlay;
    private AdornerLayer? _adornerLayer;
    private RichTextSearchSnapshot? _snapshot;
    private TextSearchMatch? _currentMatch;
    private int _userSelectionStart;
    private int _userSelectionLength;
    private bool _hasPendingManualNavigationAnchor;
    private bool _isFindOpen;
    private bool _disposed;
    private int _scrollRequestVersion;
    private (Size Size, double Width, double Height, double PageWidth) _lastLayout;

    internal RichTextBoxSearchPresenter(RichTextBox editor)
    {
        _editor = editor;
        _overlay = new RichTextBoxSearchOverlay(editor);
        _editor.SelectionChanged += Editor_SelectionChanged;
        _editor.TextChanged += Editor_TextChanged;
        _editor.Loaded += Editor_Loaded;
        _editor.Unloaded += Editor_Unloaded;
        _editor.LayoutUpdated += Editor_LayoutUpdated;
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
        if (_editor.IsLoaded)
            AttachOverlay();
    }

    internal bool IsApplyingReplacement { get; private set; }

    internal void BeginFind()
    {
        _isFindOpen = true;
        CaptureSelection();
        ClearPendingManualNavigationAnchor();
    }

    internal void EndFind()
    {
        _isFindOpen = false;
        _scrollRequestVersion++;
        _snapshot = null;
        _currentMatch = null;
        ClearPendingManualNavigationAnchor();
        _overlay.SetSearchState(null, [], null);
    }

    internal void ClearPendingManualNavigationAnchor() => _hasPendingManualNavigationAnchor = false;

    internal int? ConsumePendingManualNavigationAnchor()
    {
        if (!_hasPendingManualNavigationAnchor)
            return null;
        ClearPendingManualNavigationAnchor();
        return _userSelectionStart;
    }

    internal void Refresh(TextSearchSession session, string query, TextSearchOptions options, int? navigationAnchor = null)
    {
        _snapshot = new RichTextSearchSnapshot(_editor.Document);
        session.Refresh(_snapshot.Text, _editor.Document, query, options,
            navigationAnchor ?? _snapshot.GetOffset(_editor.Selection.Start));
    }

    internal void Present(TextSearchSession session, bool bringCurrentIntoView)
    {
        AttachOverlay();
        if (_currentMatch is not null && _currentMatch != session.CurrentMatch)
            _scrollRequestVersion++;
        _currentMatch = session.CurrentMatch;
        _overlay.SetSearchState(_snapshot, session.Matches, session.CurrentMatch);
        if (bringCurrentIntoView && _currentMatch is not null)
        {
            var version = ++_scrollRequestVersion;
            BringCurrentIntoView();
            _ = _editor.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (!_disposed && version == _scrollRequestVersion)
                    BringCurrentIntoView();
            }));
        }
    }

    internal void Replace(IReadOnlyList<TextSearchMatch> matches, string replacement)
    {
        var snapshot = _snapshot ?? new RichTextSearchSnapshot(_editor.Document);
        IsApplyingReplacement = true;
        try
        {
            foreach (var match in matches.OrderByDescending(match => match.Start))
                snapshot.CreateRange(match).Text = replacement;
        }
        finally
        {
            IsApplyingReplacement = false;
            CaptureSelection();
            ClearPendingManualNavigationAnchor();
        }
    }

    internal SearchHighlightGeometrySnapshot GetGeometrySnapshot() => _overlay.GetGeometrySnapshot();

    private void CaptureSelection()
    {
        var snapshot = new RichTextSearchSnapshot(_editor.Document);
        _userSelectionStart = snapshot.GetOffset(_editor.Selection.Start);
        _userSelectionLength = snapshot.GetOffset(_editor.Selection.End) - _userSelectionStart;
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isFindOpen || IsApplyingReplacement)
            return;
        var previous = (_userSelectionStart, _userSelectionLength);
        CaptureSelection();
        if (previous != (_userSelectionStart, _userSelectionLength))
            _hasPendingManualNavigationAnchor = true;
    }

    private void BringCurrentIntoView()
    {
        if (!_editor.IsLoaded || _currentMatch is not { } match)
            return;
        _editor.UpdateLayout();
        var segments = _overlay.GetSegments(match);
        var viewport = _overlay.Viewport;
        if (segments.Count == 0 || viewport.IsEmpty)
            return;
        var bounds = segments.Aggregate(Rect.Empty, (rect, segment) => Rect.Union(rect, segment));
        var vertical = SearchViewportPolicy.Resolve(new(_editor.VerticalOffset, viewport.Height, _editor.ExtentHeight,
            _editor.VerticalOffset + bounds.Top - viewport.Top, _editor.VerticalOffset + bounds.Bottom - viewport.Top, 0));
        var horizontal = SearchViewportPolicy.Resolve(new(_editor.HorizontalOffset, viewport.Width, _editor.ExtentWidth,
            _editor.HorizontalOffset + bounds.Left - viewport.Left, _editor.HorizontalOffset + bounds.Right - viewport.Left, 0));
        if (vertical.RequiresScroll)
            _editor.ScrollToVerticalOffset(vertical.Offset);
        if (horizontal.RequiresScroll)
            _editor.ScrollToHorizontalOffset(horizontal.Offset);
    }

    private void AttachOverlay()
    {
        if (_adornerLayer is not null || !_editor.IsLoaded)
            return;
        _adornerLayer = AdornerLayer.GetAdornerLayer(_editor);
        _adornerLayer?.Add(_overlay);
    }

    private void DetachOverlay()
    {
        _adornerLayer?.Remove(_overlay);
        _adornerLayer = null;
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e) => AttachOverlay();
    private void Editor_Unloaded(object sender, RoutedEventArgs e) => DetachOverlay();
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => _overlay.InvalidateVisual();
    private void Editor_LayoutUpdated(object? sender, EventArgs e)
    {
        var layout = (_editor.RenderSize, _editor.ExtentWidth, _editor.ExtentHeight, _editor.Document.PageWidth);
        if (_lastLayout.Equals(layout))
            return;
        _lastLayout = layout;
        _overlay.InvalidateVisual();
    }
    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e) => _overlay.InvalidateVisual();

    public void Dispose()
    {
        if (_disposed)
            return;
        EndFind();
        _disposed = true;
        DetachOverlay();
        _editor.SelectionChanged -= Editor_SelectionChanged;
        _editor.TextChanged -= Editor_TextChanged;
        _editor.Loaded -= Editor_Loaded;
        _editor.Unloaded -= Editor_Unloaded;
        _editor.LayoutUpdated -= Editor_LayoutUpdated;
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
    }
}
