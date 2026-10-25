using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Noted.Helpers;
using Noted.Services;

namespace Noted;

internal sealed class BackupPreviewRow
{
    private static readonly Brush s_greenForeground = CreateBrush(52, 112, 82);
    private static readonly Brush s_amberForeground = CreateBrush(145, 99, 28);
    private static readonly Brush s_redForeground = CreateBrush(160, 70, 70);
    private static readonly Brush s_grayBackground = CreateBrush(231, 238, 242);
    private static readonly Brush s_grayForeground = CreateBrush(95, 116, 128);

    internal BackupPreviewRow(BackupFileSummary file)
    {
        File = file;
    }

    internal BackupFileSummary File { get; }
    public string DisplayPath => File.DisplayPath;
    public string Category => File.Category;
    public string Details => BuildDetails(File);
    public string StatusText => File.Comparison switch
    {
        BackupFileComparison.Unchanged => "Same",
        BackupFileComparison.Changed => "Changed",
        BackupFileComparison.Missing => "Missing now",
        _ => "Can't compare"
    };

    public Brush StatusBackground => GetThemeBrush("NotedSubtleSurfaceBrush", s_grayBackground);

    public Brush StatusForeground => File.Comparison switch
    {
        BackupFileComparison.Unchanged => GetThemeBrush("NotedSuccessBrush", s_greenForeground),
        BackupFileComparison.Changed => GetThemeBrush("NotedWarningBrush", s_amberForeground),
        BackupFileComparison.Missing => GetThemeBrush("NotedDangerBrush", s_redForeground),
        _ => GetThemeBrush("NotedSecondaryTextBrush", s_grayForeground)
    };

    private static string BuildDetails(BackupFileSummary file)
    {
        var size = FormatSize(file.Length);
        if (file.ItemCount is int itemCount)
        {
            var itemLabel = itemCount == 1 ? "item" : "items";
            return $"{itemCount} {itemLabel}  •  {size}";
        }

        return size;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / (1024d * 1024d):0.#} MB";
    }

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Brush GetThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

public partial class BackupPreviewDialog : Window
{
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions s_previewJsonOptions = new()
    {
        WriteIndented = true
    };

    private FullBackupPreview _preview = new(false, false, Guid.Empty, null, [], null);
    private Func<Guid, BackupFileSummary, BackupFileContent>? _readFile;
    private readonly BackupCoordinator? _browserCoordinator;
    private readonly WindowEdgeResizer _edgeResizer;
    private int _selectionVersion;
    private bool _needsRecheck;
    private bool _isExporting;

    private BackupPreviewDialog()
    {
        InitializeComponent();
        _edgeResizer = new WindowEdgeResizer(this, () => { });
        Closed += (_, _) => _selectionVersion++;
    }

    internal BackupPreviewDialog(
        FullBackupPreview preview,
        Func<Guid, BackupFileSummary, BackupFileContent> readFile,
        bool isImport = false) : this()
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(readFile);

        _readFile = readFile;

        if (isImport)
        {
            DialogTitleText.Text = "Imported backup contents";
            RestoreBackupButton.Content = "Restore Imported Backup";
            FooterText.Text = "Preview is read-only. Import verifies every file again before restoring.";
        }

        ShowPreview(preview);
    }

    internal BackupPreviewDialog(BackupCoordinator coordinator) : this()
    {
        _browserCoordinator = coordinator;
        Title = DialogTitleText.Text = "Before-restore backups";
        RestoreBackupButton.Visibility = Visibility.Collapsed;
        ExportFileButton.Visibility = Visibility.Visible;
        SnapshotPanel.Visibility = Visibility.Visible;
        FooterText.Text = "Export saves a separate copy. Current Noted data stays unchanged.";
        BackupDateText.Text = "Checking before-restore backups...";
        VerificationBadge.Visibility = Visibility.Collapsed;
        Loaded += LoadBeforeRestoreBackups;
    }

    private async void LoadBeforeRestoreBackups(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadBeforeRestoreBackups;
        var result = await Task.Run(_browserCoordinator!.GetBeforeRestoreBackups);
        if (!IsVisible)
            return;
        SnapshotStatusText.Text = result.Error ?? "";
        SnapshotStatusText.Visibility = result.Error is null ? Visibility.Collapsed : Visibility.Visible;
        SnapshotSelector.ItemsSource = result.Backups.Select(backup => new BeforeRestoreBackupRow(backup)).ToList();
        if (result.Backups.Count > 0)
            SnapshotSelector.SelectedIndex = 0;
        else
        {
            BackupDateText.Text = result.Error is null ? "No before-restore backups exist yet." : "Backups are unavailable.";
            ShowMessage("Noted creates these snapshots to protect current data before restoration.");
        }
    }

    private void SnapshotSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnapshotSelector.SelectedItem is not BeforeRestoreBackupRow row)
            return;
        var backup = row.Backup;
        _readFile = (_, file) => _browserCoordinator!.ReadBeforeRestoreBackupFile(backup, file);
        ShowPreview(backup.Preview);
        if (!backup.Preview.IsValid)
            BackupDateText.Text = backup.DirectoryName;
    }

    private void ShowPreview(FullBackupPreview preview)
    {
        ++_selectionVersion;
        _preview = preview;
        _needsRecheck = false;
        VerificationBadge.Visibility = Visibility.Visible;
        VerificationText.Text = preview.IsValid ? "Verified" : "Unavailable";
        VerificationText.SetResourceReference(TextBlock.ForegroundProperty,
            preview.IsValid ? "NotedSuccessBrush" : "NotedDangerBrush");
        var date = preview.CreatedUtc?.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt")
            ?? "unknown date";
        BackupDateText.Text = $"Backup from {date}";
        var fileLabel = preview.Files.Count == 1 ? "file" : "files";
        BackupSummaryText.Text = $"{preview.Files.Count} verified {fileLabel} • compared with restore locations";

        BackupFileList.ItemsSource = preview.Files
            .Select(file => new BackupPreviewRow(file))
            .ToList();
        if (BackupFileList.Items.Count > 0)
            BackupFileList.SelectedIndex = 0;
        if (!preview.IsValid)
        {
            BackupSummaryText.Text = preview.Error ?? "The snapshot is no longer available.";
            ShowMessage("This snapshot cannot be verified. Select another snapshot or reopen the browser to check again.");
        }
        UpdateExportButton();
    }

    private async void BackupFileList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var selectionVersion = ++_selectionVersion;
        UpdateExportButton();
        if (BackupFileList.SelectedItem is not BackupPreviewRow row)
        {
            SelectedFileText.Text = "Select a file";
            ShowMessage("Select a file from the backup to preview its contents.");
            return;
        }

        SelectedFileText.Text = $"{row.Category}  •  {row.DisplayPath}";
        ShowMessage("Checking the selected file...");
        var readFile = _readFile;
        var backupId = _preview.BackupId;
        if (readFile is null)
            return;
        var content = await Task.Run(() => readFile(backupId, row.File));
        if (selectionVersion != _selectionVersion ||
            !IsVisible ||
            !ReferenceEquals(BackupFileList.SelectedItem, row))
        {
            return;
        }

        if (!content.Success || content.Data is null)
        {
            if (content.NeedsRecheck)
                MarkBackupForRecheck();
            ShowMessage(content.Error ?? "The selected file could not be previewed.");
            return;
        }

        try
        {
            switch (content.Format)
            {
                case BackupContentFormat.RichText:
                    ShowRichText(content.Data);
                    break;
                case BackupContentFormat.Json:
                    ShowJson(content.Data);
                    break;
                default:
                    ShowText(s_strictUtf8.GetString(content.Data));
                    break;
            }
        }
        catch (Exception ex) when (ex is
                   DecoderFallbackException or
                   JsonException or
                   ArgumentException or
                   IOException)
        {
            ExceptionDiagnostics.Record(ex);
            ShowMessage("This file is verified, but its contents cannot be displayed safely.");
        }
    }

    private void ShowJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        ShowText(JsonSerializer.Serialize(document.RootElement, s_previewJsonOptions));
    }

    private void ShowRichText(byte[] data)
    {
        ContentPreview.Document = new FlowDocument
        {
            PagePadding = new Thickness(2)
        };
        using var stream = new MemoryStream(data, writable: false);
        var range = new TextRange(
            ContentPreview.Document.ContentStart,
            ContentPreview.Document.ContentEnd);
        range.Load(stream, DataFormats.Rtf);
        ContentPreview.ScrollToHome();
    }

    private void ShowText(string text)
    {
        var paragraph = new Paragraph(new Run(text))
        {
            Margin = new Thickness(0)
        };
        ContentPreview.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(2),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = (Brush)FindResource("NotedTextBrush")
        };
        ContentPreview.ScrollToHome();
    }

    private void ShowMessage(string message)
    {
        ShowText(message);
    }

    private void MarkBackupForRecheck()
    {
        if (_needsRecheck)
            return;

        _needsRecheck = true;
        RestoreBackupButton.IsEnabled = false;
        VerificationBadge.Background = (Brush)FindResource("NotedSubtleSurfaceBrush");
        VerificationText.Foreground = (Brush)FindResource("NotedDangerBrush");
        VerificationText.Text = "Recheck needed";
        BackupSummaryText.Text = "The backup changed or became unavailable. Close this window and view it again.";
        UpdateExportButton();
    }

    private void UpdateExportButton() =>
        ExportFileButton.IsEnabled = _preview.IsValid && !_needsRecheck && !_isExporting &&
            BackupFileList.SelectedItem is BackupPreviewRow;

    private async void ExportFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserCoordinator is null || _needsRecheck || _isExporting ||
            SnapshotSelector.SelectedItem is not BeforeRestoreBackupRow snapshot ||
            BackupFileList.SelectedItem is not BackupPreviewRow row)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Backup File",
            Filter = "All files (*.*)|*.*",
            FileName = Path.GetFileName(row.File.LogicalPath.Replace('/', Path.DirectorySeparatorChar)),
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _isExporting = true;
        SnapshotSelector.IsEnabled = BackupFileList.IsEnabled = false;
        UpdateExportButton();
        BackupExportResult result;
        try
        {
            result = await Task.Run(() => _browserCoordinator.ExportBeforeRestoreBackupFile(
                snapshot.Backup, row.File, dialog.FileName));
        }
        finally
        {
            _isExporting = false;
            SnapshotSelector.IsEnabled = BackupFileList.IsEnabled = true;
            UpdateExportButton();
        }
        if (!IsVisible)
            return;
        var message = result.Warning is null ? result.Message : $"{result.Message}\n\n{result.Warning}";
        AppDialog.Show(this, message, result.Success ? "File Exported" : "File Export Failed",
            MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserCoordinator is null && !_needsRecheck)
            DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}

internal sealed class BeforeRestoreBackupRow(BeforeRestoreBackup backup)
{
    internal BeforeRestoreBackup Backup { get; } = backup;
    public string Label => Backup.Preview.IsValid
        ? $"{Backup.Preview.CreatedUtc?.ToLocalTime():MMM d, yyyy h:mm:ss tt}  •  {Backup.Preview.Files.Count} files  •  {Backup.Preview.BackupId.ToString("N")[..8]}"
        : $"{Backup.DirectoryName}  •  unavailable";
}
