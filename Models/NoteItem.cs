using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyNotes.Models;

public sealed class NoteItem : INotifyPropertyChanged {
    private string _displayName = "";
    private string _editableName = "";
    private bool _isEditing;

    public string FileName { get; init; } = "";
    
    public string DisplayName {
        get => _displayName;
        set {
            if (_displayName != value) {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public string EditableName {
        get => _editableName;
        set {
            if (_editableName != value) {
                _editableName = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsEditing {
        get => _isEditing;
        set {
            if (_isEditing != value) {
                _isEditing = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => DisplayName;
}
