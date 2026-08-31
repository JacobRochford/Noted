using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class ScratchpadWindow : OverlayWindow
{
    // With a responsive UI thread and successful I/O, content waits at most two seconds before saving.
    private static readonly TimeSpan ContentSaveQuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ContentSaveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly ScratchpadWindowViewModel _viewModel;
    private readonly SaveScheduler<long> _contentSaveScheduler;
    private readonly DispatcherTimer _windowStateSaveTimer;
    private TextPointer? _lastFindEnd;
    private bool _suppressFontSizeChange;
    private bool _isInitializing = true;
    private bool _isRestoringContent;
    private bool _isContentDirty;
    private bool _isWindowStateDirty;
    private bool _isClosing;
    private bool _cleanupCompleted;
    private bool _reopenOnStartup;
    private bool _preserveOpenStateOnClose;
    private string? _lastWarningMessage;
    private long _contentRevision;

    // Tracks currently selected colors for the toolbar indicator bars
    private Brush _activeTextColor = Brushes.Black;
    private Brush _activeHighlightColor = Brushes.Yellow;

    private static readonly double[] FontSizes = { 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48 };

    private const double MinimumWidth = 300;
    private const double MinimumHeight = 250;
    private const double FallbackWidth = 450;
    private const double FallbackHeight = 500;
    private const double FallbackFontSize = 13;

    public ScratchpadWindow(ScratchpadWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        var state = NormalizeWindowState(_viewModel.InitialWindowState);
        _viewModel.ApplyNormalizedWindowState(state);
        _reopenOnStartup = state.ReopenOnStartup;
        DataContext = _viewModel;

        Left = state.Left;
        Top = state.Top;
        Width = state.Width;
        Height = state.Height;

        InitializeOverlay(state.GhostModeEnabled, state.GhostModeOpacity, state.Opacity);

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _contentSaveScheduler = new SaveScheduler<long>(
            Dispatcher,
            ContentSaveQuietPeriod,
            ContentSaveMaximumDelay,
            SaveContentRevision);

        _windowStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowStateSaveTimer.Tick += WindowStateSaveTimer_Tick;

        FontSizeCombo.ItemsSource = FontSizes;
        _suppressFontSizeChange = true;
        FontSizeCombo.SelectedValue = _viewModel.FontSize;
        _suppressFontSizeChange = false;
        Editor.FontSize = _viewModel.FontSize;

        IsVisibleChanged += ScratchpadWindow_IsVisibleChanged;
        Closed += ScratchpadWindow_Closed;

        RestoreEditorContent();
        ApplyWordWrap(_viewModel.WordWrapEnabled);
        UpdateCounts();
        _isInitializing = false;
    }

    internal bool TryFlushPendingContent(out string? error)
    {
        if (!_isContentDirty)
        {
            error = null;
            return true;
        }

        _contentSaveScheduler.Schedule(_contentRevision);
        return _contentSaveScheduler.TryFlush(out error);
    }

    internal string? BackupBlockingIssue => _viewModel.PersistenceError;
    internal bool RecoveryBlocksBackup => _viewModel.RecoveryIssuesFoundThisRun;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
            ShowPersistenceWarningOnce(error);
            return;
        }

        if (!_preserveOpenStateOnClose)
        {
            _reopenOnStartup = false;
            _isWindowStateDirty = true;
        }

        if (!TryFlushPendingWindowState(out var stateError))
            ShowPersistenceWarningOnce(stateError);

        _isClosing = true;
        base.OnClosing(e);
        if (e.Cancel)
            _isClosing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        Cleanup();
        base.OnClosed(e);
    }

    protected override void SaveWindowState()
    {
        if (_cleanupCompleted || _isClosing)
            return;

        _isWindowStateDirty = true;
        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    protected override void OnTitleBarDoubleClick()
    {
        if (IsWindowVisible)
            RequestHide();
        else
            ShowWindow();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScratchpadWindowViewModel.GhostModeEnabled):
                ApplyGhostMode(_viewModel.GhostModeEnabled);
                break;
            case nameof(ScratchpadWindowViewModel.WordWrapEnabled):
                ApplyWordWrap(_viewModel.WordWrapEnabled);
                break;
        }
    }

    private void ApplyWordWrap(bool enabled)
    {
        if (enabled)
        {
            Editor.Document.PageWidth = double.NaN;
            Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            Editor.Document.PageWidth = 2000;
            Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    // Content saving

    private void RestoreEditorContent()
    {
        if (!_viewModel.InitialContentLoadSucceeded)
            return;

        var saved = _viewModel.InitialContent;
        if (string.IsNullOrEmpty(saved))
            return;

        _isRestoringContent = true;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(saved));
            var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
            range.Load(stream, DataFormats.Rtf);
        }
        catch (ArgumentException ex)
        {
            Editor.Document.Blocks.Clear();
            Editor.AppendText(saved);
            _viewModel.ReportPersistenceError(
                $"Scratchpad formatting could not be restored; the saved content was opened as plain text: {ex.Message}");
        }
        finally
        {
            _isRestoringContent = false;
        }
    }

    private static ScratchpadWindowState NormalizeWindowState(ScratchpadWindowState state)
    {
        var bounds = WindowInterop.NormalizeWindowBounds(
            state.Left,
            state.Top,
            state.Width,
            state.Height,
            MinimumWidth,
            MinimumHeight,
            FallbackWidth,
            FallbackHeight);

        var opacity = IsFinite(state.Opacity) ? Math.Clamp(state.Opacity, 0, 1) : 0.88;
        var ghostModeOpacity = IsFinite(state.GhostModeOpacity)
            ? Math.Clamp(state.GhostModeOpacity, 0, 1)
            : 0.25;
        var fontSize = IsFinite(state.FontSize) && FontSizes.Contains(state.FontSize)
            ? state.FontSize
            : FallbackFontSize;

        return state with
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Opacity = opacity,
            GhostModeOpacity = ghostModeOpacity,
            FontSize = fontSize
        };
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private string SerializeEditorContent()
    {
        using var stream = new MemoryStream();
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        range.Save(stream, DataFormats.Rtf);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private PersistenceSaveResult SaveContentRevision(long revision)
    {
        string content;
        try
        {
            content = SerializeEditorContent();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            var serializationError = $"Scratchpad content could not be prepared for saving: {ex.Message}";
            _viewModel.ReportPersistenceError(serializationError);
            return PersistenceSaveResult.Failed(serializationError);
        }

        if (!_viewModel.TrySaveContent(content))
        {
            return PersistenceSaveResult.Failed(
                _viewModel.PersistenceError ?? "Scratchpad content could not be saved.");
        }

        if (revision == _contentRevision)
            _isContentDirty = false;
        _lastWarningMessage = null;
        return PersistenceSaveResult.Succeeded();
    }

    private void WindowStateSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (!TryFlushPendingWindowState(out var error))
            ShowPersistenceWarningOnce(error);
    }

    private bool TryFlushPendingWindowState(out string? error)
    {
        _windowStateSaveTimer.Stop();

        if (!_isWindowStateDirty)
        {
            error = null;
            return true;
        }

        if (_viewModel.TrySaveWindowLayout(Left, Top, Width, Height, _reopenOnStartup))
        {
            _isWindowStateDirty = false;
            error = null;
            return true;
        }

        error = _viewModel.PersistenceError ?? "Scratchpad window settings could not be saved.";
        return false;
    }

    private void ScratchpadWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_preserveOpenStateOnClose && !IsChangingGroupVisibility)
        {
            _reopenOnStartup = IsWindowVisible;
            _isWindowStateDirty = true;
            if (!TryFlushPendingWindowState(out var visibilityStateError))
                ShowPersistenceWarningOnce(visibilityStateError);
        }

        if (!IsVisible && _isContentDirty && !TryFlushPendingContent(out var contentError))
            ShowPersistenceWarningOnce(contentError);

        if (!IsVisible && _isWindowStateDirty && !TryFlushPendingWindowState(out var stateError))
            ShowPersistenceWarningOnce(stateError);
    }

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        _preserveOpenStateOnClose = true;
        _isWindowStateDirty = true;
        if (!TryFlushPendingWindowState(out var error))
            ShowPersistenceWarningOnce(error);
    }

    private void RequestHide()
    {
        if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return;
        }

        if (!TryFlushPendingWindowState(out var stateError))
            ShowPersistenceWarningOnce(stateError);

        HideWindow();
    }

    private void ShowPersistenceWarningOnce(string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || error == _lastWarningMessage)
            return;

        _lastWarningMessage = error;
        AppDialog.Show(
            error,
            "Scratchpad Save Failed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ScratchpadWindow_Closed(object? sender, EventArgs e) => Cleanup();

    private void Cleanup()
    {
        if (_cleanupCompleted)
            return;

        _cleanupCompleted = true;
        _contentSaveScheduler.Dispose();
        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Tick -= WindowStateSaveTimer_Tick;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        IsVisibleChanged -= ScratchpadWindow_IsVisibleChanged;
        Closed -= ScratchpadWindow_Closed;
    }

    // Editor state

    private void UpdateCounts()
    {
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        _viewModel.UpdateCounts(text);
    }

    private void UpdateToolbarState()
    {
        var sel = Editor.Selection;

        var fw = sel.GetPropertyValue(TextElement.FontWeightProperty);
        BoldButton.IsChecked = fw != DependencyProperty.UnsetValue && (FontWeight)fw == FontWeights.Bold;

        var fs = sel.GetPropertyValue(TextElement.FontStyleProperty);
        ItalicButton.IsChecked = fs != DependencyProperty.UnsetValue && (FontStyle)fs == FontStyles.Italic;

        var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        UnderlineButton.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
        StrikethroughButton.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;

        var size = sel.GetPropertyValue(TextElement.FontSizeProperty);
        if (size != DependencyProperty.UnsetValue)
        {
            _suppressFontSizeChange = true;
            FontSizeCombo.SelectedValue = (double)size;
            _suppressFontSizeChange = false;
        }
    }

    // UI events

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _cleanupCompleted)
            return;

        UpdateCounts();

        if (_isRestoringContent)
            return;

        _isContentDirty = true;
        _contentSaveScheduler.Schedule(checked(++_contentRevision));
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateToolbarState();

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleFindBar(show: true);
            e.Handled = true;
        }
    }

    // Formatting

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBold.Execute(null, Editor);
        Editor.Focus();
    }

    private void ItalicButton_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleItalic.Execute(null, Editor);
        Editor.Focus();
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleUnderline.Execute(null, Editor);
        Editor.Focus();
    }

    private void StrikethroughButton_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Selection;
        if (sel.IsEmpty) return;
        var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool isStruck = deco?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty,
            isStruck ? null : (object)TextDecorations.Strikethrough);
        Editor.Focus();
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontSizeChange) return;
        if (FontSizeCombo.SelectedValue is double size && size > 0)
        {
            Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
            _viewModel.FontSize = size;
            Editor.Focus();
        }
    }

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
    {
        ShowColorPopup((Button)sender, new (string Name, Brush Brush)[]
        {
            ("Black",  Brushes.Black),
            ("Navy",   new SolidColorBrush(Color.FromRgb(0x2C, 0x6E, 0x91))),
            ("Blue",   Brushes.DodgerBlue),
            ("Green",  Brushes.ForestGreen),
            ("Red",    new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33))),
            ("Orange", Brushes.DarkOrange),
            ("Purple", Brushes.DarkViolet),
            ("Gray",   Brushes.DimGray),
            ("White",  Brushes.White),
        },
        brush =>
        {
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
            _activeTextColor = brush;
            TextColorBar.Background = brush;
            Editor.Focus();
        });
    }

    private void HighlightButton_Click(object sender, RoutedEventArgs e)
    {
        ShowColorPopup((Button)sender, new (string Name, Brush Brush)[]
        {
            ("None",     Brushes.Transparent),
            ("Yellow",   Brushes.Yellow),
            ("Lime",     Brushes.LimeGreen),
            ("Cyan",     Brushes.Cyan),
            ("Pink",     Brushes.HotPink),
            ("Orange",   new SolidColorBrush(Color.FromRgb(255, 200, 100))),
            ("Lavender", new SolidColorBrush(Color.FromRgb(200, 180, 240))),
        },
        brush =>
        {
            Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty,
                brush == Brushes.Transparent ? null : (object)brush);
            _activeHighlightColor = brush;
            HighlightColorBar.Background = brush == Brushes.Transparent ? Brushes.LightGray : brush;
            Editor.Focus();
        });
    }

    private void ShowColorPopup(Button anchor, (string Name, Brush Brush)[] colors, Action<Brush> onSelected)
    {
        var menu = new ContextMenu();
        if (TryFindResource("NotedContextMenu") is Style menuStyle)
            menu.Style = menuStyle;

        foreach (var (name, brush) in colors)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14,
                Background = brush == Brushes.Transparent ? Brushes.White : brush,
                CornerRadius = new CornerRadius(2),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(label);

            var item = new MenuItem { Header = row };
            if (TryFindResource("NotedContextMenuItem") is Style itemStyle)
                item.Style = itemStyle;
            var captured = brush;
            item.Click += (_, _) => onSelected(captured);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        ApplicationCommands.Undo.Execute(null, Editor);
        Editor.Focus();
    }

    private void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        ApplicationCommands.Redo.Execute(null, Editor);
        Editor.Focus();
    }

    private void InsertTimestampButton_Click(object sender, RoutedEventArgs e)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Editor.Selection.Text = ts;
        Editor.CaretPosition = Editor.Selection.End;
        Editor.Focus();
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
        Editor.Focus();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var result = AppDialog.Show(
            "Clear all scratchpad content?",
            "Clear Scratchpad",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            Editor.Document.Blocks.Clear();
            Editor.Focus();
        }
    }

    // Find and replace

    private void FindButton_Click(object sender, RoutedEventArgs e) => ToggleFindBar();

    private void ToggleFindBar(bool? show = null)
    {
        _viewModel.IsFindBarVisible = show ?? !_viewModel.IsFindBarVisible;
        if (_viewModel.IsFindBarVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => FindBox.Focus()));
        else
        {
            ClearFindState();
            Editor.Focus();
        }
    }

    private void CloseFindBar_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsFindBarVisible = false;
        ClearFindState();
        Editor.Focus();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) FindPrevious();
            else FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ToggleFindBar(show: false);
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void FindNext()
    {
        if (string.IsNullOrEmpty(_viewModel.FindText)) return;
        var matches = FindAll(_viewModel.FindText);
        if (matches.Count == 0)
        {
            _viewModel.FindResultText = "Not found";
            return;
        }

        var start = _lastFindEnd ?? Editor.Document.ContentStart;
        var hit = matches.FirstOrDefault(match => match.Start.CompareTo(start) >= 0)
                  ?? matches[0];
        Editor.Selection.Select(hit.Start, hit.End);
        hit.Start.Paragraph?.BringIntoView();
        _lastFindEnd = hit.End;
        _viewModel.FindResultText = string.Empty;
    }

    private void FindPrevious()
    {
        if (string.IsNullOrEmpty(_viewModel.FindText)) return;
        var all = FindAll(_viewModel.FindText);
        if (all.Count == 0) { _viewModel.FindResultText = "Not found"; return; }

        var refPos = _lastFindEnd ?? Editor.Selection.Start;
        var before = all.Where(r => r.End.CompareTo(refPos) < 0).ToList();
        var hit = before.Count > 0 ? before.Last() : all.Last(); // wrap around
        Editor.Selection.Select(hit.Start, hit.End);
        hit.Start.Paragraph?.BringIntoView();
        _lastFindEnd = hit.Start;
        _viewModel.FindResultText = string.Empty;
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.FindText)) return;
        if (!Editor.Selection.IsEmpty &&
            string.Equals(Editor.Selection.Text, _viewModel.FindText, StringComparison.OrdinalIgnoreCase))
        {
            Editor.Selection.Text = _viewModel.ReplaceText;
            _lastFindEnd = Editor.Selection.End;
        }
        FindNext();
    }

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.FindText)) return;
        var matches = FindAll(_viewModel.FindText);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            matches[index].Text = _viewModel.ReplaceText;
        }

        _lastFindEnd = null;
        _viewModel.FindResultText = matches.Count > 0
            ? $"Replaced {matches.Count}"
            : "Not found";
    }

    private void ClearFindState()
    {
        _lastFindEnd = null;
        _viewModel.FindResultText = string.Empty;
    }

    private List<TextRange> FindAll(string search)
    {
        var snapshot = BuildSearchTextSnapshot();
        var results = new List<TextRange>();
        var offset = 0;
        while (offset <= snapshot.Text.Length - search.Length)
        {
            var matchIndex = snapshot.Text.IndexOf(
                search,
                offset,
                StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                break;

            results.Add(snapshot.CreateRange(matchIndex, search.Length));
            offset = matchIndex + search.Length;
        }
        return results;
    }

    private SearchTextSnapshot BuildSearchTextSnapshot()
    {
        var text = new StringBuilder();
        var characterStarts = new List<TextPointer>();
        var characterEnds = new List<TextPointer>();
        var position = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        while (position is not null && position.CompareTo(Editor.Document.ContentEnd) < 0)
        {
            var next = position.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next is null)
                break;

            var segment = new TextRange(position, next).Text;
            foreach (var character in segment)
            {
                text.Append(character);
                characterStarts.Add(position);
                characterEnds.Add(next);
            }
            position = next;
        }

        return new SearchTextSnapshot(text.ToString(), characterStarts, characterEnds);
    }

    private sealed class SearchTextSnapshot
    {
        private readonly IReadOnlyList<TextPointer> _characterStarts;
        private readonly IReadOnlyList<TextPointer> _characterEnds;

        internal SearchTextSnapshot(
            string text,
            IReadOnlyList<TextPointer> characterStarts,
            IReadOnlyList<TextPointer> characterEnds)
        {
            Text = text;
            _characterStarts = characterStarts;
            _characterEnds = characterEnds;
        }

        internal string Text { get; }

        internal TextRange CreateRange(int startIndex, int length)
        {
            return new TextRange(
                _characterStarts[startIndex],
                _characterEnds[startIndex + length - 1]);
        }
    }
}
