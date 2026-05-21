using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;


public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable {
    private readonly NoteFileService _fileService;
    private readonly AppSettingsService _settingsService;
    private DispatcherTimer? _debounceTimer;
    private string _headerTextEdit = "";
    private bool _isHeaderEditing;
    private bool _showNotesDirectory;
    private bool _showModifiedSubtitle;
    private string _headerText = "Noted.";

    public ObservableCollection<NoteItem> Notes { get; } = new();
    
    public string HeaderText {
        get => _headerText;
        private set {
            if (_headerText != value) {
                _headerText = value;
                OnPropertyChanged();
            }
        }
    }

    public string HeaderTextEdit {
        get => _headerTextEdit;
        set {
            if (_headerTextEdit != value) {
                _headerTextEdit = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsHeaderEditing {
        get => _isHeaderEditing;
        set {
            if (_isHeaderEditing != value) {
                _isHeaderEditing = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ShowNotesDirectory {
        get => _showNotesDirectory;
        set {
            if (_showNotesDirectory != value) {
                _showNotesDirectory = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ShowModifiedSubtitle {
        get => _showModifiedSubtitle;
        set {
            if (_showModifiedSubtitle != value) {
                _showModifiedSubtitle = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SelectedFileName { get; set; }
    public event EventHandler? NotesLoaded;
    public event PropertyChangedEventHandler? PropertyChanged;

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

        HeaderText = Notes.Count > 0 ? $"Noted. ({Notes.Count})" : "Noted.";
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
        _settingsService.SavePinnedNotes(Notes.Where(n => n.IsPinned).Select(n => n.FileName).ToList());
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
