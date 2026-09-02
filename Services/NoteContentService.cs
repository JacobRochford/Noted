using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Noted.Services;

public sealed class NoteContentService : INoteContentService
{
    private const int DefaultHistoryLimit = 20;
    private readonly string _historyDirectory;
    private readonly TimeProvider _clock;
    private readonly int _historyLimit;

    public NoteContentService(string storageDirectory)
        : this(storageDirectory, TimeProvider.System, DefaultHistoryLimit)
    {
    }

    internal NoteContentService(
        string storageDirectory,
        TimeProvider clock,
        int historyLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        if (historyLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(historyLimit));

        _historyDirectory = Path.Combine(
            Path.GetFullPath(storageDirectory),
            "note-history");
        _clock = clock;
        _historyLimit = historyLimit;
    }

    public string Load(string filePath)
    {
        var validatedPath = ValidateNotePath(filePath);
        return File.ReadAllText(validatedPath, Encoding.UTF8);
    }

    public string? Save(string filePath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedPath = ValidateNotePath(filePath);
        var previousContent = File.ReadAllText(validatedPath, Encoding.UTF8);
        if (string.Equals(previousContent, content, StringComparison.Ordinal))
            return null;

        var warnings = new List<string>();
        try
        {
            SaveHistoryEntry(validatedPath, previousContent);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            warnings.Add(
                $"'{Path.GetFileName(validatedPath)}' was saved, but its previous version could not be added to note history: {ex.Message}");
        }

        FileWriter.WriteAllText(validatedPath, content);
        string persistedContent;
        try
        {
            persistedContent = File.ReadAllText(validatedPath, Encoding.UTF8);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' was written but could not be read back for verification.",
                ex);
        }

        if (!string.Equals(persistedContent, content, StringComparison.Ordinal))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' did not match the text that was saved.");
        }

        return warnings.Count == 0
            ? null
            : string.Join(" ", warnings);
    }

    public string? SaveAs(string filePath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedPath = ValidateNotePath(filePath);
        if (File.Exists(validatedPath))
            return Save(validatedPath, content);

        FileWriter.WriteAllText(validatedPath, content);
        string persistedContent;
        try
        {
            persistedContent = File.ReadAllText(validatedPath, Encoding.UTF8);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' was written but could not be read back for verification.",
                ex);
        }

        if (!string.Equals(persistedContent, content, StringComparison.Ordinal))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' did not match the text that was saved.");
        }

        return null;
    }

    private void SaveHistoryEntry(string notePath, string content)
    {
        var noteHistoryDirectory = GetNoteHistoryDirectory(notePath);
        var contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (Directory.Exists(noteHistoryDirectory) &&
            Directory.EnumerateFiles(noteHistoryDirectory, $"*_{contentHash}.json").Any())
        {
            return;
        }

        var savedUtc = _clock.GetUtcNow().UtcDateTime;
        var historyPath = Path.Combine(
            noteHistoryDirectory,
            $"{savedUtc:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{contentHash}.json");
        JsonFileStore.Write(
            historyPath,
            new NoteHistoryEntry(1, notePath, content, savedUtc));
        TrimHistory(noteHistoryDirectory);
    }

    private void TrimHistory(string noteHistoryDirectory)
    {
        var historyFiles = Directory
            .EnumerateFiles(noteHistoryDirectory, "*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        foreach (var path in historyFiles.Skip(_historyLimit))
            FileWriter.DeleteIfExists(path);
    }

    private string GetNoteHistoryDirectory(string notePath)
    {
        var normalizedPath = Path.GetFullPath(notePath).ToUpperInvariant();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return Path.Combine(_historyDirectory, hash);
    }

    private string ValidateNotePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);

        if (!NoteFileExtensions.IsSupported(fullPath))
            throw new ArgumentException(
                "Only supported text-based files can be opened or saved.",
                nameof(filePath));

        return fullPath;
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException;
    }

    private sealed record NoteHistoryEntry(
        int SchemaVersion,
        string NotePath,
        string Content,
        DateTime SavedUtc);
}
