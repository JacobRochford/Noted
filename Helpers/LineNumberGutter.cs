using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noted.Helpers;

public sealed class LineNumberGutter : FrameworkElement
{
    private TextLineMap _lines = new("");
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(TextBox), typeof(LineNumberGutter), new PropertyMetadata(null, EditorChanged));
    public TextBox? Editor { get => (TextBox?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LineNumberGutter), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public LineNumberGutter()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    private static void EditorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var gutter = (LineNumberGutter)target;
        if (e.OldValue is TextBox oldEditor)
        {
            oldEditor.TextChanged -= gutter.TextChanged;
            oldEditor.SizeChanged -= gutter.SizeChangedHandler;
            oldEditor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(gutter.ScrollChanged));
        }
        if (e.NewValue is TextBox editor)
        {
            editor.TextChanged += gutter.TextChanged;
            editor.SizeChanged += gutter.SizeChangedHandler;
            editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(gutter.ScrollChanged));
        }
        gutter.RebuildLines();
    }

    private void TextChanged(object sender, TextChangedEventArgs e) => RebuildLines();
    private void SizeChangedHandler(object sender, SizeChangedEventArgs e) => InvalidateVisual();
    private void ScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private void RebuildLines()
    {
        _lines = new TextLineMap(Editor?.Text ?? "");
        Width = Math.Max(30, Format(_lines.Starts.Length.ToString(CultureInfo.InvariantCulture)).Width + 14);
        InvalidateVisual();
    }

    private FormattedText Format(string text) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Math.Max(9, (Editor?.FontSize ?? 14) - 3),
        Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var editor = Editor;
        if (editor is null || !editor.IsLoaded || editor.ActualHeight <= 0) return;
        // SelectionChanged can cause a render before TextChanged updates our index during a tab switch.
        if (!string.Equals(editor.Text, _lines.Text, StringComparison.Ordinal))
        {
            RebuildLines();
            return;
        }
        if (!editor.IsMeasureValid || !editor.IsArrangeValid) return;
        var lines = _lines;
        // Viewport line indices can still describe the previous document. Use bounded character positions instead.
        var firstCharacter = editor.GetCharacterIndexFromPoint(new Point(editor.Padding.Left, editor.Padding.Top), true);
        var lastCharacter = editor.GetCharacterIndexFromPoint(new Point(
            Math.Max(editor.Padding.Left, editor.ActualWidth - editor.Padding.Right - SystemParameters.VerticalScrollBarWidth),
            Math.Max(editor.Padding.Top, editor.ActualHeight - editor.Padding.Bottom)), true);
        if (firstCharacter < 0 || lastCharacter < 0) return;
        var lastLine = lines.LineAt(Math.Max(firstCharacter, lastCharacter));
        for (var line = lines.LineAt(firstCharacter); line <= lastLine; line++)
        {
            if (!string.Equals(editor.Text, lines.Text, StringComparison.Ordinal)) return;
            var rect = editor.GetRectFromCharacterIndex(lines.Starts[line], true);
            if (rect.IsEmpty || rect.Bottom < 0 || rect.Top >= ActualHeight) continue;
            var label = Format((line + 1).ToString(CultureInfo.InvariantCulture));
            drawingContext.DrawText(label, new Point(Math.Max(0, ActualWidth - label.Width - 7),
                rect.Top + Math.Max(0, (rect.Height - label.Height) / 2)));
        }
    }
}
