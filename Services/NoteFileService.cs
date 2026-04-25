using System.Globalization;
using System.IO;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class NoteFileService : IDisposable {
    private readonly AppSettingsService _settingsService;
    private FileSystemWatcher? _watcher;
    private static readonly TimeSpan DeletedNoteRetention = TimeSpan.FromDays(7);
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string NotesDirectory { get; private set; }
    public string DeletedNotesDirectory { get; }

    public event EventHandler? FilesChanged;

    public NoteFileService(AppSettingsService settingsService) {
        _settingsService = settingsService;
        DeletedNotesDirectory = Path.Combine(_settingsService.StorageDirectory, "DeletedNotes");
        NotesDirectory = ResolveInitialNotesDirectory();
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(DeletedNotesDirectory);
        PurgeExpiredDeletedNotes();
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

    public string CreateNote(string? requestedName = null) {
        var now = DateTime.Now;
        var filename = string.IsNullOrWhiteSpace(requestedName)
            ? BuildUniqueFileName(now)
            : BuildRequestedFileName(requestedName);
        var fullPath = Path.Combine(NotesDirectory, filename);
        File.WriteAllText(fullPath, BuildNewNoteContent(now, _settingsService.LoadTimestampPlacement()));
        return filename;
    }

    public bool ChangeNotesDirectory(string newDirectory) {
        var normalizedPath = Path.GetFullPath(newDirectory);
        if (string.Equals(NotesDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var wasWatching = _watcher is not null;
        StopWatching();

        NotesDirectory = normalizedPath;
        _settingsService.SaveNotesDirectory(NotesDirectory);

        if (wasWatching)
            StartWatching();

        FilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DeleteNote(string fileName) {
        var fullPath = Path.Combine(NotesDirectory, fileName);
        if (!File.Exists(fullPath))
            return;

        Directory.CreateDirectory(DeletedNotesDirectory);
        PurgeExpiredDeletedNotes();

        var deletedPath = BuildDeletedNotePath(fileName);
        File.Move(fullPath, deletedPath);
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
            : $"Modified: {lastWriteTime:MMM dd, yyyy  h:mm tt}";
    }

    private static string BuildNewNoteContent(DateTime timestamp, NoteTimestampPlacement timestampPlacement) {
        var timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return timestampPlacement switch {
            NoteTimestampPlacement.Top => timestampText + Environment.NewLine + Environment.NewLine,
            NoteTimestampPlacement.Bottom => string.Join(Environment.NewLine, Enumerable.Repeat(string.Empty, 40)) + Environment.NewLine + timestampText,
            _ => string.Empty
        };
    }

    private string BuildDeletedNotePath(string fileName) {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(DeletedNotesDirectory, $"{timestamp}__{fileName}");
        if (!File.Exists(candidate))
            return candidate;

        for (int suffix = 1; suffix <= 99; suffix++) {
            candidate = Path.Combine(DeletedNotesDirectory, $"{timestamp}_{suffix:00}__{fileName}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(DeletedNotesDirectory, $"{timestamp}_{Guid.NewGuid():N}__{fileName}");
    }

    private void PurgeExpiredDeletedNotes() {
        if (!Directory.Exists(DeletedNotesDirectory))
            return;

        var cutoff = DateTime.Now - DeletedNoteRetention;
        foreach (var path in Directory.GetFiles(DeletedNotesDirectory, "*.txt")) {
            try {
                var info = new FileInfo(path);
                if (info.LastWriteTime < cutoff)
                    info.Delete();
            } catch {
                // Best-effort cleanup; ignore files that cannot be deleted.
            }
        }
    }

    private string BuildUniqueFileName(DateTime timestamp) {
        var baseName = $"{timestamp:yyyy-MM-dd_HH-mm-ss}";
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

    private string BuildRequestedFileName(string requestedName) {
        var validatedFileName = ValidateAndSanitizeFileName(requestedName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            throw new InvalidOperationException("Invalid or reserved file name.");

        var candidate = validatedFileName + ".txt";
        if (File.Exists(Path.Combine(NotesDirectory, candidate)))
            throw new InvalidOperationException("A file with that name already exists.");

        return candidate;
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
        var timestampText = fileName.StartsWith("Note_", StringComparison.Ordinal)
            ? fileName[5..]
            : fileName;

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
        StopWatching();
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

    private string ResolveInitialNotesDirectory() {
        var savedDirectory = _settingsService.LoadNotesDirectory();
        if (!string.IsNullOrWhiteSpace(savedDirectory))
            return Path.GetFullPath(savedDirectory);

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Notes");
    }

    private void StopWatching() {
        if (_watcher == null)
            return;

        _watcher.Created -= OnFileSystemChanged;
        _watcher.Deleted -= OnFileSystemChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose() {
        StopWatching();
    }
}
