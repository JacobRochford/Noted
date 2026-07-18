using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;


public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable {
    private const string DefaultHeaderText = "Noted.";
    private bool _isSettingsVisible;
    private readonly INoteFileService _fileService;
    private readonly IAppSettingsService _settingsService;
    private readonly Action<Action> _uiThreadInvoke;
    private DispatcherTimer? _debounceTimer;
    private string _headerTextEdit = "";
    private bool _isHeaderEditing;
    private bool _showNotesDirectory;
    private bool _showModifiedSubtitle;
    private string _filterText = "";
    private string _headerText = DefaultHeaderText;
    private string _customHeaderText = DefaultHeaderText;
    private FolderNavigationMode _folderNavigationMode;
    private readonly HashSet<string> _expandedFolders = new();
    private HashSet<string> _pinnedNoteKeys = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<NoteItem> Notes { get; } = new();
    
    public string HeaderText {
        get => _headerText;
        private set {
            if (_headerText != value) {
                _headerText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayHeaderText));
            }
        }
    }

    public bool IsSettingsVisible {
        get => _isSettingsVisible;
        set {
            if (_isSettingsVisible != value) {
                _isSettingsVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayHeaderText));
            }
        }
    }

    public string DisplayHeaderText => IsSettingsVisible ? "⚙" : HeaderText;

    public string EditableHeaderText => _customHeaderText;

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

    public string FilterText {
        get => _filterText;
        set {
            if (_filterText == value) return;
            _filterText = value;
            OnPropertyChanged();
            CollectionViewSource.GetDefaultView(Notes).Refresh();
            OnPropertyChanged(nameof(HasNoFilterResults));
            OnPropertyChanged(nameof(HasNoNotes));
        }
    }

    public void ClearFilter() => FilterText = "";

    // no notes in current folder and no search active
    public bool HasNoNotes => !Notes.Any() && string.IsNullOrWhiteSpace(_filterText);

    // search active but no results
    public bool HasNoFilterResults {
        get {
            if (string.IsNullOrWhiteSpace(_filterText)) return false;
            return !CollectionViewSource.GetDefaultView(Notes).OfType<NoteItem>().Any();
        }
    }

    public bool CanNavigateUp => _fileService.CanNavigateUp;
    public bool CanNavigateBack => _fileService.CanNavigateBack;
    public bool CanNavigateForward => _fileService.CanNavigateForward;
    public string CurrentFolderName => _fileService.CurrentFolderName;

    public FolderNavigationMode FolderNavigationMode {
        get => _folderNavigationMode;
        set {
            if (_folderNavigationMode != value) {
                _folderNavigationMode = value;
                _expandedFolders.Clear();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsExpandMode));
                LoadNotes();
            }
        }
    }

    // true if folder is expanded in Expand mode, or if we're in Drill-down mode (where folders are always "expanded")
    public bool IsExpandMode => _folderNavigationMode == FolderNavigationMode.Expand;

    public string? SelectedNoteKey { get; set; }
    public event EventHandler? NotesLoaded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel(INoteFileService fileService, IAppSettingsService settingsService, Action<Action> uiThreadInvoke) {
        _fileService = fileService;
        _settingsService = settingsService;
        _uiThreadInvoke = uiThreadInvoke;
        _customHeaderText = NormalizeHeaderText(settingsService.LoadCustomHeader());
        HeaderText = _customHeaderText;
        _folderNavigationMode = settingsService.LoadFolderNavigationMode();
        _fileService.FilesChanged += OnFilesChanged;
        _fileService.StartWatching();
        // set up filtering for search
        CollectionViewSource.GetDefaultView(Notes).Filter = FilterNote;
    }

    

    // Loads notes and folders for the current view
    public void LoadNotes(string? selectedNoteKey = null) {
        if (selectedNoteKey != null)
            SelectedNoteKey = selectedNoteKey;

        _pinnedNoteKeys = LoadPinnedNoteKeys();
        Notes.Clear();

        // Use Expand mode only at root
        if (_folderNavigationMode == FolderNavigationMode.Expand && !_fileService.CanNavigateUp)
            LoadNotesExpanded(_pinnedNoteKeys);
        else
            LoadNotesDrillDown(_pinnedNoteKeys);

        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(CurrentFolderName));
        OnPropertyChanged(nameof(HasNoNotes));
        OnPropertyChanged(nameof(HasNoFilterResults));
        NotesLoaded?.Invoke(this, EventArgs.Empty);
    }

    // Drill-down mode: show folders and notes in current folder
    private void LoadNotesDrillDown(HashSet<string> pinned) {
        var folders = _fileService.GetFolders();
        var items = _fileService.GetNotes();
        foreach (var folder in folders)
            Notes.Add(folder);
        foreach (var item in items) {
            item.IsPinned = pinned.Contains(item.NoteKey);
            Notes.Add(item);
        }
        HeaderText = BuildHeaderText(items.Count);
        ResortNotes();
    }

    // Expand mode: show all folders, expanded children, and root notes
    private void LoadNotesExpanded(HashSet<string> pinned) {
        var folders = _fileService.GetFolders();
        var rootNotes = _fileService.GetNotes();
        foreach (var n in rootNotes)
            n.IsPinned = pinned.Contains(n.NoteKey);

        int childCount = 0;
        // Folders sorted by name, each optionally followed by their expanded children
        foreach (var folder in folders.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)) {
            folder.IsExpanded = _expandedFolders.Contains(folder.FileName);
            Notes.Add(folder);
            if (folder.IsExpanded) {
                var children = _fileService.GetNotesInSubfolder(folder.FileName);
                childCount += children.Count;
                foreach (var child in children) {
                    child.IsPinned = pinned.Contains(child.NoteKey);
                    child.IndentLevel = 1;
                    Notes.Add(child);
                }
            }
        }

        // Root notes: pinned files (last modified desc), then other files (last modified desc)
        var pinnedFiles = rootNotes.Where(n => n.IsPinned && !n.IsFolder)
                                  .OrderByDescending(n => n.LastModified);
        var otherFiles = rootNotes.Where(n => !n.IsPinned && !n.IsFolder)
                                  .OrderByDescending(n => n.LastModified);
        foreach (var note in pinnedFiles)
            Notes.Add(note);
        foreach (var note in otherFiles)
            Notes.Add(note);

        int total = rootNotes.Count + childCount;
        HeaderText = BuildHeaderText(total);
        // No ResortNotes() here: folder/child order must be preserved
    }

    public void SetCustomHeader(string? customHeader) {
        _customHeaderText = NormalizeHeaderText(customHeader);
        OnPropertyChanged(nameof(EditableHeaderText));
        HeaderText = BuildHeaderText(Notes.Count(note => !note.IsFolder));
    }

    public NoteItem? FindNote(string? noteKey) =>
        noteKey is null
            ? null
            : Notes.FirstOrDefault(n =>
                !n.IsFolder && string.Equals(n.NoteKey, noteKey, StringComparison.OrdinalIgnoreCase));

    // Toggle pin state for a note
    public void TogglePin(NoteItem note) {
        if (note == null || note.IsFolder || string.IsNullOrWhiteSpace(note.NoteKey))
            return;

        note.IsPinned = !note.IsPinned;
        if (note.IsPinned)
            _pinnedNoteKeys.Add(note.NoteKey);
        else
            _pinnedNoteKeys.Remove(note.NoteKey);

        SavePinnedNoteKeys();
        if (_folderNavigationMode == FolderNavigationMode.Expand && !_fileService.CanNavigateUp)
            LoadNotes();
        else
            ResortNotes();
    }

    public void ReplacePinnedNoteKey(string oldNoteKey, string newNoteKey) {
        if (string.IsNullOrWhiteSpace(oldNoteKey)
            || string.IsNullOrWhiteSpace(newNoteKey)
            || !_pinnedNoteKeys.Remove(oldNoteKey))
            return;

        _pinnedNoteKeys.Add(newNoteKey);
        SavePinnedNoteKeys();
    }

    private HashSet<string> LoadPinnedNoteKeys() {
        var storedKeys = _settingsService.LoadPinnedNotes() ?? Array.Empty<string>();
        var resolvedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legacyKeys = new List<string>();

        foreach (var storedKey in storedKeys) {
            if (string.IsNullOrWhiteSpace(storedKey))
                continue;

            var normalized = storedKey.Trim().Replace('\\', '/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
                resolvedKeys.Add(normalized);
            else
                legacyKeys.Add(normalized);
        }

        if (legacyKeys.Count == 0)
            return resolvedKeys;

        var allNoteKeys = _fileService.GetAllNoteKeys();
        foreach (var legacyKey in legacyKeys) {
            var legacyPath = legacyKey.TrimStart('/');
            if (legacyPath.Contains('/')) {
                var exactKey = $"./{legacyPath}";
                var exactMatch = allNoteKeys.FirstOrDefault(key =>
                    string.Equals(key, exactKey, StringComparison.OrdinalIgnoreCase));
                if (exactMatch is not null)
                    resolvedKeys.Add(exactMatch);
                continue;
            }

            var matches = allNoteKeys
                .Where(key => string.Equals(GetNoteKeyFileName(key), legacyPath, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 1)
                resolvedKeys.Add(matches[0]);
        }

        _settingsService.SavePinnedNotes(
            resolvedKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList());
        return resolvedKeys;
    }

    private void SavePinnedNoteKeys() {
        _settingsService.SavePinnedNotes(
            _pinnedNoteKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string GetNoteKeyFileName(string noteKey) {
        var separatorIndex = noteKey.LastIndexOf('/');
        return separatorIndex >= 0 ? noteKey[(separatorIndex + 1)..] : noteKey;
    }

    // Resort notes in-place: folders, then pinned, then alpha
    private void ResortNotes() {
        var sorted = Notes
            .OrderByDescending(n => n.IsPinned && !n.IsFolder) // pinned files first
            .ThenBy(n => n.IsFolder ? 0 : 1) // folders next
            .ThenBy(n => n.IsFolder ? n.DisplayName : null, StringComparer.OrdinalIgnoreCase) // folders alpha
            .ThenByDescending(n => !n.IsFolder && !n.IsPinned ? n.LastModified : DateTime.MinValue) // files by last modified desc
            .ToList();
        for (int i = 0; i < sorted.Count; i++) {
            int current = Notes.IndexOf(sorted[i]);
            if (current != i)
                Notes.Move(current, i);
        }
    }

    private string BuildHeaderText(int noteCount) {
        return noteCount > 0 ? $"{_customHeaderText} ({noteCount})" : _customHeaderText;
    }

    private static string NormalizeHeaderText(string? headerText) {
        return string.IsNullOrWhiteSpace(headerText) ? DefaultHeaderText : headerText.Trim();
    }

    // File system changed - refresh notes on UI thread
    private void OnFilesChanged(object? sender, EventArgs e) {
        _uiThreadInvoke(DebounceRefresh);
    }

    public void NavigateTo(string folderName) {
        _fileService.NavigateTo(folderName);
        ClearFilter();
        LoadNotes();
    }

    public void ToggleFolderExpansion(NoteItem folder) {
        if (!folder.IsFolder) return;
        if (_expandedFolders.Contains(folder.FileName))
            _expandedFolders.Remove(folder.FileName);
        else
            _expandedFolders.Add(folder.FileName);
        LoadNotes();
    }

    public void NavigateUp() {
        _fileService.NavigateUp();
        ClearFilter();
        LoadNotes();
    }

    public void NavigateBack() {
        _fileService.NavigateBack();
        ClearFilter();
        LoadNotes();
    }

    public void NavigateForward() {
        _fileService.NavigateForward();
        ClearFilter();
        LoadNotes();
    }

    // Debounce file system events to avoid redundant reloads
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

    private bool FilterNote(object obj) {
        if (string.IsNullOrWhiteSpace(_filterText))
            return true;
        return obj is NoteItem note
            && (note.DisplayName.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                || note.EditableName.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
    }

    // cleanup resources
    public void Dispose() {
        _debounceTimer?.Stop();
        _debounceTimer = null;
        _fileService.FilesChanged -= OnFilesChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
