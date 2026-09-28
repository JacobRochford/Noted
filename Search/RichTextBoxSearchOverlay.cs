using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noted.Search;

internal sealed class RichTextBoxSearchOverlay : Adorner
{
    private readonly RichTextBox _editor;
    private RichTextSearchSnapshot? _snapshot;
    private IReadOnlyList<TextSearchMatch> _matches = [];
    private TextSearchMatch? _current;

    private static readonly DependencyProperty MatchBrushProperty = SearchHighlightOverlay.MatchBrushProperty.AddOwner(typeof(RichTextBoxSearchOverlay));
    private static readonly DependencyProperty CurrentBrushProperty = SearchHighlightOverlay.CurrentBrushProperty.AddOwner(typeof(RichTextBoxSearchOverlay));
    private static readonly DependencyProperty CurrentBorderBrushProperty = SearchHighlightOverlay.CurrentBorderBrushProperty.AddOwner(typeof(RichTextBoxSearchOverlay));
    private static readonly Brush s_matchBrush = new SolidColorBrush(Color.FromArgb(80, 91, 168, 200));
    private static readonly Brush s_currentBrush = new SolidColorBrush(Color.FromArgb(112, 91, 168, 200));

    static RichTextBoxSearchOverlay()
    {
        s_matchBrush.Freeze();
        s_currentBrush.Freeze();
    }

    internal RichTextBoxSearchOverlay(RichTextBox editor) : base(editor)
    {
        _editor = editor;
        IsHitTestVisible = false;
        SetResourceReference(MatchBrushProperty, "NotedSearchMatchBrush");
        SetResourceReference(CurrentBrushProperty, "NotedSearchCurrentBrush");
        SetResourceReference(CurrentBorderBrushProperty, "NotedSearchCurrentBorderBrush");
    }

    internal void SetSearchState(RichTextSearchSnapshot? snapshot, IReadOnlyList<TextSearchMatch> matches, TextSearchMatch? current)
    {
        _snapshot = snapshot;
        _matches = matches;
        _current = current;
        InvalidateVisual();
    }

    internal Rect Viewport
    {
        get
        {
            _editor.ApplyTemplate();
            var host = FindVisual<ScrollContentPresenter>(_editor);
            return host is null ? new Rect(_editor.RenderSize) :
                host.TransformToAncestor(_editor).TransformBounds(new Rect(host.RenderSize));
        }
    }

    internal IReadOnlyList<Rect> GetSegments(TextSearchMatch match)
    {
        var segments = new List<Rect>();
        if (_snapshot is null || match.Start < 0 || match.End > _snapshot.Text.Length)
            return segments;
        for (var index = match.Start; index < match.End; index++)
        {
            var start = _snapshot.GetStart(index).GetCharacterRect(LogicalDirection.Forward);
            var end = _snapshot.GetEnd(index).GetCharacterRect(LogicalDirection.Backward);
            if (start.IsEmpty || end.IsEmpty || start.Height <= 0 || Math.Abs(start.Top - end.Top) > 0.5)
                continue;
            var rect = new Rect(Math.Min(start.Left, end.Left), Math.Min(start.Top, end.Top),
                Math.Max(1, Math.Abs(end.Left - start.Left)), Math.Max(start.Bottom, end.Bottom) - Math.Min(start.Top, end.Top));
            if (segments.Count > 0 && Math.Abs(segments[^1].Top - rect.Top) < 0.5 &&
                Math.Abs(segments[^1].Height - rect.Height) < 0.5 &&
                rect.Left <= segments[^1].Right + 1 && rect.Right >= segments[^1].Left - 1)
            {
                var combined = segments[^1];
                combined.Union(rect);
                segments[^1] = combined;
            }
            else
                segments.Add(rect);
        }
        return segments;
    }

    internal SearchHighlightGeometrySnapshot GetGeometrySnapshot()
    {
        var viewport = Viewport;
        return new(viewport, _matches.Select(match => new SearchMatchGeometry(match, match == _current,
            GetSegments(match).Select(rect => Rect.Intersect(rect, viewport))
                .Where(rect => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0).ToArray())).ToArray(), null);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var geometry = GetGeometrySnapshot();
        drawingContext.PushClip(new RectangleGeometry(geometry.Viewport));
        foreach (var match in geometry.Matches.OrderBy(match => match.IsCurrent))
        {
            var brush = (Brush?)GetValue(match.IsCurrent ? CurrentBrushProperty : MatchBrushProperty)
                ?? (match.IsCurrent ? s_currentBrush : s_matchBrush);
            var pen = match.IsCurrent ? new Pen((Brush?)GetValue(CurrentBorderBrushProperty) ?? Brushes.SteelBlue, 1) : null;
            foreach (var segment in match.Segments)
                drawingContext.DrawRectangle(brush, pen, segment);
        }
        drawingContext.Pop();
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
                return result;
            if (FindVisual<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
