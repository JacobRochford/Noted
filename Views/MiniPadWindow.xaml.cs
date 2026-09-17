using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Noted.Helpers;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class MiniPadWindow : OverlayWindow
{
    private readonly IMiniPadRecoveryService _recoveryService;
    private readonly IAppSettingsService _settingsService;
    private readonly SaveScheduler<string> _saveScheduler;
    private IReadOnlyList<TextMatch> _findMatches = [];
    private string _findSource = string.Empty;
    private int _findIndex = -1;
    private bool _keepDraftOnClose;
    private bool _preserveOpenStateOnClose;
    private bool _reopenOnStartup;

    public MiniPadWindow(
        IMiniPadRecoveryService recoveryService,
        IAppSettingsService settingsService,
        MiniPadRecoveryDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(settingsService);

        _recoveryService = recoveryService;
        _settingsService = settingsService;
        _reopenOnStartup = settingsService.LoadMiniPadWindowState().ReopenOnStartup;

        InitializeComponent();
        InitializeOverlay(false, settingsService.LoadGhostModeOpacity(), settingsService.LoadDefaultOpacity());
        SetWordWrap(settingsService.LoadMiniPadWindowState().WordWrapEnabled);
        AlwaysVisibleMenuItem.IsChecked = settingsService.LoadWindowAlwaysVisible(nameof(MiniPadWindow));
        Editor.Text = draft?.Content ?? string.Empty;
        _saveScheduler = new SaveScheduler<string>(
            Dispatcher,
            quietPeriod: TimeSpan.FromMilliseconds(750),
            maximumDelay: TimeSpan.FromSeconds(2),
            SaveDraftSnapshot);
        _saveScheduler.StateChanged += SaveScheduler_StateChanged;
        Editor.TextChanged += Editor_TextChanged;
        Loaded += MiniPadWindow_Loaded;
        IsVisibleChanged += MiniPadWindow_IsVisibleChanged;
        Closing += MiniPadWindow_Closing;
        Closed += MiniPadWindow_Closed;

        if (draft is null)
        {
            _saveScheduler.Schedule(Editor.Text);
            _saveScheduler.TryFlush(out _);
        }
    }

    public void KeepDraftOnClose()
    {
        _keepDraftOnClose = true;
    }

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        _preserveOpenStateOnClose = true;
        SaveWindowState();
    }

    public bool TryFlushPendingContent(out string? error)
    {
        _saveScheduler.Schedule(Editor.Text);
        return _saveScheduler.TryFlush(out error);
    }

    internal string? BackupBlockingIssue =>
        _saveScheduler.LastError ?? _saveScheduler.LastWarning;

    protected override void SaveWindowState()
    {
        _settingsService.SaveMiniPadWindowState(new MiniPadWindowState
        {
            WordWrapEnabled = Editor.TextWrapping == TextWrapping.Wrap,
            ReopenOnStartup = _reopenOnStartup
        });
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    protected override void RequestHide()
    {
        if (!TryFlushPendingContent(out var error))
        {
            AppDialog.Show(
                error ?? "MiniPad recovery data could not be saved.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _reopenOnStartup = false;
        SaveWindowState();
        HideWindow();
    }

    private void MiniPadWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_preserveOpenStateOnClose || IsChangingGroupVisibility)
            return;

        _reopenOnStartup = IsWindowVisible;
        SaveWindowState();
    }

    private void MiniPadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, Editor.Focus);
    }

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _saveScheduler.Schedule(Editor.Text);
        if (FindPanel.IsVisible) RefreshFind(selectMatch: false);
    }

    private PersistenceSaveResult SaveDraftSnapshot(string content)
    {
        try
        {
            var warning = _recoveryService.SaveDraft(content);
            return PersistenceSaveResult.Succeeded(warning);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return PersistenceSaveResult.Failed(
                $"MiniPad recovery data could not be saved: {ex.Message}");
        }
    }

    private void SaveScheduler_StateChanged(object? sender, EventArgs e)
    {
        var message = _saveScheduler.LastError ?? _saveScheduler.LastWarning;
        PersistenceErrorText.Text = message ?? string.Empty;
        PersistenceErrorPanel.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MiniPadWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_keepDraftOnClose)
            return;

        if (!_saveScheduler.TryFlush(out var error))
        {
            e.Cancel = true;
            AppDialog.Show(
                error ?? "MiniPad recovery data could not be saved.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_preserveOpenStateOnClose)
        {
            _reopenOnStartup = false;
            SaveWindowState();
        }

        e.Cancel = true;
        HideWindow();
    }

    private void MiniPadWindow_Closed(object? sender, EventArgs e)
    {
        _saveScheduler.StateChanged -= SaveScheduler_StateChanged;
        _saveScheduler.Dispose();
        Editor.TextChanged -= Editor_TextChanged;
        Loaded -= MiniPadWindow_Loaded;
        IsVisibleChanged -= MiniPadWindow_IsVisibleChanged;
        Closing -= MiniPadWindow_Closing;
        Closed -= MiniPadWindow_Closed;
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void WordWrapMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetWordWrap(WordWrapMenuItem.IsChecked);
        SaveWindowState();
    }

    private void SetWordWrap(bool enabled)
    {
        WordWrapMenuItem.IsChecked = enabled;
        Editor.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Editor.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void AlwaysVisibleMenuItem_Click(object sender, RoutedEventArgs e) =>
        WindowAppearance.SetAlwaysVisible(this, AlwaysVisibleMenuItem.IsChecked);

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void OpenFind()
    {
        FindPanel.Visibility = Visibility.Visible;
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) FindTextBox.Text = Editor.SelectedText;
        RefreshFind(selectMatch: true);
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();
    private void CloseFind()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) OpenFind();
        else if (e.Key == Key.Escape && FindPanel.Visibility == Visibility.Visible) CloseFind();
        else if (e.Key == Key.F3 || (e.Key == Key.Enter && FindTextBox.IsKeyboardFocusWithin))
            MoveFind(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        else return;
        e.Handled = true;
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFind(selectMatch: true);
    private void RefreshFind(bool selectMatch)
    {
        if (Editor is null || FindResultText is null) return;
        _findSource = Editor.Text;
        _findMatches = PlainTextSearch.FindMatches(_findSource, FindTextBox.Text, matchCase: false);
        _findIndex = -1;
        if (selectMatch && _findMatches.Count > 0)
        {
            _findIndex = 0;
            for (var i = 0; i < _findMatches.Count; i++)
                if (_findMatches[i].Start >= Editor.SelectionStart) { _findIndex = i; break; }
            SelectFindMatch();
        }
        else UpdateFindResult();
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => MoveFind(-1);
    private void FindNext_Click(object sender, RoutedEventArgs e) => MoveFind(1);
    private void MoveFind(int direction)
    {
        if (!string.Equals(_findSource, Editor.Text, StringComparison.Ordinal)) RefreshFind(selectMatch: false);
        if (_findMatches.Count == 0) return;
        _findIndex = _findIndex < 0 ? (direction > 0 ? 0 : _findMatches.Count - 1)
            : (_findIndex + direction + _findMatches.Count) % _findMatches.Count;
        SelectFindMatch();
    }

    private void SelectFindMatch()
    {
        var match = _findMatches[_findIndex];
        Editor.Select(match.Start, match.Length);
        var line = Editor.GetLineIndexFromCharacterIndex(match.Start);
        if (line >= 0) Editor.ScrollToLine(line);
        UpdateFindResult();
    }

    private void UpdateFindResult() => FindResultText.Text = string.IsNullOrEmpty(FindTextBox.Text) ? ""
        : _findMatches.Count == 0 ? "Not found"
        : _findIndex < 0 ? $"{_findMatches.Count} matches" : $"{_findIndex + 1} of {_findMatches.Count}";

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException;
    }

}
