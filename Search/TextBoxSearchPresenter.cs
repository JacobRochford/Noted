using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace Noted.Search;

internal sealed class TextBoxSearchPresenter : IDisposable
{
    private static readonly DependencyProperty[] s_layoutProperties =
    [
        TextBox.TextWrappingProperty,
        TextBox.FontFamilyProperty,
        TextBox.FontSizeProperty,
        TextBox.FontStyleProperty,
        TextBox.FontWeightProperty,
        TextBox.FontStretchProperty,
        TextBox.PaddingProperty,
        TextBox.FlowDirectionProperty
    ];

    private readonly TextBox _editor;
    private readonly SearchHighlightOverlay _overlay;
    private readonly List<DependencyPropertyDescriptor> _layoutPropertyDescriptors = [];
    private AdornerLayer? _adornerLayer;
    private TextSearchMatch? _currentMatch;
    private int _userSelectionStart;
    private int _userSelectionLength;
    private int _scrollRequestVersion;
    private bool _hasPendingManualNavigationAnchor;
    private bool _applyingSearchSelection;
    private bool _disposed;

    internal TextBoxSearchPresenter(TextBox editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _overlay = new SearchHighlightOverlay(editor);
        CaptureUserSelection();

        _editor.Loaded += Editor_Loaded;
        _editor.Unloaded += Editor_Unloaded;
        _editor.SizeChanged += Editor_SizeChanged;
        _editor.TextChanged += Editor_TextChanged;
        _editor.SelectionChanged += Editor_SelectionChanged;
        _editor.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(Editor_ScrollChanged));

        foreach (var property in s_layoutProperties)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(TextBox));
            if (descriptor is null)
                continue;

            descriptor.AddValueChanged(_editor, EditorLayoutProperty_Changed);
            _layoutPropertyDescriptors.Add(descriptor);
        }

        if (_editor.IsLoaded)
            AttachOverlay();
    }

    internal int InvalidationVersion { get; private set; }

    internal void BeginFind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CaptureUserSelection();
        _hasPendingManualNavigationAnchor = false;
    }

    internal void Present(
        TextSearchSession session,
        bool bringCurrentIntoView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);

        AttachOverlay();
        _currentMatch = session.CurrentMatch;
        _overlay.SetSearchState(session.Matches, session.CurrentMatch);
        InvalidationVersion++;

        if (bringCurrentIntoView && session.CurrentMatch is not null)
            BringCurrentIntoView();
    }

    internal int GetQueryRefreshAnchor() =>
        Math.Clamp(_userSelectionStart, 0, _editor.Text.Length);

    internal int? ConsumePendingManualNavigationAnchor(bool forward)
    {
        if (!_hasPendingManualNavigationAnchor)
            return null;

        _hasPendingManualNavigationAnchor = false;
        var selectionStart = Math.Clamp(_userSelectionStart, 0, _editor.Text.Length);
        var selectionLength = Math.Clamp(
            _userSelectionLength,
            0,
            _editor.Text.Length - selectionStart);
        return forward
            ? Math.Clamp(selectionStart + selectionLength, 0, _editor.Text.Length)
            : selectionStart;
    }

    internal void EndFind(TextSearchMatch? currentMatch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (currentMatch is { } match)
            SynchronizeSearchSelection(match);

        _hasPendingManualNavigationAnchor = false;
        Clear();
        CaptureUserSelection();
    }

    internal void BringCurrentIntoView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestVersion = ++_scrollRequestVersion;
        BringCurrentIntoView(requestVersion, allowCorrection: true);
    }

    internal void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scrollRequestVersion++;
        _currentMatch = null;
        _overlay.Clear();
        InvalidationVersion++;
    }

    internal void InvalidateLayout()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvalidateGeometry();
    }

    internal void RequestRenderInvalidation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = _editor.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(InvalidateGeometry));
    }

    internal SearchHighlightGeometrySnapshot GetGeometrySnapshot() =>
        _overlay.GetGeometrySnapshot();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _scrollRequestVersion++;
        DetachOverlay();
        _editor.Loaded -= Editor_Loaded;
        _editor.Unloaded -= Editor_Unloaded;
        _editor.SizeChanged -= Editor_SizeChanged;
        _editor.TextChanged -= Editor_TextChanged;
        _editor.SelectionChanged -= Editor_SelectionChanged;
        _editor.RemoveHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(Editor_ScrollChanged));

        foreach (var descriptor in _layoutPropertyDescriptors)
            descriptor.RemoveValueChanged(_editor, EditorLayoutProperty_Changed);
        _layoutPropertyDescriptors.Clear();
    }

    private void BringCurrentIntoView(int requestVersion, bool allowCorrection)
    {
        if (_disposed || requestVersion != _scrollRequestVersion || _currentMatch is not { } match)
            return;
        if (!_editor.IsLoaded || match.Start < 0 || match.Start >= _editor.Text.Length)
            return;

        var scrolled = RevealVertical(match);
        if (_editor.TextWrapping == TextWrapping.NoWrap)
            scrolled |= RevealHorizontal(match);

        if (!scrolled || !allowCorrection)
            return;

        _ = _editor.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => BringCurrentIntoView(requestVersion, allowCorrection: false)));
    }

    private void SynchronizeSearchSelection(TextSearchMatch currentMatch)
    {
        if (currentMatch.Start < 0 ||
            currentMatch.Length <= 0 ||
            currentMatch.End > _editor.Text.Length)
        {
            return;
        }

        _applyingSearchSelection = true;
        try
        {
            _editor.Select(currentMatch.Start, currentMatch.Length);
        }
        finally
        {
            _applyingSearchSelection = false;
        }
    }

    private bool RevealVertical(TextSearchMatch match)
    {
        if (_editor.ViewportHeight <= 0 ||
            _editor.ExtentHeight <= _editor.ViewportHeight)
        {
            return false;
        }

        var firstMatchLine = _editor.GetLineIndexFromCharacterIndex(match.Start);
        var lastCharacterIndex = Math.Min(match.End - 1, _editor.Text.Length - 1);
        var lastMatchLine = _editor.GetLineIndexFromCharacterIndex(lastCharacterIndex);
        var firstVisibleLine = _editor.GetFirstVisibleLineIndex();
        if (firstMatchLine < 0 ||
            lastMatchLine < firstMatchLine ||
            firstVisibleLine < 0 ||
            !TryGetVisualLineMetrics(firstVisibleLine, out var firstVisibleLineTop, out var lineHeight))
        {
            return false;
        }

        // SearchViewportPolicy is unit-agnostic. Convert visual-line positions to the
        // TextBox vertical scroll coordinate system (device-independent units) before resolving.
        var firstVisibleLineContentTop =
            _editor.VerticalOffset + firstVisibleLineTop;
        var resultStart = firstVisibleLineContentTop +
            ((firstMatchLine - firstVisibleLine) * lineHeight);
        var resultEnd = firstVisibleLineContentTop +
            ((lastMatchLine - firstVisibleLine + 1) * lineHeight);
        var context = _editor.ViewportHeight >= lineHeight * 3
            ? lineHeight
            : 0;
        var adjustment = SearchViewportPolicy.Resolve(new SearchViewportAxis(
            _editor.VerticalOffset,
            _editor.ViewportHeight,
            Math.Max(_editor.ExtentHeight, _editor.ViewportHeight),
            resultStart,
            resultEnd,
            context));
        if (!adjustment.RequiresScroll)
            return false;

        _editor.ScrollToVerticalOffset(adjustment.Offset);
        return true;
    }

    private bool TryGetVisualLineMetrics(
        int lineIndex,
        out double lineTop,
        out double lineHeight)
    {
        lineTop = 0;
        lineHeight = 0;

        var characterIndex = _editor.GetCharacterIndexFromLineIndex(lineIndex);
        if (characterIndex < 0)
            return false;

        var lineRect = _editor.GetRectFromCharacterIndex(characterIndex, false);
        var viewport = _overlay.GetGeometrySnapshot().Viewport;
        if (!IsUsableVertical(lineRect) || viewport.IsEmpty)
            return false;

        lineTop = lineRect.Top - viewport.Top;
        lineHeight = lineRect.Height;
        return lineHeight > 0;
    }

    private bool RevealHorizontal(TextSearchMatch match)
    {
        if (GetContentHost() is not ScrollViewer scrollViewer || scrollViewer.ViewportWidth <= 0)
            return false;

        var viewport = _overlay.GetGeometrySnapshot().Viewport;
        var leading = _editor.GetRectFromCharacterIndex(match.Start, false);
        var lastCharacter = Math.Min(match.End - 1, _editor.Text.Length - 1);
        var trailing = _editor.GetRectFromCharacterIndex(lastCharacter, true);
        if (viewport.IsEmpty || !IsUsable(leading) || !IsUsable(trailing))
            return false;

        var resultStart = scrollViewer.HorizontalOffset +
            Math.Min(leading.Left, trailing.Left) - viewport.Left;
        var resultEnd = scrollViewer.HorizontalOffset +
            Math.Max(leading.Right, trailing.Right) - viewport.Left;
        var context = Math.Min(leading.Height, scrollViewer.ViewportWidth / 4);
        var adjustment = SearchViewportPolicy.Resolve(new SearchViewportAxis(
            scrollViewer.HorizontalOffset,
            scrollViewer.ViewportWidth,
            Math.Max(scrollViewer.ExtentWidth, scrollViewer.ViewportWidth),
            resultStart,
            resultEnd,
            context));
        if (!adjustment.RequiresScroll)
            return false;

        _editor.ScrollToHorizontalOffset(adjustment.Offset);
        return true;
    }

    private FrameworkElement? GetContentHost()
    {
        _editor.ApplyTemplate();
        return _editor.Template.FindName("PART_ContentHost", _editor) as FrameworkElement;
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
        if (_adornerLayer is null)
            return;

        _adornerLayer.Remove(_overlay);
        _adornerLayer = null;
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        AttachOverlay();
        InvalidateGeometry();
    }

    private void Editor_Unloaded(object sender, RoutedEventArgs e) => DetachOverlay();

    private void Editor_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateGeometry();

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => InvalidateGeometry();

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingSearchSelection)
            return;

        CaptureUserSelection();
        _hasPendingManualNavigationAnchor = true;
    }

    private void CaptureUserSelection()
    {
        _userSelectionStart = Math.Clamp(
            _editor.SelectionStart,
            0,
            _editor.Text.Length);
        _userSelectionLength = Math.Clamp(
            _editor.SelectionLength,
            0,
            _editor.Text.Length - _userSelectionStart);
    }

    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateGeometry();

    private void EditorLayoutProperty_Changed(object? sender, EventArgs e) => InvalidateGeometry();

    private void InvalidateGeometry()
    {
        if (_disposed)
            return;

        InvalidationVersion++;
        _overlay.InvalidateVisual();
    }

    private static bool IsUsable(Rect rect) =>
        !rect.IsEmpty &&
        !double.IsNaN(rect.Left) &&
        !double.IsInfinity(rect.Left) &&
        !double.IsNaN(rect.Right) &&
        !double.IsInfinity(rect.Right);

    private static bool IsUsableVertical(Rect rect) =>
        IsUsable(rect) &&
        !double.IsNaN(rect.Top) &&
        !double.IsInfinity(rect.Top) &&
        !double.IsNaN(rect.Bottom) &&
        !double.IsInfinity(rect.Bottom) &&
        rect.Height > 0;
}
