using System.Globalization;
using System.IO;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class NoteFileService : IDisposable {
    private FileSystemWatcher? _watcher;
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string NotesDirectory { get; }

    public event EventHandler? FilesChanged;

    public NoteFileService() {
        NotesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Notes");
        Directory.CreateDirectory(NotesDirectory);
    }

    public IReadOnlyList<NoteItem> GetNotes() {
        return Directory.GetFiles(NotesDirectory, "*.txt")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTime)
            .Select(info => {
                var name = info.Name;
                return new NoteItem {
                    FileName = name,
                    DisplayName = FormatNoteName(name),
                    EditableName = Path.GetFileNameWithoutExtension(name),
                    Subtitle = BuildSubtitle(name, info.LastWriteTime)
                };
            })
            .ToList();
    }

    public string CreateNote() {
        var now = DateTime.Now;
        var filename = BuildUniqueGeneratedFileName(now);
        var fullPath = Path.Combine(NotesDirectory, filename);
        File.WriteAllText(fullPath, $"Created: {now:yyyy-MM-dd HH:mm:ss}\r\n\r\n");
        return filename;
    }

    public void DeleteNote(string fileName) {
        var fullPath = Path.Combine(NotesDirectory, fileName);
        File.Delete(fullPath);
    }

    public (bool Success, string? NewFileName, string? Error) RenameNote(string oldFileName, string newDisplayName) {
        var oldPath = Path.Combine(NotesDirectory, oldFileName);

        var validatedFileName = ValidateAndSanitizeFileName(newDisplayName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            return (false, null, "Invalid or reserved file name.");

        var newFileName = validatedFileName + ".txt";
        if (newFileName == oldFileName) return (true, oldFileName, null);

        var newPath = Path.Combine(NotesDirectory, newFileName);

        try {
            if (!File.Exists(oldPath))
                return (false, null, "Original file not found.");
            if (File.Exists(newPath))
                return (false, null, "A file with that name already exists.");

            File.Move(oldPath, newPath);
            return (true, newFileName, null);
        } catch (Exception ex) {
            return (false, null, ex.Message);
        }
    }

    public static string FormatNoteName(string fileName) {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (TryParseGeneratedNoteDate(name, out var dt)) {
            return dt.ToString("MMM dd, yyyy  h:mm:ss tt");
        }
        return fileName;
    }

    private static string BuildSubtitle(string fileName, DateTime lastWriteTime) {
        return IsGeneratedNoteFileName(fileName)
            ? fileName
            : $"Modified {lastWriteTime:MMM dd, yyyy  h:mm tt}";
    }

    private string BuildUniqueGeneratedFileName(DateTime timestamp) {
        var baseName = $"Note_{timestamp:yyyy-MM-dd_HH-mm-ss}";
        var candidate = baseName + ".txt";
        if (!File.Exists(Path.Combine(NotesDirectory, candidate)))
            return candidate;

        for (int suffix = 1; suffix <= 99; suffix++) {
            candidate = $"{baseName}_{suffix:00}.txt";
            if (!File.Exists(Path.Combine(NotesDirectory, candidate)))
                return candidate;
        }

        return $"{baseName}_{Guid.NewGuid():N}.txt";
    }

    private static string? ValidateAndSanitizeFileName(string value) {
        var sanitized = Path.GetInvalidFileNameChars().Aggregate(
            value,
            (current, c) => current.Replace(c.ToString(), "")
        ).Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(sanitized))
            return null;

        if (sanitized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            sanitized = sanitized[..^4].TrimEnd();

        if (string.IsNullOrWhiteSpace(sanitized) || ReservedFileNames.Contains(sanitized))
            return null;

        return sanitized;
    }

    private static bool IsGeneratedNoteFileName(string fileName) {
        return TryParseGeneratedNoteDate(Path.GetFileNameWithoutExtension(fileName), out _);
    }

    private static bool TryParseGeneratedNoteDate(string fileName, out DateTime timestamp) {
        timestamp = default;
        if (!fileName.StartsWith("Note_", StringComparison.Ordinal))
            return false;

        var timestampText = fileName[5..];
        var underscoreIndex = timestampText.LastIndexOf('_');
        if (underscoreIndex > 0) {
            var suffix = timestampText[(underscoreIndex + 1)..];
            if (suffix.Length == 2 && suffix.All(char.IsDigit))
                timestampText = timestampText[..underscoreIndex];
        }

        return DateTime.TryParseExact(timestampText, "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    public void StartWatching() {
        _watcher = new FileSystemWatcher(NotesDirectory, "*.txt") {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += (_, _) => FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) {
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() {
        _watcher?.Dispose();
        _watcher = null;
    }
}
