using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noted.Search;

internal sealed record SearchMatchGeometry(
    TextSearchMatch Match,
    bool IsCurrent,
    IReadOnlyList<Rect> Segments);

internal sealed record SearchHighlightGeometrySnapshot(
    Rect Viewport,
    IReadOnlyList<SearchMatchGeometry> Matches,
    Rect? CurrentVisualRow);

internal sealed class SearchHighlightOverlay : Adorner
{
    private static readonly Brush s_fallbackMatchBrush = CreateBrush(Color.FromArgb(80, 91, 168, 200));
    private static readonly Brush s_fallbackCurrentBrush = CreateBrush(Color.FromArgb(112, 91, 168, 200));
    private static readonly Brush s_fallbackCurrentBorderBrush = CreateBrush(Color.FromRgb(57, 123, 153));
    private static readonly Brush s_fallbackCurrentLineBrush = CreateBrush(Color.FromArgb(28, 91, 168, 200));

    private readonly TextBox _editor;
    private IReadOnlyList<TextSearchMatch> _matches = [];
    private TextSearchMatch? _currentMatch;

    internal SearchHighlightOverlay(TextBox editor)
        : base(editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        IsHitTestVisible = false;
        ClipToBounds = true;
        SetResourceReference(MatchBrushProperty, "NotedSearchMatchBrush");
        SetResourceReference(CurrentBrushProperty, "NotedSearchCurrentBrush");
        SetResourceReference(CurrentBorderBrushProperty, "NotedSearchCurrentBorderBrush");
        SetResourceReference(CurrentLineBrushProperty, "NotedSearchCurrentLineBrush");
    }

    internal static readonly DependencyProperty MatchBrushProperty =
        DependencyProperty.Register(
            nameof(MatchBrush),
            typeof(Brush),
            typeof(SearchHighlightOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty CurrentBrushProperty =
        DependencyProperty.Register(
            nameof(CurrentBrush),
            typeof(Brush),
            typeof(SearchHighlightOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty CurrentBorderBrushProperty =
        DependencyProperty.Register(
            nameof(CurrentBorderBrush),
            typeof(Brush),
            typeof(SearchHighlightOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty CurrentLineBrushProperty =
        DependencyProperty.Register(
            nameof(CurrentLineBrush),
            typeof(Brush),
            typeof(SearchHighlightOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal Brush? MatchBrush
    {
        get => (Brush?)GetValue(MatchBrushProperty);
        set => SetValue(MatchBrushProperty, value);
    }

    internal Brush? CurrentBrush
    {
        get => (Brush?)GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    internal Brush? CurrentBorderBrush
    {
        get => (Brush?)GetValue(CurrentBorderBrushProperty);
        set => SetValue(CurrentBorderBrushProperty, value);
    }

    internal Brush? CurrentLineBrush
    {
        get => (Brush?)GetValue(CurrentLineBrushProperty);
        set => SetValue(CurrentLineBrushProperty, value);
    }

    internal void SetSearchState(
        IReadOnlyList<TextSearchMatch> matches,
        TextSearchMatch? currentMatch)
    {
        ArgumentNullException.ThrowIfNull(matches);
        _matches = matches;
        _currentMatch = currentMatch;
        InvalidateVisual();
    }

    internal void Clear()
    {
        _matches = [];
        _currentMatch = null;
        InvalidateVisual();
    }

    internal SearchHighlightGeometrySnapshot GetGeometrySnapshot()
    {
        var viewport = GetTextViewport();
        if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0)
            return new SearchHighlightGeometrySnapshot(Rect.Empty, [], null);

        if (_matches.Count == 0)
            return new SearchHighlightGeometrySnapshot(viewport, [], null);

        var firstVisibleLine = 0;
        var lastVisibleLine = _editor.LineCount - 1;
        var anchorLine = _editor.GetFirstVisibleLineIndex();
        if (anchorLine >= 0 && anchorLine <= lastVisibleLine)
        {
            var anchorStart = _editor.GetCharacterIndexFromLineIndex(anchorLine);
            var anchorRect = _editor.GetRectFromCharacterIndex(anchorStart, false);
            if (IsUsable(anchorRect))
            {
                // overlay bounds can include padding and scrollbars beyond the text viewport.
                firstVisibleLine = Math.Clamp(anchorLine +
                    (int)Math.Floor((viewport.Top - anchorRect.Top) / anchorRect.Height) - 1,
                    0, lastVisibleLine);
                lastVisibleLine = Math.Clamp(anchorLine +
                    (int)Math.Ceiling((viewport.Bottom - anchorRect.Top) / anchorRect.Height),
                    firstVisibleLine, lastVisibleLine);
            }
        }

        var hasVisibleLines = lastVisibleLine >= firstVisibleLine;
        var visibleStart = hasVisibleLines ? _editor.GetCharacterIndexFromLineIndex(firstVisibleLine) : 0;
        var visibleEnd = hasVisibleLines
            ? _editor.GetCharacterIndexFromLineIndex(lastVisibleLine) + _editor.GetLineLength(lastVisibleLine)
            : 0;

        var geometries = new List<SearchMatchGeometry>(_matches.Count);
        foreach (var match in _matches)
        {
            if (!IsValid(match))
                continue;

            var segments = match.Start < visibleEnd && match.End > visibleStart
                ? GetVisibleSegments(match, viewport, firstVisibleLine, lastVisibleLine)
                : [];
            geometries.Add(new SearchMatchGeometry(
                match,
                _currentMatch == match,
                segments));
        }

        Rect? currentVisualRow = null;
        if (_currentMatch is { } current && IsValid(current) &&
            current.Start >= visibleStart && current.Start < visibleEnd)
        {
            var characterRect = _editor.GetRectFromCharacterIndex(current.Start, false);
            if (IsUsable(characterRect))
            {
                var row = new Rect(
                    viewport.Left,
                    characterRect.Top,
                    viewport.Width,
                    characterRect.Height);
                var clippedRow = Rect.Intersect(row, viewport);
                if (!clippedRow.IsEmpty && clippedRow.Height > 0)
                    currentVisualRow = clippedRow;
            }
        }

        return new SearchHighlightGeometrySnapshot(viewport, geometries, currentVisualRow);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var geometry = GetGeometrySnapshot();
        if (geometry.Viewport.IsEmpty)
            return;

        drawingContext.PushClip(new RectangleGeometry(geometry.Viewport));

        if (geometry.CurrentVisualRow is { } row)
        {
            drawingContext.DrawRectangle(
                CurrentLineBrush ?? s_fallbackCurrentLineBrush,
                null,
                row);
        }

        foreach (var match in geometry.Matches.Where(value => !value.IsCurrent))
        {
            foreach (var segment in match.Segments)
                drawingContext.DrawRectangle(MatchBrush ?? s_fallbackMatchBrush, null, segment);
        }

        var currentPen = new Pen(
            CurrentBorderBrush ?? s_fallbackCurrentBorderBrush,
            1);
        foreach (var match in geometry.Matches.Where(value => value.IsCurrent))
        {
            foreach (var segment in match.Segments)
            {
                drawingContext.DrawRectangle(
                    CurrentBrush ?? s_fallbackCurrentBrush,
                    currentPen,
                    segment);
            }
        }

        drawingContext.Pop();
    }

    private IReadOnlyList<Rect> GetVisibleSegments(
        TextSearchMatch match,
        Rect viewport,
        int firstVisibleLine,
        int lastVisibleLine)
    {
        var firstLine = _editor.GetLineIndexFromCharacterIndex(match.Start);
        var lastCharacterIndex = Math.Min(match.End - 1, _editor.Text.Length - 1);
        var lastLine = _editor.GetLineIndexFromCharacterIndex(lastCharacterIndex);
        if (firstLine < 0 || lastLine < firstLine)
            return [];

        var segments = new List<Rect>();
        for (var lineIndex = Math.Max(firstLine, firstVisibleLine);
            lineIndex <= Math.Min(lastLine, lastVisibleLine);
            lineIndex++)
        {
            var lineStart = _editor.GetCharacterIndexFromLineIndex(lineIndex);
            if (lineStart < 0)
                continue;

            var lineEnd = lineStart + _editor.GetLineLength(lineIndex);
            var segmentStart = Math.Max(match.Start, lineStart);
            var segmentEnd = Math.Min(match.End, lineEnd);
            if (segmentEnd <= segmentStart)
                continue;

            var leading = _editor.GetRectFromCharacterIndex(segmentStart, false);
            var trailing = _editor.GetRectFromCharacterIndex(segmentEnd - 1, true);
            if (!IsUsable(leading) || !IsUsable(trailing))
                continue;

            var left = Math.Min(leading.Left, trailing.Left);
            var right = Math.Max(leading.Right, trailing.Right);
            var top = Math.Min(leading.Top, trailing.Top);
            var bottom = Math.Max(leading.Bottom, trailing.Bottom);
            var segment = new Rect(
                left,
                top,
                Math.Max(1, right - left),
                Math.Max(1, bottom - top));
            var clippedSegment = Rect.Intersect(segment, viewport);
            if (!clippedSegment.IsEmpty && clippedSegment.Width > 0 && clippedSegment.Height > 0)
                segments.Add(clippedSegment);
        }

        return segments;
    }

    private Rect GetTextViewport()
    {
        _editor.ApplyTemplate();
        if (_editor.Template.FindName("PART_ContentHost", _editor) is FrameworkElement contentHost &&
            contentHost.ActualWidth > 0 &&
            contentHost.ActualHeight > 0)
        {
            try
            {
                return contentHost
                    .TransformToAncestor(_editor)
                    .TransformBounds(new Rect(contentHost.RenderSize));
            }
            catch (InvalidOperationException ex)
            {
                Noted.Services.ExceptionDiagnostics.Record(ex);
                // Fall back to the editor bounds while a template is being replaced.
            }
        }

        return _editor.ActualWidth > 0 && _editor.ActualHeight > 0
            ? new Rect(_editor.RenderSize)
            : Rect.Empty;
    }

    private bool IsValid(TextSearchMatch match) =>
        match.Start >= 0 &&
        match.Length > 0 &&
        match.End <= _editor.Text.Length;

    private static bool IsUsable(Rect rect) =>
        !rect.IsEmpty &&
        IsFinite(rect.Left) &&
        IsFinite(rect.Top) &&
        IsFinite(rect.Right) &&
        IsFinite(rect.Bottom) &&
        rect.Height > 0;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
