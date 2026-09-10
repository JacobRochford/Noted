using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public sealed class DictionaryItem : INotifyPropertyChanged
{
    private string _word = "";
    private string _definition = "";

    public string Word
    {
        get => _word;
        set
        {
            if (_word != value)
            {
                _word = value;
                OnPropertyChanged();
            }
        }
    }

    public string Definition
    {
        get => _definition;
        set
        {
            if (_definition != value)
            {
                _definition = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isEditing;
    private bool _isExpanded;
    private string _draftWord = "";
    private string _draftDefinition = "";
    public bool IsNew { get; set; }
    public bool IsEditing { get => _isEditing; private set { _isEditing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowEditActions)); } }
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }
    public string DraftWord { get => _draftWord; set { _draftWord = value; OnPropertyChanged(); if (IsAutoSaving) Word = value; } }
    public string DraftDefinition { get => _draftDefinition; set { _draftDefinition = value; OnPropertyChanged(); if (IsAutoSaving) Definition = value; } }
    public bool IsAutoSaving { get; private set; }
    public bool ShowEditActions => IsEditing && !IsAutoSaving;
    public bool HasPendingEdit => IsEditing && !IsAutoSaving && (IsNew || DraftWord != Word || DraftDefinition != Definition);

    public void BeginEdit(bool autoSave = false)
    {
        if (IsEditing) return;
        DraftWord = Word;
        DraftDefinition = Definition;
        IsExpanded = true;
        IsAutoSaving = autoSave;
        IsEditing = true;
    }

    public void FinishAutoSave()
    {
        if (!IsAutoSaving) return;
        IsAutoSaving = false;
        IsEditing = false;
    }

    public void SaveEdit()
    {
        // Publish both values together so filtering and persistence see a complete entry.
        _word = DraftWord;
        _definition = DraftDefinition;
        IsNew = false;
        IsEditing = false;
        OnPropertyChanged(nameof(Word));
        OnPropertyChanged(nameof(Definition));
    }

    public void CancelEdit()
    {
        DraftWord = Word;
        DraftDefinition = Definition;
        IsEditing = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
