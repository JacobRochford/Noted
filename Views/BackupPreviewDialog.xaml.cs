using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Noted.Helpers;
using Noted.Services;

namespace Noted;

internal sealed class BackupPreviewRow
{
    private static readonly Brush GreenBackground = CreateBrush(221, 242, 231);
    private static readonly Brush GreenForeground = CreateBrush(52, 112, 82);
    private static readonly Brush AmberBackground = CreateBrush(255, 240, 210);
    private static readonly Brush AmberForeground = CreateBrush(145, 99, 28);
    private static readonly Brush RedBackground = CreateBrush(250, 226, 226);
    private static readonly Brush RedForeground = CreateBrush(160, 70, 70);
    private static readonly Brush GrayBackground = CreateBrush(231, 238, 242);
    private static readonly Brush GrayForeground = CreateBrush(95, 116, 128);

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

    public Brush StatusBackground => File.Comparison switch
    {
        BackupFileComparison.Unchanged => GreenBackground,
        BackupFileComparison.Changed => AmberBackground,
        BackupFileComparison.Missing => RedBackground,
        _ => GrayBackground
    };

    public Brush StatusForeground => File.Comparison switch
    {
        BackupFileComparison.Unchanged => GreenForeground,
        BackupFileComparison.Changed => AmberForeground,
        BackupFileComparison.Missing => RedForeground,
        _ => GrayForeground
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
}

public partial class BackupPreviewDialog : Window
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly FullBackupPreview _preview;
    private readonly Func<Guid, BackupFileSummary, BackupFileContent> _readFile;
    private readonly WindowEdgeResizer _edgeResizer;
    private int _selectionVersion;
    private bool _needsRecheck;

    internal BackupPreviewDialog(
        FullBackupPreview preview,
        Func<Guid, BackupFileSummary, BackupFileContent> readFile,
        bool isImport = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(readFile);

        InitializeComponent();
        _preview = preview;
        _readFile = readFile;
        _edgeResizer = new WindowEdgeResizer(this, () => { });
        Closed += (_, _) => _selectionVersion++;

        if (isImport)
        {
            DialogTitleText.Text = "Imported backup contents";
            RestoreBackupButton.Content = "Restore Imported Backup";
            FooterText.Text = "Preview is read-only. Import verifies every file again before restoring.";
        }

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
    }

    private async void BackupFileList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (BackupFileList.SelectedItem is not BackupPreviewRow row)
        {
            SelectedFileText.Text = "Select a file";
            ShowMessage("Select a file from the backup to preview its contents.");
            return;
        }

        SelectedFileText.Text = $"{row.Category}  •  {row.DisplayPath}";
        ShowMessage("Checking the selected file...");
        var selectionVersion = ++_selectionVersion;
        var content = await Task.Run(() => _readFile(_preview.BackupId, row.File));
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
                    ShowText(StrictUtf8.GetString(content.Data));
                    break;
            }
        }
        catch (Exception ex) when (ex is
                   DecoderFallbackException or
                   JsonException or
                   ArgumentException or
                   IOException)
        {
            ShowMessage($"This file is verified, but its contents cannot be displayed safely: {ex.Message}");
        }
    }

    private void ShowJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        ShowText(JsonSerializer.Serialize(document.RootElement, PreviewJsonOptions));
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
            Foreground = new SolidColorBrush(Color.FromRgb(45, 62, 72))
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
        VerificationBadge.Background = new SolidColorBrush(Color.FromRgb(250, 226, 226));
        VerificationText.Foreground = new SolidColorBrush(Color.FromRgb(160, 70, 70));
        VerificationText.Text = "Recheck needed";
        BackupSummaryText.Text = "The backup changed or became unavailable. Close this window and view it again.";
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_needsRecheck)
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
