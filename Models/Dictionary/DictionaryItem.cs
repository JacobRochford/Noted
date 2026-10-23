namespace Noted.Models;

public sealed class DictionaryItem : ObservableObject
{
    private string _word = "";
    private string _definition = "";
    private bool _isEditing;
    private bool _isExpanded;
    private string _draftWord = "";
    private string _draftDefinition = "";

    public string Word
    {
        get => _word;
        set => SetProperty(ref _word, value);
    }

    public string Definition
    {
        get => _definition;
        set => SetProperty(ref _definition, value);
    }

    public bool IsNew { get; set; }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(HasPendingEdit));
                OnPropertyChanged(nameof(ShowEditActions));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string DraftWord
    {
        get => _draftWord;
        set
        {
            if (!SetProperty(ref _draftWord, value))
                return;

            if (IsAutoSaving)
                Word = value;
            OnPropertyChanged(nameof(HasPendingEdit));
        }
    }

    public string DraftDefinition
    {
        get => _draftDefinition;
        set
        {
            if (!SetProperty(ref _draftDefinition, value))
                return;

            if (IsAutoSaving)
                Definition = value;
            OnPropertyChanged(nameof(HasPendingEdit));
        }
    }

    public bool IsAutoSaving { get; private set; }
    public bool ShowEditActions => IsEditing && !IsAutoSaving;
    public bool HasPendingEdit => IsEditing && !IsAutoSaving &&
        (IsNew || DraftWord != Word || DraftDefinition != Definition);

    public void BeginEdit(bool autoSave = false)
    {
        if (IsEditing)
            return;

        DraftWord = Word;
        DraftDefinition = Definition;
        IsExpanded = true;
        IsAutoSaving = autoSave;
        IsEditing = true;
    }

    public void FinishAutoSave()
    {
        if (!IsAutoSaving)
            return;

        IsAutoSaving = false;
        IsEditing = false;
    }

    public void SaveEdit() => SaveEdit(DraftWord, DraftDefinition);

    internal void SaveEdit(string word, string definition)
    {
        // Publish both values together so filtering and persistence see a complete entry.
        _word = word;
        _definition = definition;
        IsNew = false;
        IsEditing = DraftWord != word || DraftDefinition != definition;
        OnPropertyChanged(nameof(Word));
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(HasPendingEdit));
    }

    public void CancelEdit()
    {
        DraftWord = Word;
        DraftDefinition = Definition;
        IsEditing = false;
    }
}
