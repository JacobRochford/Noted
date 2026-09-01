using System.Globalization;
using System.IO;
using Noted.Helpers;
using Noted.Models;

namespace Noted.Services;

// handles all note/folder file ops, nav, and watcher
public sealed class NoteFileService : INoteFileService {
    private const string DeletedNoteTimestampFormat = "yyyy-MM-dd_HH-mm-ss_fff";
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
    public string ArchivedNotesDirectory => Path.Combine(NotesDirectory, ".archive");

    public event EventHandler? FilesChanged;

    public NoteFileService(IAppSettingsService settingsService) {
        _settingsService = settingsService;
        DeletedNotesDirectory = Path.Combine(_settingsService.AppDataDirectory, "DeletedNotes");
        NotesDirectory = ResolveInitialNotesDirectory();
        CurrentDirectory = NotesDirectory;
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(DeletedNotesDirectory);
        PurgeExpiredDeletedNotes(); // clean up old deleted notes
    }

    public IReadOnlyList<NoteItem> GetNotes() {
        // get all supported notes in current dir
        if (!Directory.Exists(CurrentDirectory)) CurrentDirectory = NotesDirectory;
        // Notes are always sorted by last modified (descending)
        return EnumerateNoteFiles(CurrentDirectory)
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

        return EnumerateNoteFiles(NotesDirectory, SearchOption.AllDirectories)
            .Where(path => !IsPathWithinArchiveDirectory(path))
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

    public CreatedNote CreateNote(string? requestedName = null) {
        // create new note, optionally with user-supplied name
        var now = DateTime.Now;
        var usesGeneratedName = string.IsNullOrWhiteSpace(requestedName);
        var filename = usesGeneratedName
            ? BuildUniqueFileName(now)
            : BuildRequestedFileName(requestedName!);
        var fullPath = Path.Combine(CurrentDirectory, filename);
        var content = BuildNewNoteContent(now, _settingsService.LoadTimestampPlacement());
        FileWriter.WriteAllText(fullPath, content);
        if (!string.Equals(File.ReadAllText(fullPath), content, StringComparison.Ordinal))
            throw new IOException("The new note was created but could not be verified.");
        return new CreatedNote(filename, usesGeneratedName);
    }

    public bool ChangeNotesDirectory(string newDirectory) {
        // switch to a new notes dir
        var normalizedPath = Path.GetFullPath(newDirectory);
        if (string.Equals(NotesDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var wasWatching = _watcher is not null;
        FileSystemWatcher? newFileWatcher = null;
        FileSystemWatcher? newDirectoryWatcher = null;

        try {
            if (wasWatching)
                (newFileWatcher, newDirectoryWatcher) = CreateWatchers(normalizedPath);

            // Persist before exposing the new root to callers. If this fails,
            // the current root, navigation state, and existing watchers remain intact.
            _settingsService.SaveNotesDirectory(normalizedPath);
        } catch {
            DisposeFileWatcher(newFileWatcher);
            DisposeDirectoryWatcher(newDirectoryWatcher);
            throw;
        }

        NotesDirectory = normalizedPath;
        CurrentDirectory = normalizedPath;
        ResetNavigationHistory();

        if (newFileWatcher is not null && newDirectoryWatcher is not null)
            ReplaceWatchers(newFileWatcher, newDirectoryWatcher);

        FilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool DeleteNote(string fileName, string? containingDirectory = null) {
        // move note to DeletedNotes (soft delete)
        var directory = ResolveNoteDirectory(containingDirectory);
        if (directory is null ||
            !NoteFileExtensions.IsSupported(fileName) ||
            !TryResolveChildPath(NotesDirectory, directory, fileName, out var fullPath))
            return false;
        if (!File.Exists(fullPath))
            return false;

        Directory.CreateDirectory(DeletedNotesDirectory);

        var deletedPath = BuildDeletedNotePath(Path.GetFileName(fullPath));
        File.Move(fullPath, deletedPath);
        return true;
    }

    public IReadOnlyList<ArchivedNoteItem> GetArchivedNotes() {
        if (!Directory.Exists(ArchivedNotesDirectory))
            return Array.Empty<ArchivedNoteItem>();

        return EnumerateNoteFiles(ArchivedNotesDirectory, SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTime)
            .Select(info => {
                var relativePath = Path.GetRelativePath(ArchivedNotesDirectory, info.FullName);
                var folder = Path.GetDirectoryName(relativePath)?
                    .Replace(Path.DirectorySeparatorChar, '/');
                var subtitle = BuildSubtitle(info.Name, info.LastWriteTime);
                return new ArchivedNoteItem {
                    RelativePath = relativePath,
                    FileName = info.Name,
                    DisplayName = FormatNoteName(info.Name),
                    Subtitle = string.IsNullOrWhiteSpace(folder)
                        ? subtitle
                        : $"{folder} • {subtitle}",
                    LastModified = info.LastWriteTime
                };
            })
            .ToList();
    }

    public (bool Success, string? Error) ArchiveNote(
        string fileName,
        string? containingDirectory = null) {
        var directory = ResolveNoteDirectory(containingDirectory);
        if (directory is null)
            return (false, "Access denied.");

        if (!NoteFileExtensions.IsSupported(fileName) ||
            !TryResolveChildPath(NotesDirectory, directory, fileName, out var sourcePath) ||
            IsPathWithinArchiveDirectory(sourcePath) ||
            !File.Exists(sourcePath)) {
            return (false, "The note could not be found.");
        }

        var relativePath = Path.GetRelativePath(NotesDirectory, sourcePath);
        if (!TryResolveChildPath(
                ArchivedNotesDirectory,
                ArchivedNotesDirectory,
                relativePath,
                out var archivePath))
            return (false, "Access denied.");
        if (File.Exists(archivePath))
            return (false, "A note with the same path is already archived.");

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            File.Move(sourcePath, archivePath);
            FilesChanged?.Invoke(this, EventArgs.Empty);
            return (true, null);
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? Error) RestoreArchivedNote(string relativePath) {
        if (string.IsNullOrWhiteSpace(relativePath))
            return (false, "The archived note path is required.");

        if (!TryResolveChildPath(
                ArchivedNotesDirectory,
                ArchivedNotesDirectory,
                relativePath,
                out var sourcePath) ||
            !TryResolveChildPath(
                NotesDirectory,
                NotesDirectory,
                relativePath,
                out var destinationPath) ||
            IsPathWithinArchiveDirectory(destinationPath) ||
            !NoteFileExtensions.IsSupported(sourcePath) ||
            !File.Exists(sourcePath)) {
            return (false, "The archived note could not be found.");
        }
        if (File.Exists(destinationPath))
            return (false, "A note already exists at the original path.");

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);
            DeleteEmptyArchiveDirectories(Path.GetDirectoryName(sourcePath));
            FilesChanged?.Invoke(this, EventArgs.Empty);
            return (true, null);
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? NewFileName, string? Error) RenameNote(
        string oldFileName,
        string newDisplayName,
        string? containingDirectory = null) {
        // rename note file (validates name)
        var directory = ResolveNoteDirectory(containingDirectory);
        if (directory is null)
            return (false, null, "Access denied.");
        if (!TryResolveChildPath(NotesDirectory, directory, oldFileName, out var oldPath))
            return (false, null, "Access denied.");

        var validatedFileName = ValidateAndSanitizeFileName(newDisplayName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            return (false, null, "Invalid or reserved file name.");

        var extension = Path.GetExtension(oldFileName);
        if (!NoteFileExtensions.IsSupported(oldFileName))
            return (false, null, "Unsupported note file type.");

        var newFileName = validatedFileName + extension;
        if (newFileName == oldFileName) return (true, oldFileName, null);

        if (!TryResolveChildPath(NotesDirectory, directory, newFileName, out var newPath))
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
        return NoteNameFormatter.Format(fileName, keepExtensionForCustomName: true);
    }

    // show file name for generated notes, otherwise show last modified
    private static string BuildSubtitle(string fileName, DateTime lastWriteTime) {
        return NoteNameFormatter.IsGenerated(fileName)
            ? fileName
            : $"Modified: {lastWriteTime:MMM dd, yyyy  h:mm tt}";
    }

    // build note content with timestamp (top/bottom/none)
    private static string BuildNewNoteContent(DateTime timestamp, NoteTimestampPlacement timestampPlacement) {
        var timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return timestampPlacement switch {
            NoteTimestampPlacement.Top => timestampText + Environment.NewLine + Environment.NewLine + Environment.NewLine,
            NoteTimestampPlacement.Bottom => string.Join(Environment.NewLine, Enumerable.Repeat(string.Empty, 30)) + Environment.NewLine + timestampText,
            _ => string.Empty
        };
    }

    // build unique deleted note path (timestamped, avoids collisions)
    private string BuildDeletedNotePath(string fileName) {
        var timestamp = DateTime.Now.ToString(DeletedNoteTimestampFormat, CultureInfo.InvariantCulture);
        var pathToTry = Path.Combine(DeletedNotesDirectory, $"{timestamp}__{fileName}");
        if (!File.Exists(pathToTry))
            return pathToTry;

        for (int suffix = 1; suffix <= 99; suffix++) {
            pathToTry = Path.Combine(DeletedNotesDirectory, $"{timestamp}_{suffix:00}__{fileName}");
            if (!File.Exists(pathToTry))
                return pathToTry;
        }

        return Path.Combine(DeletedNotesDirectory, $"{timestamp}_{Guid.NewGuid():N}__{fileName}");
    }

    // delete old files from DeletedNotes
    private void PurgeExpiredDeletedNotes() {
        if (!Directory.Exists(DeletedNotesDirectory))
            return;

        var cutoff = DateTime.Now - DeletedNoteRetention;
        foreach (var path in EnumerateNoteFiles(DeletedNotesDirectory)) {
            try {
                if (TryGetDeletionTime(path, out var deletedAt) && deletedAt < cutoff)
                    File.Delete(path);
            } catch {
                // ignore locked/in-use files
            }
        }
    }

    private static bool TryGetDeletionTime(string path, out DateTime deletedAt) {
        var fileName = Path.GetFileName(path);
        var separatorIndex = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separatorIndex < DeletedNoteTimestampFormat.Length) {
            deletedAt = default;
            return false;
        }

        var timestamp = fileName[..DeletedNoteTimestampFormat.Length];
        return DateTime.TryParseExact(
            timestamp,
            DeletedNoteTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out deletedAt);
    }

    // build unique note file name (timestamped, avoids collisions)
    private string BuildUniqueFileName(DateTime timestamp) {
        var baseName = $"{timestamp:yyyy-MM-dd_HH-mm-ss}";
        var fileNameToTry = baseName + ".txt";
        if (!File.Exists(Path.Combine(CurrentDirectory, fileNameToTry)))
            return fileNameToTry;

        for (int suffix = 1; suffix <= 99; suffix++) {
            fileNameToTry = $"{baseName}_{suffix:00}.txt";
            if (!File.Exists(Path.Combine(CurrentDirectory, fileNameToTry)))
                return fileNameToTry;
        }

        return $"{baseName}_{Guid.NewGuid():N}.txt";
    }

    // build note file name from user input (throws if invalid or taken)
    private string BuildRequestedFileName(string requestedName) {
        var validatedFileName = ValidateAndSanitizeFileName(requestedName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            throw new InvalidOperationException("Invalid or reserved file name.");

        var fileName = validatedFileName + ".txt";
        if (File.Exists(Path.Combine(CurrentDirectory, fileName)))
            throw new InvalidOperationException("A file with that name already exists.");

        return fileName;
    }

    // remove invalid chars, reserved names, and supported note extensions
    private static string? ValidateAndSanitizeFileName(string value) {
        var sanitized = NormalizeBaseName(RemoveInvalidNameCharacters(value));
        if (string.IsNullOrWhiteSpace(sanitized))
            return null;

        var extension = NoteFileExtensions.Supported
            .FirstOrDefault(supportedExtension =>
                sanitized.EndsWith(supportedExtension, StringComparison.OrdinalIgnoreCase));
        if (extension is not null)
            sanitized = sanitized[..^extension.Length].TrimEnd();

        return ValidateBaseName(sanitized);
    }

    // start watching for file/folder changes
    public void StartWatching() {
        var (fileWatcher, directoryWatcher) = CreateWatchers(NotesDirectory);
        ReplaceWatchers(fileWatcher, directoryWatcher);
    }

    private static (FileSystemWatcher FileWatcher, FileSystemWatcher DirectoryWatcher) CreateWatchers(
        string notesDirectory) {
        FileSystemWatcher? fileWatcher = null;
        FileSystemWatcher? directoryWatcher = null;

        try {
            fileWatcher = new FileSystemWatcher(notesDirectory) {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = false
            };
            foreach (var extension in NoteFileExtensions.Supported)
                fileWatcher.Filters.Add($"*{extension}");
            fileWatcher.EnableRaisingEvents = true;
            directoryWatcher = new FileSystemWatcher(notesDirectory) {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            return (fileWatcher, directoryWatcher);
        } catch {
            fileWatcher?.Dispose();
            directoryWatcher?.Dispose();
            throw;
        }
    }

    private void ReplaceWatchers(
        FileSystemWatcher fileWatcher,
        FileSystemWatcher directoryWatcher) {
        fileWatcher.Created += OnFileSystemChanged;
        fileWatcher.Changed += OnFileSystemChanged;
        fileWatcher.Deleted += OnFileSystemChanged;
        fileWatcher.Renamed += OnFileSystemChanged;
        directoryWatcher.Created += OnFileSystemChanged;
        directoryWatcher.Deleted += OnFileSystemChanged;
        directoryWatcher.Renamed += OnFileSystemChanged;

        var previousFileWatcher = _watcher;
        var previousDirectoryWatcher = _directoryWatcher;
        _watcher = fileWatcher;
        _directoryWatcher = directoryWatcher;

        DisposeFileWatcher(previousFileWatcher);
        DisposeDirectoryWatcher(previousDirectoryWatcher);
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) {
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // get all folders in current dir
    public IReadOnlyList<NoteItem> GetFolders() {
        if (!Directory.Exists(CurrentDirectory)) CurrentDirectory = NotesDirectory;
        return Directory.GetDirectories(CurrentDirectory)
            .Where(path => !PathsEqual(path, ArchivedNotesDirectory))
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

    // get supported notes in a subfolder, don't change nav state (Expand mode)
    public IReadOnlyList<NoteItem> GetNotesInSubfolder(string subfolderName) {
        if (!TryResolveChildPath(
                NotesDirectory,
                NotesDirectory,
                subfolderName,
                out var subfolderPath))
            return Array.Empty<NoteItem>();
        if (!Directory.Exists(subfolderPath))
            return Array.Empty<NoteItem>();
        if (IsPathWithinArchiveDirectory(subfolderPath))
            return Array.Empty<NoteItem>();
        return EnumerateNoteFiles(subfolderPath)
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

    private static IEnumerable<string> EnumerateNoteFiles(
        string directory,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateFiles(directory, "*", searchOption)
            .Where(NoteFileExtensions.IsSupported);
    }

    // go into a folder (blocks path traversal)
    public void NavigateTo(string folderName) {
        if (!TryResolveChildPath(
                NotesDirectory,
                CurrentDirectory,
                folderName,
                out var target))
            return;
        if (IsPathWithinArchiveDirectory(target))
            return;
        NavigateToDirectory(target, clearForwardHistory: true);
    }

    // go up one folder (blocks escape)
    public void NavigateUp() {
        if (!CanNavigateUp) return;
        var parent = Path.GetDirectoryName(CurrentDirectory);
        var target = parent is not null &&
                     TryNormalizeContainedPath(NotesDirectory, parent, out var normalizedParent)
            ? normalizedParent
            : NotesDirectory;
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
        var sanitized = ValidateBaseName(RemoveInvalidNameCharacters(folderName));
        if (string.IsNullOrWhiteSpace(sanitized))
            return (false, "Invalid or reserved folder name.");

        if (!TryResolveChildPath(
                NotesDirectory,
                CurrentDirectory,
                sanitized,
                out var target))
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
    public (bool Success, string? NewFolderName, string? Error) RenameFolder(string oldName, string newName) {
        var sanitized = ValidateBaseName(RemoveInvalidNameCharacters(newName));
        if (string.IsNullOrWhiteSpace(sanitized))
            return (false, null, "Invalid or reserved folder name.");
        if (string.Equals(sanitized, oldName, StringComparison.OrdinalIgnoreCase))
            return (true, sanitized, null);

        if (!TryResolveChildPath(NotesDirectory, CurrentDirectory, oldName, out var oldPath) ||
            !TryResolveChildPath(NotesDirectory, CurrentDirectory, sanitized, out var newPath))
            return (false, null, "Access denied.");
        if (!Directory.Exists(oldPath))
            return (false, null, "Folder not found.");
        if (Directory.Exists(newPath))
            return (false, null, "A folder with that name already exists.");

        try {
            Directory.Move(oldPath, newPath);
            return (true, sanitized, null);
        } catch (Exception ex) {
            return (false, null, ex.Message);
        }
    }

    // delete a folder (blocks traversal)
    public (bool Success, string? Error) DeleteFolder(string folderName) {
        if (!TryResolveChildPath(
                NotesDirectory,
                CurrentDirectory,
                folderName,
                out var target))
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

    private static string RemoveInvalidNameCharacters(string value) {
        return Path.GetInvalidFileNameChars().Aggregate(
            value,
            (current, c) => current.Replace(c.ToString(), "")
        );
    }

    private static string? ValidateBaseName(string value) {
        var sanitized = NormalizeBaseName(value);
        if (string.IsNullOrWhiteSpace(sanitized))
            return null;

        var deviceName = sanitized.Split('.', 2)[0].TrimEnd();
        if (ReservedFileNames.Contains(deviceName))
            return null;

        return sanitized;
    }

    private static string NormalizeBaseName(string value) {
        return value.Trim().TrimEnd('.').TrimEnd();
    }

    // get initial notes dir (from settings or default)
    private string ResolveInitialNotesDirectory() {
        var savedDirectory = _settingsService.LoadNotesDirectory();
        if (!string.IsNullOrWhiteSpace(savedDirectory))
            return Path.GetFullPath(savedDirectory);

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Notes");
    }

    private string? ResolveNoteDirectory(string? containingDirectory) {
        return TryNormalizeContainedPath(
                   NotesDirectory,
                   containingDirectory ?? CurrentDirectory,
                   out var directory) &&
               !IsPathWithinArchiveDirectory(directory)
            ? directory
            : null;
    }

    private bool IsPathWithinNotesDirectory(string path) {
        return IsPathWithinDirectory(path, NotesDirectory);
    }

    private bool IsPathWithinArchiveDirectory(string path) {
        return IsPathWithinDirectory(path, ArchivedNotesDirectory);
    }

    private static bool IsPathWithinDirectory(string path, string directory) {
        return TryNormalizeContainedPath(directory, path, out _);
    }

    private static bool PathsEqual(string left, string right) {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveChildPath(
        string rootDirectory,
        string parentDirectory,
        string childPath,
        out string resolvedPath) {
        resolvedPath = string.Empty;
        if (!TryNormalizeContainedPath(rootDirectory, parentDirectory, out var normalizedParent))
            return false;

        try {
            var combinedPath = Path.Combine(normalizedParent, childPath);
            if (!TryNormalizeContainedPath(
                    normalizedParent,
                    combinedPath,
                    out var normalizedChild,
                    allowRoot: false))
                return false;

            return TryNormalizeContainedPath(rootDirectory, normalizedChild, out resolvedPath);
        } catch (Exception ex) when (IsExpectedPathException(ex)) {
            return false;
        }
    }

    private static bool TryNormalizeContainedPath(
        string rootDirectory,
        string pathToCheck,
        out string normalizedPath,
        bool allowRoot = true) {
        normalizedPath = string.Empty;
        try {
            var normalizedRoot = Path.GetFullPath(rootDirectory);
            normalizedPath = Path.GetFullPath(pathToCheck);
            if (PathsEqual(normalizedPath, normalizedRoot))
                return allowRoot;

            var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase);
        } catch (Exception ex) when (IsExpectedPathException(ex)) {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool IsExpectedPathException(Exception exception) {
        return exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            System.Security.SecurityException;
    }

    private void DeleteEmptyArchiveDirectories(string? directory) {
        while (!string.IsNullOrWhiteSpace(directory) &&
               !PathsEqual(directory, ArchivedNotesDirectory) &&
               IsPathWithinArchiveDirectory(directory)) {
            if (!Directory.Exists(directory) ||
                Directory.EnumerateFileSystemEntries(directory).Any()) {
                return;
            }

            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static bool IsExpectedFileOperationException(Exception exception) {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException;
    }

    private void NavigateToDirectory(string target, bool clearForwardHistory) {
        if (!TryNormalizeContainedPath(NotesDirectory, target, out var normalizedTarget) ||
            IsPathWithinArchiveDirectory(normalizedTarget) ||
            !Directory.Exists(normalizedTarget) ||
            PathsEqual(normalizedTarget, CurrentDirectory))
            return;

        _backHistory.Push(CurrentDirectory);
        CurrentDirectory = normalizedTarget;

        if (clearForwardHistory)
            _forwardHistory.Clear();
    }

    private bool TryPopValidHistoryEntry(Stack<string> history, out string target) {
        while (history.Count > 0) {
            var historyPath = history.Pop();
            if (!TryNormalizeContainedPath(NotesDirectory, historyPath, out var fullPath) ||
                IsPathWithinArchiveDirectory(fullPath) ||
                !Directory.Exists(fullPath))
                continue;

            target = fullPath;
            return true;
        }

        target = string.Empty;
        return false;
    }

    private void ResetNavigationHistory() {
        _backHistory.Clear();
        _forwardHistory.Clear();
    }

    private void StopWatching() {
        var fileWatcher = _watcher;
        var directoryWatcher = _directoryWatcher;
        _watcher = null;
        _directoryWatcher = null;

        DisposeFileWatcher(fileWatcher);
        DisposeDirectoryWatcher(directoryWatcher);
    }

    private void DisposeFileWatcher(FileSystemWatcher? watcher) {
        if (watcher is null)
            return;

        watcher.Created -= OnFileSystemChanged;
        watcher.Changed -= OnFileSystemChanged;
        watcher.Deleted -= OnFileSystemChanged;
        watcher.Renamed -= OnFileSystemChanged;
        watcher.Dispose();
    }

    private void DisposeDirectoryWatcher(FileSystemWatcher? watcher) {
        if (watcher is null)
            return;

        watcher.Created -= OnFileSystemChanged;
        watcher.Deleted -= OnFileSystemChanged;
        watcher.Renamed -= OnFileSystemChanged;
        watcher.Dispose();
    }

    public void Dispose() {
        StopWatching();
    }
}
