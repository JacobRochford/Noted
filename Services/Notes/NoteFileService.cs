using System.Globalization;
using System.IO;
using Noted.Helpers;
using Noted.Models;

namespace Noted.Services;

// handles note and folder file operations
public sealed class NoteFileService : INoteFileService {
    private const string DeletedNoteTimestampFormat = "yyyy-MM-dd_HH-mm-ss_fff";
    private readonly IAppSettingsService _settingsService;
    private readonly NoteBrowserNavigation _navigation;
    private readonly NoteDirectoryWatcher _directoryWatcher = new();
    private static readonly TimeSpan s_deletedNoteRetention = TimeSpan.FromDays(14); // how long to keep deleted notes
    private static readonly HashSet<string> s_reservedFileNames = new(StringComparer.OrdinalIgnoreCase) {
        // windows reserved names
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string NotesDirectory => _navigation.RootDirectory;
    public string CurrentDirectory => _navigation.CurrentDirectory;
    public string CurrentFolderName => _navigation.CurrentFolderName;
    public bool CanNavigateUp => _navigation.CanNavigateUp;
    public bool CanNavigateBack => _navigation.CanNavigateBack;
    public bool CanNavigateForward => _navigation.CanNavigateForward;
    public string DeletedNotesDirectory { get; }
    public string ArchivedNotesDirectory => Path.Combine(NotesDirectory, ".archive");

    public event EventHandler? FilesChanged;

    public NoteFileService(IAppSettingsService settingsService) {
        _settingsService = settingsService;
        DeletedNotesDirectory = Path.Combine(_settingsService.AppDataDirectory, "DeletedNotes");
        _navigation = new NoteBrowserNavigation(ResolveInitialNotesDirectory());
        _directoryWatcher.Changed += OnFilesChanged;
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(DeletedNotesDirectory);
        PurgeExpiredDeletedNotes(); // clean up old deleted notes
    }

    public IReadOnlyList<NoteItem> GetNotes() {
        // get all supported notes in current dir
        _navigation.EnsureCurrentDirectoryExists();
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

    public bool TryGetNoteKey(string filePath, out string noteKey)
    {
        noteKey = string.Empty;
        if (!TryNormalizeContainedPath(NotesDirectory, filePath, out var fullPath, allowRoot: false))
            return false;

        noteKey = GetNoteKey(fullPath);
        return true;
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

    public string SuggestNoteName() => Path.GetFileNameWithoutExtension(BuildUniqueFileName(DateTime.Now));

    public (CreatedNote? Note, string? Error) CreateNote(string? requestedName = null) {
        // create new note, optionally with user-supplied name
        var now = DateTime.Now;
        var usesGeneratedName = string.IsNullOrWhiteSpace(requestedName);
        var (filename, error) = usesGeneratedName
            ? (BuildUniqueFileName(now), (string?)null)
            : BuildRequestedFileName(requestedName!);
        if (filename is null)
            return (null, error);
        var fullPath = Path.Combine(CurrentDirectory, filename);
        var content = BuildNewNoteContent(
            now,
            _settingsService.LoadTimestampPlacement(),
            _settingsService.LoadTimestampLine());
        FileWriter.WriteAllText(fullPath, content);
        if (!string.Equals(File.ReadAllText(fullPath), content, StringComparison.Ordinal))
            throw new IOException("The new note was created but could not be verified.");
        return (new CreatedNote(filename, usesGeneratedName, content), null);
    }

    public bool ChangeNotesDirectory(string newDirectory) {
        // switch to a new notes dir
        var normalizedPath = Path.GetFullPath(newDirectory);
        if (string.Equals(NotesDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var wasWatching = _directoryWatcher.IsWatching;
        NoteDirectoryWatcher.PreparedWatchers? replacementWatchers = null;

        try {
            if (wasWatching)
                replacementWatchers = _directoryWatcher.PrepareReplacement(normalizedPath);

            // Persist before exposing the new root to callers. If this fails,
            // the current root, navigation state, and existing watchers remain intact.
            _settingsService.SaveNotesDirectory(normalizedPath);
        } catch {
            replacementWatchers?.Dispose();
            throw;
        }

        _navigation.ChangeRoot(normalizedPath);

        if (replacementWatchers is not null) {
            using (replacementWatchers)
                _directoryWatcher.ReplaceWith(replacementWatchers);
        }

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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, "The note could not be archived. Check folder access and whether the file is in use.");
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
        return (true, null);
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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, "The archived note could not be restored. Check folder access and whether the file is in use.");
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
        return (true, null);
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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, null, "The note could not be renamed. Check folder access and whether the file is in use.");
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

    // build note content with the configured timestamp placement
    private static string BuildNewNoteContent(
        DateTime timestamp,
        NoteTimestampPlacement timestampPlacement,
        int timestampLine) {
        var timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return timestampPlacement switch {
            NoteTimestampPlacement.Top => timestampText + Environment.NewLine + Environment.NewLine + Environment.NewLine,
            NoteTimestampPlacement.Bottom => string.Join(Environment.NewLine, Enumerable.Repeat(string.Empty, 30)) + Environment.NewLine + timestampText,
            NoteTimestampPlacement.Line =>
                string.Join(Environment.NewLine, Enumerable.Repeat(string.Empty, Math.Max(0, timestampLine - 1))) +
                (timestampLine > 1 ? Environment.NewLine : string.Empty) +
                timestampText,
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

        var cutoff = DateTime.Now - s_deletedNoteRetention;
        foreach (var path in EnumerateNoteFiles(DeletedNotesDirectory)) {
            try {
                if (TryGetDeletionTime(path, out var deletedAt) && deletedAt < cutoff)
                    File.Delete(path);
            } catch (Exception ex) when (FileSystemErrors.IsExpected(ex)) {
                ExceptionDiagnostics.Record(ex);
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

    private (string? FileName, string? Error) BuildRequestedFileName(string requestedName) {
        var validatedFileName = ValidateAndSanitizeFileName(requestedName);
        if (string.IsNullOrWhiteSpace(validatedFileName))
            return (null, "Invalid or reserved file name.");

        var fileName = validatedFileName + ".txt";
        if (File.Exists(Path.Combine(CurrentDirectory, fileName)))
            return (null, "A file with that name already exists.");

        return (fileName, null);
    }

    // remove invalid chars, reserved names, and supported note extensions
    private static string? ValidateAndSanitizeFileName(string value) {
        var sanitized = NormalizeBaseName(RemoveInvalidNameCharacters(value));
        if (string.IsNullOrWhiteSpace(sanitized))
            return null;

        var extension = NoteFileExtensions.SupportedExtensions
            .FirstOrDefault(supportedExtension =>
                sanitized.EndsWith(supportedExtension, StringComparison.OrdinalIgnoreCase));
        if (extension is not null)
            sanitized = sanitized[..^extension.Length].TrimEnd();

        return ValidateBaseName(sanitized);
    }

    // start watching for file/folder changes
    public void StartWatching() => _directoryWatcher.Start(NotesDirectory);

    private void OnFilesChanged(object? sender, EventArgs e) {
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // get all folders in current dir
    public IReadOnlyList<NoteItem> GetFolders() {
        _navigation.EnsureCurrentDirectoryExists();
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
    public void NavigateTo(string folderName) => _navigation.NavigateTo(folderName);

    // go up one folder (blocks escape)
    public void NavigateUp() => _navigation.NavigateUp();

    public void NavigateBack() => _navigation.NavigateBack();

    public void NavigateForward() => _navigation.NavigateForward();

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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, "The folder could not be created. Check folder access and whether the name is already in use.");
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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, null, "The folder could not be renamed. Check folder access and whether it is in use.");
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
        } catch (Exception ex) when (IsExpectedFileOperationException(ex)) {
            ExceptionDiagnostics.Record(ex);
            return (false, "The folder could not be deleted. Check folder access and whether its files are in use.");
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
        if (s_reservedFileNames.Contains(deviceName))
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

    private static bool IsExpectedFileOperationException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

    public void Dispose() {
        _directoryWatcher.Changed -= OnFilesChanged;
        _directoryWatcher.Dispose();
    }
}
