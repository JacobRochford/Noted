using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MyNotes.Models;
using MyNotes.Services;

namespace MyNotes.ViewModels;

public sealed class MainWindowViewModel : IDisposable {
    private readonly NoteFileService _fileService;
    private DispatcherTimer? _debounceTimer;

    public ObservableCollection<NoteItem> Notes { get; } = new();
    public string HeaderText { get; private set; } = "My Notes";

    /// <summary>Tracks the file name that should be selected after a reload.</summary>
    public string? SelectedFileName { get; set; }

    /// <summary>Fired on the UI thread after <see cref="Notes"/> has been repopulated.</summary>
    public event EventHandler? NotesLoaded;

    public MainWindowViewModel(NoteFileService fileService) {
        _fileService = fileService;
        _fileService.FilesChanged += OnFilesChanged;
        _fileService.StartWatching();
    }

    /// <summary>
    /// Reloads <see cref="Notes"/> from disk. If <paramref name="selectedFileName"/> is
    /// provided it is stored in <see cref="SelectedFileName"/> before the reload.
    /// </summary>
    public void LoadNotes(string? selectedFileName = null) {
        if (selectedFileName != null)
            SelectedFileName = selectedFileName;

        var items = _fileService.GetNotes();
        Notes.Clear();
        foreach (var item in items)
            Notes.Add(item);

        HeaderText = Notes.Count > 0 ? $"My Notes ({Notes.Count})" : "My Notes";
        NotesLoaded?.Invoke(this, EventArgs.Empty);
    }

    public NoteItem? FindNote(string? fileName) =>
        fileName is null ? null : Notes.FirstOrDefault(n => n.FileName == fileName);

    // Called from background FileSystemWatcher thread — marshal to UI thread.
    private void OnFilesChanged(object? sender, EventArgs e) {
        Application.Current.Dispatcher.Invoke(DebounceRefresh);
    }

    private void DebounceRefresh() {
        if (_debounceTimer == null) {
            _debounceTimer = new DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _debounceTimer.Tick += (_, _) => {
                _debounceTimer.Stop();
                LoadNotes();
            };
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Dispose() {
        _debounceTimer?.Stop();
        _debounceTimer = null;
        _fileService.FilesChanged -= OnFilesChanged;
    }
}
