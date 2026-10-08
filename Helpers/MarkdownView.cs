using System.Windows;
using System.Windows.Controls;

namespace Noted.Helpers;

// A read-only surface using the same safe renderer as NoteEditor.
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(string.Empty, Source_Changed));

    private string? _renderedSource;

    public MarkdownView()
    {
        IsToolBarVisible = false;
        IsVisibleChanged += (_, _) => Refresh();
    }

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void Source_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownView)sender).Refresh();

    private void Refresh()
    {
        if (!IsVisible || _renderedSource == Source)
            return;

        Document = MarkdownRenderer.Render(Source);
        Document.Background = System.Windows.Media.Brushes.Transparent;
        Document.PagePadding = new Thickness(0);
        _renderedSource = Source;
    }
}
