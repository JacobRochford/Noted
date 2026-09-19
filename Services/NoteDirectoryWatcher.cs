using System.IO;

namespace Noted.Services;

internal sealed class NoteDirectoryWatcher : IDisposable
{
    private FileSystemWatcher? _fileWatcher;
    private FileSystemWatcher? _directoryWatcher;

    public bool IsWatching => _fileWatcher is not null;

    public event EventHandler? Changed;

    public void Start(string notesDirectory)
    {
        using var replacement = PrepareReplacement(notesDirectory);
        ReplaceWith(replacement);
    }

    public PreparedWatchers PrepareReplacement(string notesDirectory) => new(notesDirectory);

    public void ReplaceWith(PreparedWatchers replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var (fileWatcher, directoryWatcher) = replacement.Take();

        fileWatcher.Created += OnFileSystemChanged;
        fileWatcher.Changed += OnFileSystemChanged;
        fileWatcher.Deleted += OnFileSystemChanged;
        fileWatcher.Renamed += OnFileSystemChanged;
        directoryWatcher.Created += OnFileSystemChanged;
        directoryWatcher.Deleted += OnFileSystemChanged;
        directoryWatcher.Renamed += OnFileSystemChanged;

        var previousFileWatcher = _fileWatcher;
        var previousDirectoryWatcher = _directoryWatcher;
        _fileWatcher = fileWatcher;
        _directoryWatcher = directoryWatcher;

        DisposeFileWatcher(previousFileWatcher);
        DisposeDirectoryWatcher(previousDirectoryWatcher);
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);

    private void Stop()
    {
        var fileWatcher = _fileWatcher;
        var directoryWatcher = _directoryWatcher;
        _fileWatcher = null;
        _directoryWatcher = null;

        DisposeFileWatcher(fileWatcher);
        DisposeDirectoryWatcher(directoryWatcher);
    }

    private void DisposeFileWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;

        watcher.Created -= OnFileSystemChanged;
        watcher.Changed -= OnFileSystemChanged;
        watcher.Deleted -= OnFileSystemChanged;
        watcher.Renamed -= OnFileSystemChanged;
        watcher.Dispose();
    }

    private void DisposeDirectoryWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;

        watcher.Created -= OnFileSystemChanged;
        watcher.Deleted -= OnFileSystemChanged;
        watcher.Renamed -= OnFileSystemChanged;
        watcher.Dispose();
    }

    public void Dispose() => Stop();

    internal sealed class PreparedWatchers : IDisposable
    {
        private FileSystemWatcher? _fileWatcher;
        private FileSystemWatcher? _directoryWatcher;

        public PreparedWatchers(string notesDirectory)
        {
            try
            {
                _fileWatcher = new FileSystemWatcher(notesDirectory)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = false
                };
                foreach (var extension in NoteFileExtensions.SupportedExtensions)
                    _fileWatcher.Filters.Add($"*{extension}");
                _fileWatcher.EnableRaisingEvents = true;

                _directoryWatcher = new FileSystemWatcher(notesDirectory)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal (FileSystemWatcher FileWatcher, FileSystemWatcher DirectoryWatcher) Take()
        {
            if (_fileWatcher is null || _directoryWatcher is null)
                throw new InvalidOperationException("The prepared watchers have already been used.");

            var watchers = (_fileWatcher, _directoryWatcher);
            _fileWatcher = null;
            _directoryWatcher = null;
            return watchers;
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
            _directoryWatcher?.Dispose();
            _fileWatcher = null;
            _directoryWatcher = null;
        }
    }
}
