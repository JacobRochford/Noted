using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Noted.Models;

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

    public string Load(string filePath) => LoadWithEncoding(filePath).Content;

    public TextFileContent LoadWithEncoding(string filePath) =>
        TextFileCodec.Read(ValidateNotePath(filePath));

    public string? Save(string filePath, string content, TextFileEncoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedPath = ValidateNotePath(filePath);
        var previous = TextFileCodec.Read(validatedPath);
        var targetEncoding = encoding ?? previous.Encoding;
        var bytes = TextFileCodec.Encode(content, targetEncoding);
        var previousContent = previous.Content;
        if (previous.Encoding == targetEncoding && string.Equals(previousContent, content, StringComparison.Ordinal))
            return null;

        var warnings = new List<string>();
        try
        {
            SaveHistoryEntry(validatedPath, previousContent);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            warnings.Add(
                $"'{Path.GetFileName(validatedPath)}' was saved, but its previous version could not be added to note history.");
        }

        FileWriter.WriteAllBytes(validatedPath, bytes);
        byte[] persistedBytes;
        try
        {
            persistedBytes = File.ReadAllBytes(validatedPath);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' was written but could not be read back for verification.",
                ex);
        }

        if (!persistedBytes.AsSpan().SequenceEqual(bytes))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' did not match the text that was saved.");
        }

        return warnings.Count == 0
            ? null
            : string.Join(" ", warnings);
    }

    public string? SaveAs(string filePath, string content, TextFileEncoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedPath = ValidateNotePath(filePath);
        if (File.Exists(validatedPath))
            return Save(validatedPath, content, encoding);

        var bytes = TextFileCodec.Encode(content, encoding ?? TextFileEncoding.Utf8);
        FileWriter.WriteAllBytes(validatedPath, bytes);
        byte[] persistedBytes;
        try
        {
            persistedBytes = File.ReadAllBytes(validatedPath);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' was written but could not be read back for verification.",
                ex);
        }

        if (!persistedBytes.AsSpan().SequenceEqual(bytes))
        {
            throw new FileVerificationException(
                $"'{Path.GetFileName(validatedPath)}' did not match the text that was saved.");
        }

        return null;
    }

    public string? SaveNewNoteAs(string initialPath, string destinationPath, string content, string initialContent, TextFileEncoding? encoding = null)
    {
        var source = ValidateNotePath(initialPath);
        var destination = ValidateNotePath(destinationPath);
        var warning = SaveAs(destination, content, encoding ?? TextFileEncoding.Utf8);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return warning;

        try
        {
            // Hold the exact source file against writes and replacement while comparing and deleting it.
            // DELETE access is needed for FileDispositionInfo; no deletion occurs just by opening this handle.
            using var handle = OpenInitialFile(source, GenericRead | DeleteAccess, ShareRead,
                IntPtr.Zero, OpenExisting, NormalAttributes, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) return warning;
                throw new IOException("The original note could not be opened for cleanup.", new Win32Exception(error));
            }
            using var stream = new FileStream(handle, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (!string.Equals(TextFileCodec.Decode(buffer.ToArray()).Content, initialContent, StringComparison.Ordinal))
                throw new IOException("Its content changed after the note was created.");

            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref disposition, 1))
                throw new IOException("The original note could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            var cleanupWarning = $"The note was saved as '{Path.GetFileName(destination)}', but the original file " +
                $"'{source}' was kept.";
            return string.IsNullOrWhiteSpace(warning) ? cleanupWarning : $"{warning} {cleanupWarning}";
        }
        return warning;
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint NormalAttributes = 0x80;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { public byte DeleteFile; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenInitialFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDispositionInfo information, uint size);

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

    private static bool IsExpectedFileException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

    private sealed record NoteHistoryEntry(
        int SchemaVersion,
        string NotePath,
        string Content,
        DateTime SavedUtc);
}
