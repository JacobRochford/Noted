using System.IO;
using System.Security;
using System.Text;

namespace Noted.Services;

public sealed class ScratchpadContentService : IScratchpadContentService
{
    private readonly string _contentFilePath;
    private readonly string _backupFilePath;
    private bool _preserveBackupOnNextSave;
    private bool _writesBlocked;

    public ScratchpadContentService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _contentFilePath = Path.Combine(Path.GetFullPath(storageDirectory), "scratchpad.rtf");
        _backupFilePath = $"{_contentFilePath}.bak";
    }

    public ScratchpadContentLoadResult TryLoadContent()
    {
        var primary = ReadText(_contentFilePath);
        if (primary.Success)
            return new ScratchpadContentLoadResult(true, true, primary.Content, null, null);

        var backup = ReadText(_backupFilePath);
        if (backup.Success)
        {
            _preserveBackupOnNextSave = primary.Status == JsonFileReadStatus.Unavailable;
            var reason = primary.Status == JsonFileReadStatus.Missing
                ? "the main scratchpad file was missing"
                : "the main scratchpad file could not be read";
            return new ScratchpadContentLoadResult(
                true,
                true,
                backup.Content,
                null,
                $"Scratchpad content was restored from its rolling backup because {reason}.");
        }

        _writesBlocked = primary.Status == JsonFileReadStatus.Unavailable ||
            backup.Status == JsonFileReadStatus.Unavailable;
        var failure = primary.Status != JsonFileReadStatus.Missing ? primary : backup;
        return failure.Status == JsonFileReadStatus.Missing
            ? new ScratchpadContentLoadResult(true, false, null, null, null)
            : new ScratchpadContentLoadResult(false, false, null, failure.Error, null);
    }

    public ScratchpadContentSaveResult TrySaveContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_writesBlocked)
        {
            return new ScratchpadContentSaveResult(
                false,
                "Scratchpad content cannot be saved because an existing content file could not be read. Resolve the reported file error and restart Noted before retrying.",
                null);
        }

        try
        {
            var existing = ReadText(_contentFilePath);
            if (existing.Success && string.Equals(existing.Content, content, StringComparison.Ordinal))
                return new ScratchpadContentSaveResult(true, null, null);

            var writeResult = FileWriter.WriteAllText(
                _contentFilePath,
                content,
                _preserveBackupOnNextSave ? null : _backupFilePath);
            var verification = ReadText(_contentFilePath);
            if (!verification.Success ||
                !string.Equals(verification.Content, content, StringComparison.Ordinal))
            {
                _writesBlocked = true;
                return new ScratchpadContentSaveResult(
                    false,
                    "Scratchpad content was written but could not be read back and verified.",
                    writeResult.Warning);
            }

            _preserveBackupOnNextSave = false;
            return new ScratchpadContentSaveResult(true, null, writeResult.Warning);
        }
        catch (IOException ex)
        {
            return new ScratchpadContentSaveResult(false, ex.Message, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ScratchpadContentSaveResult(false, ex.Message, null);
        }
        catch (SecurityException ex)
        {
            return new ScratchpadContentSaveResult(false, ex.Message, null);
        }
    }

    private static TextReadResult ReadText(string path)
    {
        try
        {
            return new TextReadResult(
                JsonFileReadStatus.Success,
                File.ReadAllText(path, Encoding.UTF8),
                null);
        }
        catch (FileNotFoundException)
        {
            return new TextReadResult(JsonFileReadStatus.Missing, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new TextReadResult(JsonFileReadStatus.Missing, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new TextReadResult(JsonFileReadStatus.Unavailable, null, ex.Message);
        }
    }

    private sealed record TextReadResult(
        JsonFileReadStatus Status,
        string? Content,
        string? Error)
    {
        internal bool Success => Status == JsonFileReadStatus.Success;
    }
}
