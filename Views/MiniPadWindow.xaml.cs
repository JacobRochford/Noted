using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Models;
using Noted.Search;
using Noted.Services;

namespace Noted;

public partial class MiniPadWindow : OverlayWindow
{
    private readonly IMiniPadRecoveryService _recoveryService;
    private readonly IAppSettingsService _settingsService;
    private readonly SaveScheduler<string> _saveScheduler;
    private readonly TextSearchSession _findSession = new();
    private TextBoxSearchPresenter? _findPresenter;
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
        _findPresenter = new TextBoxSearchPresenter(Editor);
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
        if (FindPanel.IsVisible) RefreshFindResults(revealCurrent: false);
    }

    private void Editor_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(TrimTrailingDoubleClickWhitespace));
    }

    private void TrimTrailingDoubleClickWhitespace()
    {
        if (Editor.SelectionLength <= 0)
            return;

        var selectedText = Editor.SelectedText;
        var trimmedLength = selectedText.TrimEnd(' ', '\t').Length;
        if (trimmedLength <= 0 || trimmedLength == selectedText.Length)
            return;

        Editor.Select(Editor.SelectionStart, trimmedLength);
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
        _findPresenter?.Dispose();
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
        _findPresenter?.InvalidateLayout();
    }

    private void AlwaysVisibleMenuItem_Click(object sender, RoutedEventArgs e) =>
        WindowAppearance.SetAlwaysVisible(this, AlwaysVisibleMenuItem.IsChecked);

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void OpenFind()
    {
        var openingFind = FindPanel.Visibility != Visibility.Visible;
        FindPanel.Visibility = Visibility.Visible;
        if (openingFind)
            _findPresenter?.BeginFind();
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) FindTextBox.Text = Editor.SelectedText;
        RefreshFindResults(revealCurrent: true);
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void CloseFindButton_Click(object sender, RoutedEventArgs e) => CloseFind();
    private void CloseFind()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _findPresenter?.EndFind(_findSession.CurrentMatch);
        _findSession.Reset();
        Editor.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) OpenFind();
        else if (e.Key == Key.Escape && FindPanel.Visibility == Visibility.Visible) CloseFind();
        else if (e.Key == Key.F3 || (e.Key == Key.Enter && FindTextBox.IsKeyboardFocusWithin))
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                FindPrevious();
            else
                FindNext();
        }
        else return;
        e.Handled = true;
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshFindResults(revealCurrent: true);

    private void RefreshFindResults(bool revealCurrent)
    {
        if (Editor is null || FindResultText is null || _findPresenter is null) return;
        _findSession.Refresh(
            Editor.Text,
            Editor,
            FindTextBox.Text,
            new TextSearchOptions { MatchCase = false },
            _findPresenter.GetQueryRefreshAnchor());
        _findPresenter.Present(
            _findSession,
            bringCurrentIntoView: revealCurrent);
        _findPresenter.RequestRenderInvalidation();
        UpdateFindResultText();
    }

    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) =>
        FindPrevious();

    private void FindNextButton_Click(object sender, RoutedEventArgs e) =>
        FindNext();

    private void FindNext() => MoveFind(forward: true);

    private void FindPrevious() => MoveFind(forward: false);

    private void MoveFind(bool forward)
    {
        if (_findPresenter is null)
            return;

        _findSession.Refresh(
            Editor.Text,
            Editor,
            FindTextBox.Text,
            new TextSearchOptions { MatchCase = false },
            _findPresenter.GetQueryRefreshAnchor());
        if (_findSession.Matches.Count == 0)
        {
            UpdateFindResultText();
            return;
        }

        var manualAnchor = _findPresenter.ConsumePendingManualNavigationAnchor(forward);
        if (forward)
            _findSession.Next(manualAnchor);
        else
            _findSession.Previous(manualAnchor);
        _findPresenter.Present(
            _findSession,
            bringCurrentIntoView: true);
        UpdateFindResultText();
    }

    private void UpdateFindResultText()
    {
        FindResultText.Text = string.IsNullOrEmpty(FindTextBox.Text)
            ? string.Empty
            : _findSession.Matches.Count == 0
                ? "Not found"
                : $"{_findSession.CurrentMatchIndex + 1} of {_findSession.Matches.Count}";
    }

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException;
    }

}
