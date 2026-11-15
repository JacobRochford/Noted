using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;

namespace Noted;

public partial class NoteEditorWindow
{
    private TabDragFeedback? _tabDragFeedback;
    private AdornerLayer? _tabDragLayer;
    private DispatcherTimer? _tabScrollTimer;
    private int _tabScrollDirection;
    private Point _tabDragPosition;
    private HwndSource? _tabDragSource;
    private int _tabDragWheelDelta;

    private OpenNoteDocument? GetDraggedTab(IDataObject data) =>
        data.GetDataPresent(typeof(OpenNoteDocument)) &&
        data.GetData(typeof(OpenNoteDocument)) is OpenNoteDocument document && _documents.Contains(document)
            ? document : null;

    private void BeginTabDragFeedback()
    {
        _tabDragSource = PresentationSource.FromVisual(this) as HwndSource;
        _tabDragSource?.AddHook(TabDragWindowMessage);
        _tabDragWheelDelta = 0;
        var root = (FrameworkElement)Content;
        _tabDragLayer = AdornerLayer.GetAdornerLayer(root);
        if (_tabDragLayer is not null)
        {
            _tabDragFeedback = new TabDragFeedback(root) { IsHitTestVisible = false };
            _tabDragLayer.Add(_tabDragFeedback);
        }
    }

    private void EndTabDragFeedback()
    {
        _tabDragSource?.RemoveHook(TabDragWindowMessage);
        _tabDragSource = null;
        _tabDragWheelDelta = 0;
        ClearTabDragFeedback();
        if (_tabDragFeedback is not null) _tabDragLayer?.Remove(_tabDragFeedback);
        _tabDragFeedback = null;
        _tabDragLayer = null;
    }

    private void ClearTabDragFeedback()
    {
        StopTabAutoScroll();
        if (_tabDragFeedback is null) return;
        _tabDragFeedback.Pane = null;
        _tabDragFeedback.Insertion = null;
        _tabDragFeedback.Label = null;
        _tabDragFeedback.InvalidateVisual();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (!new Rect(RenderSize).Contains(e.GetPosition(this)))
            ClearTabDragFeedback();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        // reset feedback before the destination's preview handler runs.
        if (!new Rect(OpenTabsList.RenderSize).Contains(e.GetPosition(OpenTabsList))) StopTabAutoScroll();
        if (_tabDragFeedback is not null)
        {
            _tabDragFeedback.Pane = null;
            _tabDragFeedback.Insertion = null;
        }
        if (GetDraggedTab(e.Data) is not { } document)
        {
            ClearTabDragFeedback();
            return;
        }
        if (_tabDragFeedback is null) return;
        _tabDragFeedback.Label = document.SidebarLabel;
        _tabDragFeedback.Pointer = e.GetPosition(_tabDragFeedback.AdornedElement);
        _tabDragFeedback.InvalidateVisual();
    }

    internal bool? GetPaneDropDestination(OpenNoteDocument document, Point position)
    {
        if (!_documents.Contains(document) || _documents.Count < 2 ||
            !new Rect(EditorWorkspace.RenderSize).Contains(position)) return null;
        if (_secondaryDocument is null) return true;
        var secondaryStart = SecondaryEditorPane.TranslatePoint(new Point(), EditorWorkspace).X;
        var primaryEnd = PrimaryEditorPane.TranslatePoint(new Point(PrimaryEditorPane.ActualWidth, 0), EditorWorkspace).X;
        if (position.X > primaryEnd && position.X < secondaryStart) return null;
        var secondary = position.X >= secondaryStart;
        return ReferenceEquals(document, secondary ? _secondaryDocument : _activeDocument) ? null : secondary;
    }

    private void EditorWorkspace_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (GetDraggedTab(e.Data) is not { } document ||
            GetPaneDropDestination(document, e.GetPosition(EditorWorkspace)) is not { } secondary) return;
        e.Effects = DragDropEffects.Move;
        if (_tabDragFeedback is null) return;
        var root = _tabDragFeedback.AdornedElement;
        Rect bounds;
        if (_secondaryDocument is null)
        {
            var width = Math.Max(0, (EditorWorkspace.ActualWidth - 5) / 2);
            bounds = new Rect(EditorWorkspace.TranslatePoint(new Point(width + 5, 0), root),
                new Size(width, EditorWorkspace.ActualHeight));
        }
        else
        {
            FrameworkElement pane = secondary ? SecondaryEditorPane : PrimaryEditorPane;
            bounds = new Rect(pane.TranslatePoint(new Point(), root), pane.RenderSize);
        }
        _tabDragFeedback.Pane = bounds;
        _tabDragFeedback.InvalidateVisual();
    }

    private void EditorWorkspace_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (GetDraggedTab(e.Data) is { } document &&
            GetPaneDropDestination(document, e.GetPosition(EditorWorkspace)) is { } secondary)
        {
            ApplyPaneDrop(document, secondary);
            e.Effects = DragDropEffects.Move;
        }
        ClearTabDragFeedback();
    }

    internal void ApplyPaneDrop(OpenNoteDocument document, bool secondary)
    {
        if (!_documents.Contains(document) || _documents.Count < 2 ||
            ReferenceEquals(document, secondary ? _secondaryDocument : _activeDocument)) return;
        if (secondary)
            ShowSecondaryDocument(document, focusPane: true, focusEditor: true, saveSession: true);
        else
        {
            if (ReferenceEquals(document, _secondaryDocument))
                ShowSecondaryDocument(_activeDocument!, focusPane: false, focusEditor: false, saveSession: false);
            ActivateDocument(document);
            SaveEditorSession();
        }
    }

    private void OpenSideBySide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: OpenNoteDocument document } || !_documents.Contains(document) ||
            _documents.Count < 2) return;
        if (ReferenceEquals(document, _secondaryDocument)) ActivateDocument(document);
        else ApplyPaneDrop(document, secondary: true);
        e.Handled = true;
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items[0] is MenuItem openSideBySide)
            openSideBySide.IsEnabled = _documents.Count > 1 && menu.DataContext is OpenNoteDocument document &&
                _documents.Contains(document);
    }

    internal static int GetReorderIndex(int sourceIndex, int insertionIndex, int count) =>
        Math.Clamp(insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex, 0, count - 1);

    internal (int Index, double Y) GetTabInsertion(Point position)
    {
        double bottom = 0;
        var nextIndex = 0;
        for (var index = 0; index < _documents.Count; index++)
        {
            if (OpenTabsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            var top = item.TranslatePoint(new Point(), OpenTabsList).Y;
            bottom = top + item.ActualHeight;
            nextIndex = index + 1;
            if (position.Y < top + item.ActualHeight / 2) return (index, top);
        }
        return (nextIndex, bottom);
    }

    private void OpenTabsList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (GetDraggedTab(e.Data) is not { } document) return;
        _tabDragPosition = e.GetPosition(OpenTabsList);
        var insertion = GetTabInsertion(_tabDragPosition);
        if (GetReorderIndex(_documents.IndexOf(document), insertion.Index, _documents.Count) != _documents.IndexOf(document))
        {
            e.Effects = DragDropEffects.Move;
            ShowTabInsertion(insertion.Y);
        }
        UpdateTabAutoScroll();
    }

    private void ShowTabInsertion(double y)
    {
        if (_tabDragFeedback is null) return;
        var point = OpenTabsList.TranslatePoint(new Point(0, Math.Clamp(y, 1, Math.Max(1, OpenTabsList.ActualHeight - 1))),
            _tabDragFeedback.AdornedElement);
        _tabDragFeedback.Insertion = new Rect(point, new Size(OpenTabsList.ActualWidth, 2));
        _tabDragFeedback.InvalidateVisual();
    }

    private void OpenTabsList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (GetDraggedTab(e.Data) is { } document)
        {
            var insertion = GetTabInsertion(e.GetPosition(OpenTabsList));
            if (ApplyTabReorder(document, insertion.Index)) e.Effects = DragDropEffects.Move;
        }
        ClearTabDragFeedback();
    }

    internal bool ApplyTabReorder(OpenNoteDocument document, int insertion)
    {
        var source = _documents.IndexOf(document);
        if (source < 0 || insertion < 0 || insertion > _documents.Count) return false;
        var index = GetReorderIndex(source, insertion, _documents.Count);
        if (index == source) return false;
        _workspace.Move(document, index);
        SelectTab(CurrentDocument);
        SaveEditorSession();
        return true;
    }

    private void UpdateTabAutoScroll()
    {
        var direction = GetTabScrollDirection(_tabDragPosition);
        if (direction == 0) { StopTabAutoScroll(); return; }
        _tabScrollDirection = direction;
        if (_tabScrollTimer is null)
        {
            _tabScrollTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(80) };
            _tabScrollTimer.Tick += TabAutoScroll_Tick;
        }
        _tabScrollTimer.Start();
    }

    internal int GetTabScrollDirection(Point position)
    {
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(OpenTabsList);
        if (scroll is null || scroll.ScrollableHeight <= 0 || !new Rect(OpenTabsList.RenderSize).Contains(position))
            return 0;
        if (position.Y < 24 && scroll.VerticalOffset > 0) return -1;
        if (position.Y > OpenTabsList.ActualHeight - 24 && scroll.VerticalOffset < scroll.ScrollableHeight) return 1;
        return 0;
    }

    private void StopTabAutoScroll()
    {
        _tabScrollTimer?.Stop();
        _tabScrollDirection = 0;
    }

    private void TabAutoScroll_Tick(object? sender, EventArgs e)
    {
        if (_tabScrollDirection == 0) return;
        if (_draggedDocument is null || !_documents.Contains(_draggedDocument) ||
            !new Rect(OpenTabsList.RenderSize).Contains(_tabDragPosition))
        { ClearTabDragFeedback(); return; }
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(OpenTabsList);
        if (scroll is null) { StopTabAutoScroll(); return; }
        if (_tabScrollDirection < 0) scroll.LineUp(); else scroll.LineDown();
        OpenTabsList.UpdateLayout();
        RefreshTabInsertion();
        UpdateTabAutoScroll();
    }

    private void RefreshTabInsertion()
    {
        var insertion = GetTabInsertion(_tabDragPosition);
        if (_tabDragFeedback is not null) _tabDragFeedback.Insertion = null;
        if (_draggedDocument is not null && _documents.Contains(_draggedDocument) &&
            GetReorderIndex(_documents.IndexOf(_draggedDocument), insertion.Index, _documents.Count) !=
            _documents.IndexOf(_draggedDocument)) ShowTabInsertion(insertion.Y);
        _tabDragFeedback?.InvalidateVisual();
    }

    private IntPtr TabDragWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseWheel = 0x020A;
        if (message != WmMouseWheel || !_isTabDragInProgress) return IntPtr.Zero;
        var packedPosition = lParam.ToInt64();
        var screenPosition = new Point(unchecked((short)packedPosition), unchecked((short)(packedPosition >> 16)));
        var position = OpenTabsList.PointFromScreen(screenPosition);
        handled = ScrollTabsDuringDrag(unchecked((short)(wParam.ToInt64() >> 16)), position);
        return IntPtr.Zero;
    }

    internal bool ScrollTabsDuringDrag(int delta, Point position)
    {
        if (_draggedDocument is null || !_documents.Contains(_draggedDocument) ||
            !new Rect(OpenTabsList.RenderSize).Contains(position)) return false;
        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(OpenTabsList);
        if (scroll is null || scroll.ScrollableHeight <= 0) return false;
        _tabDragPosition = position;
        _tabDragWheelDelta += delta;
        // OLE dragging bypasses the usual WPF wheel route; keep wheel input local to the sidebar.
        while (Math.Abs(_tabDragWheelDelta) >= 120)
        {
            var step = Math.Sign(_tabDragWheelDelta) * 120;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, step)
                { RoutedEvent = Mouse.MouseWheelEvent });
            _tabDragWheelDelta -= step;
        }
        OpenTabsList.UpdateLayout();
        RefreshTabInsertion();
        UpdateTabAutoScroll();
        return true;
    }

    private sealed class TabDragFeedback(FrameworkElement root) : Adorner(root)
    {
        public Rect? Pane { get; set; }
        public Rect? Insertion { get; set; }
        public Point Pointer { get; set; }
        public string? Label { get; set; }

        protected override void OnRender(DrawingContext context)
        {
            var accent = (Brush)root.FindResource("NotedAccentBrush");
            if (Pane is { } pane)
            {
                context.PushOpacity(0.15);
                context.DrawRectangle(accent, null, pane);
                context.Pop();
                context.DrawRectangle(null, new Pen(accent, 1), pane);
            }
            if (Insertion is { } insertion) context.DrawRectangle(accent, null, insertion);
            if (Label is null) return;
            var text = new FormattedText(Label, CultureInfo.CurrentUICulture, root.FlowDirection,
                new Typeface(TextElement.GetFontFamily(root), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                12, (Brush)root.FindResource("NotedTextBrush"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = 210, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            var point = new Point(Math.Clamp(Pointer.X + 14, 0, Math.Max(0, root.ActualWidth - 230)),
                Math.Clamp(Pointer.Y + 18, 0, Math.Max(0, root.ActualHeight - 32)));
            context.PushOpacity(0.85);
            context.DrawRoundedRectangle((Brush)root.FindResource("NotedSurfaceBrush"), new Pen(accent, 1),
                new Rect(point, new Size(Math.Min(230, text.Width + 16), 28)), 3, 3);
            context.DrawText(text, new Point(point.X + 8, point.Y + 6));
            context.Pop();
        }
    }
}
