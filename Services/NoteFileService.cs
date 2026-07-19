using System.Globalization;
using System.IO;
using Noted.Models;

namespace Noted.Services;

// handles all note/folder file ops, nav, and watcher
public sealed class NoteFileService : INoteFileService {
    private readonly IAppSettingsService _settingsService;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _directoryWatcher;
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private static readonly TimeSpan DeletedNoteRetention = TimeSpan.FromDays(14); // how long to keep deleted notes
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase) {
        // windows reserved names
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string NotesDirectory { get; private set; }
    public string CurrentDirectory { get; private set; } = "";
    public string CurrentFolderName {
        get {
            // show pretty folder path, or blank if at root
            if (string.Equals(CurrentDirectory, NotesDirectory, StringComparison.OrdinalIgnoreCase))
                return "";
            var relative = Path.GetRelativePath(
                Path.GetFullPath(NotesDirectory),
                Path.GetFullPath(CurrentDirectory));
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ");
        }
    }
    public bool CanNavigateUp =>
        !string.Equals(CurrentDirectory, NotesDirectory, StringComparison.OrdinalIgnoreCase); // at root?
    public bool CanNavigateBack => _backHistory.Count > 0;
    public bool CanNavigateForward => _forwardHistory.Count > 0;
    public string DeletedNotesDirectory { get; }

    public event EventHandler? FilesChanged;

    public NoteFileService(IAppSettingsService settingsService) {
        _settingsService = settingsService;
        DeletedNotesDirectory = Path.Combine(_settingsService.StorageDirectory, "DeletedNotes");
        NotesDirectory = ResolveInitialNotesDirectory();
        CurrentDirectory = NotesDirectory;
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(DeletedNotesDirectory);
        PurgeExpiredDeletedNotes(); // clean up old deleted notes
    }

    public IReadOnlyList<NoteItem> GetNotes() {
        // get all .txt notes in current dir
        if (!Directory.Exists(CurrentDirectory)) CurrentDirectory = NotesDirectory;
        // Notes are always sorted by last modified (descending)
        return Directory.GetFiles(CurrentDirectory, "*.txt")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTime)
            .Select(info => {
                var name = info.Name;
                return new NoteItem {
                    FileName = name,
                    NoteKey = GetNoteKey(info.FullName),
                    DisplayName = FormatNoteName(name),
                    EditableName = Path.GetFileNameWithoutExtension(name),
                    Subtitle = BuildSubtitle(name, info.LastWriteTime),
                    LastModified = info.LastWriteTime
                };
            })
            .ToList();
    }

    public IReadOnlyList<string> GetAllNoteKeys() {
        if (!Directory.Exists(NotesDirectory))
            return Array.Empty<string>();

        return Directory.GetFiles(NotesDirectory, "*.txt", SearchOption.AllDirectories)
            .Select(GetNoteKey)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetNoteKey(string filePath) {
        var fullPath = Path.GetFullPath(filePath);
        if (!IsPathWithinNotesDirectory(fullPath))
            throw new ArgumentException("The note path must be inside the configured notes directory.", nameof(filePath));

        var relativePath = Path.GetRelativePath(Path.GetFullPath(NotesDirectory), fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return $"./{relativePath}";
    }

    public string CreateNote(string? requestedName = null) {
        // create new note, optionally with user-supplied name
        var now = DateTime.Now;
        var filename = string.IsNullOrWhiteSpace(requestedName)
            ? BuildUniqueFileName(now)
            : BuildRequestedFileName(requestedName);
        var fullPath = Path.Combine(CurrentDirectory, filename);
        File.WriteAllText(fullPath, BuildNewNoteContent(now, _settingsService.LoadTimestampPlacement()));
        return filename;
    }

    public bool ChangeNotesDirectory(string newDirectory) {
        // switch to a new notes dir
        var normalizedPath = Path.GetFullPath(newDirectory);
        if (string.Equals(NotesDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var wasWatching = _watcher is not null;
        StopWatching();

        NotesDirectory = normalizedPath;
        CurrentDirectory = normalizedPath;
        ResetNavigationHistory();
        _settingsService.SaveNotesDirectory(NotesDirectory);

        if (wasWatching)
            StartWatching();

        FilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DeleteNote(string fileName, string? containingDirectory = null) {
        // move note to DeletedNotes (soft delete)
        var directory = ResolveNoteDirectory(containingDirectory);
        if (directory is null)
            return;
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!IsPathWithinNotesDirectory(fullPath))
            return;
        if (!File.Exists(fullPath))
            return;

        Directory.CreateDirectory(DeletedNotesDirectory);

        var deletedPath = BuildDeletedNotePath(fileName);
        File.Move(fullPath, deletedPath);
    }

    public (bool Success, string? NewFileName, string? Error) RenameNote(
        string oldFileName,
        string newDisplayName,
        string? containingDirectory = null) {
        // rename note file (validates name)
        var directory = ResolveNoteDirectory(containingDirectory);
        if (directory is null)
            return (false, null, "Access denied.");
        var oldPath = Path.GetFullPath(Path.Combine(directory, oldFileName));
        if (!IsPathWithinNotesDirectory(oldPath))
            return (false, null, "Access denied.");

        var validatedFileName = ValidateAndSanitizeFileName(newDisplayName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            return (false, null, "Invalid or reserved file name.");

        var newFileName = validatedFileName + ".txt";
        if (newFileName == oldFileName) return (true, oldFileName, null);

        var newPath = Path.GetFullPath(Path.Combine(directory, newFileName));
        if (!IsPathWithinNotesDirectory(newPath))
            return (false, null, "Access denied.");

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

    // pretty print note name if it's a generated timestamp
    public static string FormatNoteName(string fileName) {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (TryParseGeneratedNoteDate(name, out var dt)) {
            return dt.ToString("MMM dd, yyyy  h:mm:ss tt");
        }
        return fileName;
    }

    // show file name for generated notes, otherwise show last modified
    private static string BuildSubtitle(string fileName, DateTime lastWriteTime) {
        return IsGeneratedNoteFileName(fileName)
            ? fileName
            : $"Modified: {lastWriteTime:MMM dd, yyyy  h:mm tt}";
    }

    // build note content with timestamp (top/bottom/none)
    private static string BuildNewNoteContent(DateTime timestamp, NoteTimestampPlacement timestampPlacement) {
        var timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return timestampPlacement switch {
            NoteTimestampPlacement.Top => timestampText + Environment.NewLine + Environment.NewLine,
            NoteTimestampPlacement.Bottom => string.Join(Environment.NewLine, Enumerable.Repeat(string.Empty, 30)) + Environment.NewLine + timestampText,
            _ => string.Empty
        };
    }

    // build unique deleted note path (timestamped, avoids collisions)
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

    // delete old files from DeletedNotes
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
                // ignore locked/in-use files
            }
        }
    }

    // build unique note file name (timestamped, avoids collisions)
    private string BuildUniqueFileName(DateTime timestamp) {
        var baseName = $"{timestamp:yyyy-MM-dd_HH-mm-ss}";
        var candidate = baseName + ".txt";
        if (!File.Exists(Path.Combine(CurrentDirectory, candidate)))
            return candidate;

        for (int suffix = 1; suffix <= 99; suffix++) {
            candidate = $"{baseName}_{suffix:00}.txt";
            if (!File.Exists(Path.Combine(CurrentDirectory, candidate)))
                return candidate;
        }

        return $"{baseName}_{Guid.NewGuid():N}.txt";
    }

    // build note file name from user input (throws if invalid or taken)
    private string BuildRequestedFileName(string requestedName) {
        var validatedFileName = ValidateAndSanitizeFileName(requestedName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            throw new InvalidOperationException("Invalid or reserved file name.");

        var candidate = validatedFileName + ".txt";
        if (File.Exists(Path.Combine(CurrentDirectory, candidate)))
            throw new InvalidOperationException("A file with that name already exists.");

        return candidate;
    }

    // remove invalid chars, reserved names, .txt extension
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

    // is this a generated note file name (timestamped)?
    private static bool IsGeneratedNoteFileName(string fileName) {
        return TryParseGeneratedNoteDate(Path.GetFileNameWithoutExtension(fileName), out _);
    }

    // try to parse a generated note file name as a timestamp
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

    // start watching for file/folder changes
    public void StartWatching() {
        StopWatching();
        _watcher = new FileSystemWatcher(NotesDirectory, "*.txt") {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileSystemChanged;
        _watcher.Changed += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;
        // separate watcher for folder create/rename/delete ("*.txt" filter misses dirs)
        _directoryWatcher = new FileSystemWatcher(NotesDirectory) {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        _directoryWatcher.Created += OnFileSystemChanged;
        _directoryWatcher.Deleted += OnFileSystemChanged;
        _directoryWatcher.Renamed += OnFileSystemChanged;
    }

    // notify listeners on file/folder change
    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) {
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // get all folders in current dir
    public IReadOnlyList<NoteItem> GetFolders() {
        if (!Directory.Exists(CurrentDirectory)) CurrentDirectory = NotesDirectory;
        return Directory.GetDirectories(CurrentDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => new NoteItem {
                FileName = info.Name,
                DisplayName = info.Name,
                EditableName = info.Name,
                Subtitle = "",
                IsFolder = true
            })
            .ToList();
    }

    // get .txt notes in a subfolder, don't change nav state (Expand mode)
    public IReadOnlyList<NoteItem> GetNotesInSubfolder(string subfolderName) {
        var subfolderPath = Path.GetFullPath(Path.Combine(NotesDirectory, subfolderName));
        var root = Path.GetFullPath(NotesDirectory);
        // block path traversal, don't let user escape root
        if (!subfolderPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<NoteItem>();
        if (!Directory.Exists(subfolderPath))
            return Array.Empty<NoteItem>();
        return Directory.GetFiles(subfolderPath, "*.txt")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTime)
            .Select(info => {
                var name = info.Name;
                return new NoteItem {
                    FileName = name,
                    NoteKey = GetNoteKey(info.FullName),
                    DisplayName = FormatNoteName(name),
                    EditableName = Path.GetFileNameWithoutExtension(name),
                    Subtitle = BuildSubtitle(name, info.LastWriteTime),
                    FullPath = info.FullName,
                    LastModified = info.LastWriteTime
                };
            })
            .ToList();
    }

    // go into a folder (blocks path traversal)
    public void NavigateTo(string folderName) {
        var target = Path.GetFullPath(Path.Combine(CurrentDirectory, folderName));
        var root = Path.GetFullPath(NotesDirectory);
        // block path traversal, don't let user escape root
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            return;
        NavigateToDirectory(target, clearForwardHistory: true);
    }

    // go up one folder (blocks escape)
    public void NavigateUp() {
        if (!CanNavigateUp) return;
        var parent = Path.GetDirectoryName(CurrentDirectory);
        var root = Path.GetFullPath(NotesDirectory);
        var target = parent is null || !parent.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? NotesDirectory
            : parent;
        NavigateToDirectory(target, clearForwardHistory: true);
    }

    public void NavigateBack() {
        if (!TryPopValidHistoryEntry(_backHistory, out var target))
            return;

        _forwardHistory.Push(CurrentDirectory);
        CurrentDirectory = target;
    }

    public void NavigateForward() {
        if (!TryPopValidHistoryEntry(_forwardHistory, out var target))
            return;

        _backHistory.Push(CurrentDirectory);
        CurrentDirectory = target;
    }

    // create a new folder (sanitizes name, blocks traversal)
    public (bool Success, string? Error) CreateFolder(string folderName) {
        var sanitized = ValidateAndSanitizeFolderName(folderName);
        if (string.IsNullOrWhiteSpace(sanitized))
            return (false, "Invalid or reserved folder name.");

        var target = Path.GetFullPath(Path.Combine(CurrentDirectory, sanitized));
        if (!target.StartsWith(Path.GetFullPath(NotesDirectory), StringComparison.OrdinalIgnoreCase))
            return (false, "Access denied.");
        if (Directory.Exists(target))
            return (false, "A folder with that name already exists.");

        try {
            Directory.CreateDirectory(target);
            return (true, null);
        } catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    // rename a folder (sanitizes name, blocks traversal)
    public (bool Success, string? Error) RenameFolder(string oldName, string newName) {
        var sanitized = ValidateAndSanitizeFolderName(newName);
        if (string.IsNullOrWhiteSpace(sanitized))
            return (false, "Invalid or reserved folder name.");
        if (string.Equals(sanitized, oldName, StringComparison.OrdinalIgnoreCase))
            return (true, null);

        var root = Path.GetFullPath(NotesDirectory) + Path.DirectorySeparatorChar;
        var oldPath = Path.GetFullPath(Path.Combine(CurrentDirectory, oldName));
        var newPath = Path.GetFullPath(Path.Combine(CurrentDirectory, sanitized));

        if (!oldPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return (false, "Access denied.");
        if (!Directory.Exists(oldPath))
            return (false, "Folder not found.");
        if (Directory.Exists(newPath))
            return (false, "A folder with that name already exists.");

        try {
            Directory.Move(oldPath, newPath);
            return (true, null);
        } catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    // delete a folder (blocks traversal)
    public (bool Success, string? Error) DeleteFolder(string folderName) {
        var target = Path.GetFullPath(Path.Combine(CurrentDirectory, folderName));
        var root = Path.GetFullPath(NotesDirectory) + Path.DirectorySeparatorChar;

        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return (false, "Access denied.");
        if (!Directory.Exists(target))
            return (false, "Folder not found.");

        try {
            Directory.Delete(target, recursive: true);
            return (true, null);
        } catch (Exception ex) {
            return (false, ex.Message);
        }
    }

    // remove invalid chars, reserved names
    private static string? ValidateAndSanitizeFolderName(string value) {
        var sanitized = Path.GetInvalidFileNameChars().Aggregate(
            value,
            (current, c) => current.Replace(c.ToString(), "")
        ).Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(sanitized) || ReservedFileNames.Contains(sanitized))
            return null;

        return sanitized;
    }

    // get initial notes dir (from settings or default)
    private string ResolveInitialNotesDirectory() {
        var savedDirectory = _settingsService.LoadNotesDirectory();
        if (!string.IsNullOrWhiteSpace(savedDirectory))
            return Path.GetFullPath(savedDirectory);

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Notes");
    }

    private string? ResolveNoteDirectory(string? containingDirectory) {
        var directory = Path.GetFullPath(containingDirectory ?? CurrentDirectory);
        return IsPathWithinNotesDirectory(directory) ? directory : null;
    }

    private bool IsPathWithinNotesDirectory(string path) {
        var root = Path.GetFullPath(NotesDirectory);
        var candidate = Path.GetFullPath(path);
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void NavigateToDirectory(string target, bool clearForwardHistory) {
        if (!Directory.Exists(target)
            || string.Equals(target, CurrentDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        _backHistory.Push(CurrentDirectory);
        CurrentDirectory = target;

        if (clearForwardHistory)
            _forwardHistory.Clear();
    }

    private bool TryPopValidHistoryEntry(Stack<string> history, out string target) {
        var root = Path.GetFullPath(NotesDirectory);

        while (history.Count > 0) {
            var candidate = history.Pop();
            if (!Directory.Exists(candidate))
                continue;

            var fullCandidate = Path.GetFullPath(candidate);
            if (string.Equals(fullCandidate, root, StringComparison.OrdinalIgnoreCase)
                || fullCandidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                target = fullCandidate;
                return true;
            }
        }

        target = string.Empty;
        return false;
    }

    private void ResetNavigationHistory() {
        _backHistory.Clear();
        _forwardHistory.Clear();
    }

    private void StopWatching() {
        if (_watcher != null) {
            _watcher.Created -= OnFileSystemChanged;
            _watcher.Changed -= OnFileSystemChanged;
            _watcher.Deleted -= OnFileSystemChanged;
            _watcher.Renamed -= OnFileSystemChanged;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_directoryWatcher != null) {
            _directoryWatcher.Created -= OnFileSystemChanged;
            _directoryWatcher.Deleted -= OnFileSystemChanged;
            _directoryWatcher.Renamed -= OnFileSystemChanged;
            _directoryWatcher.Dispose();
            _directoryWatcher = null;
        }
    }

    public void Dispose() {
        StopWatching();
    }
}
