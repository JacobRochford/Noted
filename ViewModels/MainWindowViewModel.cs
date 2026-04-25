using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MyNotes.Models;
using MyNotes.Services;

namespace MyNotes.ViewModels;


public sealed class MainWindowViewModel : IDisposable {
    private readonly NoteFileService _fileService;
    private readonly AppSettingsService _settingsService;
    private DispatcherTimer? _debounceTimer;

    public ObservableCollection<NoteItem> Notes { get; } = new();
    public string HeaderText { get; private set; } = "My Notes";
    public string? SelectedFileName { get; set; }
    public event EventHandler? NotesLoaded;

    public MainWindowViewModel(NoteFileService fileService, AppSettingsService settingsService) {
        _fileService = fileService;
        _settingsService = settingsService;
        _fileService.FilesChanged += OnFilesChanged;
        _fileService.StartWatching();
    }

    

    public void LoadNotes(string? selectedFileName = null) {
        if (selectedFileName != null)
            SelectedFileName = selectedFileName;

        var pinned = new HashSet<string>(_settingsService.LoadPinnedNotes() ?? Array.Empty<string>());
        var items = _fileService.GetNotes();
        Notes.Clear();
        foreach (var item in items)
        {
            item.IsPinned = pinned.Contains(item.FileName);
            Notes.Add(item);
        }

        HeaderText = Notes.Count > 0 ? $"My Notes ({Notes.Count})" : "My Notes";
        ResortNotes();
        NotesLoaded?.Invoke(this, EventArgs.Empty);
    }

    public NoteItem? FindNote(string? fileName) =>
        fileName is null ? null : Notes.FirstOrDefault(n => n.FileName == fileName);

    public void TogglePin(NoteItem note) {
        if (note == null)
            return;
        note.IsPinned = !note.IsPinned;
        // Save pin state persistently
        _settingsService.SavePinnedNotes(Notes.Where(n => n.IsPinned).Select(n => n.FileName));
        ResortNotes();
    }

    private void ResortNotes() {
        var sorted = Notes.OrderByDescending(n => n.IsPinned).ThenBy(n => n.DisplayName).ToList();
        Notes.Clear();
        foreach (var n in sorted)
            Notes.Add(n);
    }

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
