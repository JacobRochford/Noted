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
using Noted.Search;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class ScratchpadWindow : OverlayWindow
{
    // With a responsive UI thread and successful I/O, content waits at most two seconds before saving.
    private static readonly TimeSpan s_contentSaveQuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_contentSaveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly ScratchpadWindowViewModel _viewModel;
    private readonly SaveScheduler<long> _contentSaveScheduler;
    private readonly DispatcherTimer _windowStateSaveTimer;
    private readonly TextSearchSession _findSession = new();
    private readonly RichTextBoxSearchPresenter _findPresenter;
    private bool _isSeedingFindQuery;
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

    private static readonly double[] s_fontSizes = { 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48 };

    private const double MinimumWidth = 300;
    private const double MinimumHeight = 250;
    private const double FallbackWidth = 450;
    private const double FallbackHeight = 500;
    private const double FallbackFontSize = 13;

    public ScratchpadWindow(ScratchpadWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _findPresenter = new RichTextBoxSearchPresenter(Editor);
        _findSession.QueryOrOptionsChanged += _findPresenter.ClearPendingManualNavigationAnchor;
        var state = NormalizeWindowState(_viewModel.InitialWindowState);
        _viewModel.ApplyNormalizedWindowState(state);
        _reopenOnStartup = state.ReopenOnStartup;
        DataContext = _viewModel;
        EditorIndentation.Attach(Editor, () => _viewModel.Indentation);
        EditorLineCommands.Attach(Editor);
        EditorJsonFormatting.Attach(Editor, () => _viewModel.Indentation);
        IndentationButton.Content = EditorIndentation.GetLabel(_viewModel.Indentation);
        EditorIndentation.AttachIndicator(IndentationButton, () => _viewModel.Indentation, _viewModel.SetIndentation, () => Editor);

        Left = state.Left;
        Top = state.Top;
        Width = state.Width;
        Height = state.Height;

        InitializeOverlay(state.GhostModeEnabled, state.GhostModeOpacity, state.Opacity);

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _contentSaveScheduler = new SaveScheduler<long>(
            Dispatcher,
            s_contentSaveQuietPeriod,
            s_contentSaveMaximumDelay,
            SaveContentRevision);

        _windowStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowStateSaveTimer.Tick += WindowStateSaveTimer_Tick;

        FontSizeCombo.ItemsSource = s_fontSizes;
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
        {
            e.Cancel = true;
            ShowPersistenceWarningOnce(stateError);
            return;
        }

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
            case nameof(ScratchpadWindowViewModel.FindText):
                if (_viewModel.IsFindBarVisible && !_isSeedingFindQuery)
                    RefreshFindResults(revealCurrent: true);
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
            ExceptionDiagnostics.Record(ex);
            Editor.Document.Blocks.Clear();
            Editor.AppendText(saved);
            _viewModel.ReportPersistenceError(
                "Scratchpad formatting could not be restored; the saved content was opened as plain text.");
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
        var fontSize = IsFinite(state.FontSize) && s_fontSizes.Contains(state.FontSize)
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
        var content = SerializeEditorContent();

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

    internal bool TryPrepareForApplicationShutdown(out string? error)
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        _isWindowStateDirty = true;
        if (!TryFlushPendingWindowState(out error))
            return false;

        _preserveOpenStateOnClose = true;
        return true;
    }

    internal void CancelPreparedClose() => _preserveOpenStateOnClose = false;

    protected override void RequestHide()
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
        _findSession.QueryOrOptionsChanged -= _findPresenter.ClearPendingManualNavigationAnchor;
        _findPresenter.Dispose();
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

        if (_viewModel.IsFindBarVisible && !_findPresenter.IsApplyingReplacement)
            RefreshFindResults(revealCurrent: false);
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateToolbarState();
    }

    private void Editor_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(TrimTrailingDoubleClickWhitespace));
    }

    private void TrimTrailingDoubleClickWhitespace()
    {
        if (Editor.Selection.IsEmpty)
            return;

        var selectedText = Editor.Selection.Text;
        var trimmedLength = selectedText.TrimEnd(' ', '\t').Length;
        var trailingLength = selectedText.Length - trimmedLength;
        if (trimmedLength <= 0 || trailingLength <= 0)
            return;

        var trimmedEnd = MoveTextPointerBackwardByTextCharacters(
            Editor.Selection.End,
            trailingLength);
        if (trimmedEnd is not null && trimmedEnd.CompareTo(Editor.Selection.Start) > 0)
            Editor.Selection.Select(Editor.Selection.Start, trimmedEnd);
    }

    private static TextPointer? MoveTextPointerBackwardByTextCharacters(
        TextPointer position,
        int characterCount)
    {
        var current = position;
        var remaining = characterCount;
        while (remaining > 0)
        {
            if (current.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text)
            {
                var text = current.GetTextInRun(LogicalDirection.Backward);
                var move = Math.Min(remaining, text.Length);
                current = current.GetPositionAtOffset(-move, LogicalDirection.Backward) ?? current;
                remaining -= move;
                continue;
            }

            var previous = current.GetNextContextPosition(LogicalDirection.Backward);
            if (previous is null)
                return null;
            current = previous;
        }

        return current;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenFind();
            e.Handled = true;
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F3 && _viewModel.IsFindBarVisible)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                FindPrevious();
            else
                FindNext();
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
            HighlightColorBar.Background = brush == Brushes.Transparent
                ? (Brush)FindResource("NotedBorderBrush")
                : brush;
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
                Background = brush == Brushes.Transparent
                    ? (Brush)FindResource("NotedSurfaceBrush")
                    : brush,
                CornerRadius = new CornerRadius(2),
                BorderBrush = (Brush)FindResource("NotedBorderBrush"),
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

    private void FindButton_Click(object sender, RoutedEventArgs e) => ToggleFind();

    private void ToggleFind()
    {
        if (_viewModel.IsFindBarVisible)
            CloseFind();
        else
            OpenFind();
    }

    private void OpenFind()
    {
        var wasVisible = _viewModel.IsFindBarVisible;
        var seed = FindSelectionSeed.GetQuery(Editor.Selection.Text, wasVisible, Editor.IsKeyboardFocusWithin);
        if (wasVisible && seed is null)
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
            return;
        }

        if (seed is not null)
        {
            _isSeedingFindQuery = true;
            try { _viewModel.FindText = seed; }
            finally { _isSeedingFindQuery = false; }
        }

        _viewModel.IsFindBarVisible = true;
        _findSession.Reset();
        _findPresenter.BeginFind();
        RefreshFindResults(revealCurrent: false);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                FindTextBox.Focus();
                FindTextBox.SelectAll();
            }));
    }

    private void CloseFindButton_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        _viewModel.IsFindBarVisible = false;
        _findSession.Reset();
        _findPresenter.EndFind();
        _viewModel.FindResultText = string.Empty;
        Editor.Focus();
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.F3)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void FindNext() => MoveFind(forward: true);

    private void FindPrevious() => MoveFind(forward: false);

    private void MoveFind(bool forward)
    {
        RefreshFindResults(revealCurrent: false);
        if (_findSession.Matches.Count == 0)
            return;

        var manualAnchor = _findPresenter.ConsumePendingManualNavigationAnchor();
        if (forward)
            _findSession.Next(manualAnchor);
        else
            _findSession.Previous(manualAnchor);
        _findPresenter.Present(_findSession, bringCurrentIntoView: true);
        UpdateFindResultText();
    }

    private void RefreshFindResults(bool revealCurrent, int? navigationAnchor = null)
    {
        _findPresenter.Refresh(_findSession, _viewModel.FindText,
            GetFindOptions(), navigationAnchor);
        _findPresenter.Present(_findSession, bringCurrentIntoView: revealCurrent);
        UpdateFindResultText();
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.FindText))
            return;

        RefreshFindResults(revealCurrent: false);
        if (_findSession.CurrentMatch is not { } match)
        {
            FindNext();
            return;
        }

        _findPresenter.Replace([match], _viewModel.ReplaceText);
        RefreshFindResults(
            revealCurrent: true,
            navigationAnchor: match.Start + _viewModel.ReplaceText.Length);
    }

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.FindText))
            return;

        RefreshFindResults(revealCurrent: false);
        if (_findSession.Matches.Count == 0)
        {
            UpdateFindResultText();
            return;
        }

        var matches = _findSession.Matches.ToArray();
        _findPresenter.Replace(matches, _viewModel.ReplaceText);
        _findSession.Reset();
        RefreshFindResults(revealCurrent: false);
        _viewModel.FindResultText = $"Replaced {matches.Length}";
    }

    private TextSearchOptions GetFindOptions() => new()
    {
        MatchWholeWord = MatchWholeWordOption.IsChecked == true,
        UseRegex = RegexOption.IsChecked == true
    };

    private void FindOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsFindBarVisible)
            RefreshFindResults(revealCurrent: true);
    }

    private void UpdateFindResultText()
    {
        _viewModel.FindResultText = string.IsNullOrEmpty(_viewModel.FindText)
            ? string.Empty
            : _findSession.ValidationMessage ?? (_findSession.Matches.Count == 0
                ? "Not found"
                : $"{_findSession.CurrentMatchIndex + 1} of {_findSession.Matches.Count}");
    }

}
