using System.Text.RegularExpressions;

namespace Noted.Models;

public sealed class JotItem : ObservableObject
{
    private string _title = "";
    private string _details = "";
    private string _category = "";
    private bool _isPinned;
    private bool _isStarred;
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _modifiedUtc = DateTimeOffset.UtcNow;
    private bool _isEditing;
    private string _draftTitle = "";
    private string _draftDetails = "";
    private string _draftCategory = "";
    private bool _draftIsPinned;
    private bool _draftIsStarred;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            if (!SetProperty(ref _title, value ?? ""))
                return;

            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string Details
    {
        get => _details;
        set
        {
            if (!SetProperty(ref _details, value ?? ""))
                return;

            OnPropertyChanged(nameof(PreviewText));
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (!SetProperty(ref _category, NormalizeCategory(value)))
                return;

            OnPropertyChanged(nameof(CategoryLabel));
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public bool IsStarred
    {
        get => _isStarred;
        set => SetProperty(ref _isStarred, value);
    }

    public DateTimeOffset CreatedUtc
    {
        get => _createdUtc;
        set
        {
            if (!SetProperty(ref _createdUtc, value))
                return;

            OnPropertyChanged(nameof(CreatedDisplay));
        }
    }

    public DateTimeOffset ModifiedUtc
    {
        get => _modifiedUtc;
        set
        {
            if (!SetProperty(ref _modifiedUtc, value))
                return;

            OnPropertyChanged(nameof(ModifiedDisplay));
            OnPropertyChanged(nameof(ModifiedShortDisplay));
        }
    }

    public bool IsNew { get; set; }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!SetProperty(ref _isEditing, value))
                return;

            OnPropertyChanged(nameof(HasPendingEdit));
            OnPropertyChanged(nameof(CanSaveDraft));
        }
    }

    public string DraftTitle
    {
        get => _draftTitle;
        set
        {
            if (!SetProperty(ref _draftTitle, value ?? ""))
                return;

            NotifyDraftChanged();
        }
    }

    public string DraftDetails
    {
        get => _draftDetails;
        set
        {
            if (!SetProperty(ref _draftDetails, value ?? ""))
                return;

            NotifyDraftChanged();
        }
    }

    public string DraftCategory
    {
        get => _draftCategory;
        set
        {
            if (!SetProperty(ref _draftCategory, value ?? ""))
                return;

            OnPropertyChanged(nameof(HasPendingEdit));
        }
    }

    public bool DraftIsPinned
    {
        get => _draftIsPinned;
        set
        {
            if (!SetProperty(ref _draftIsPinned, value))
                return;

            OnPropertyChanged(nameof(HasPendingEdit));
        }
    }

    public bool DraftIsStarred
    {
        get => _draftIsStarred;
        set
        {
            if (!SetProperty(ref _draftIsStarred, value))
                return;

            OnPropertyChanged(nameof(HasPendingEdit));
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
    public string PreviewText => CreatePreview(Details);
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? "Inbox" : Category;
    public string CreatedDisplay => CreatedUtc.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
    public string ModifiedDisplay => ModifiedUtc.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
    public string ModifiedShortDisplay => ModifiedUtc.ToLocalTime().ToString("MMM d");

    public bool CanSaveDraft => IsEditing &&
        (!string.IsNullOrWhiteSpace(DraftTitle) || !string.IsNullOrWhiteSpace(DraftDetails));

    public bool HasDraftContent =>
        !string.IsNullOrWhiteSpace(DraftTitle) ||
        !string.IsNullOrWhiteSpace(DraftDetails) ||
        !string.IsNullOrWhiteSpace(DraftCategory) ||
        DraftIsPinned ||
        DraftIsStarred;

    public bool HasPendingEdit => IsEditing &&
        (IsNew ||
         DraftTitle != Title ||
         DraftDetails != Details ||
         NormalizeCategory(DraftCategory) != Category ||
         DraftIsPinned != IsPinned ||
         DraftIsStarred != IsStarred);

    public void BeginEdit()
    {
        if (IsEditing)
            return;

        DraftTitle = Title;
        DraftDetails = Details;
        DraftCategory = Category;
        DraftIsPinned = IsPinned;
        DraftIsStarred = IsStarred;
        IsEditing = true;
    }

    public void SaveEdit(DateTimeOffset modifiedUtc)
    {
        if (!IsEditing)
            return;

        Title = CreateTitle(DraftTitle, DraftDetails);
        Details = DraftDetails;
        Category = DraftCategory;
        IsPinned = DraftIsPinned;
        IsStarred = DraftIsStarred;
        ModifiedUtc = modifiedUtc;
        IsNew = false;
        IsEditing = false;
    }

    public void CancelEdit()
    {
        DraftTitle = Title;
        DraftDetails = Details;
        DraftCategory = Category;
        DraftIsPinned = IsPinned;
        DraftIsStarred = IsStarred;
        IsEditing = false;
    }

    public void TogglePinned(DateTimeOffset modifiedUtc)
    {
        IsPinned = !IsPinned;
        ModifiedUtc = modifiedUtc;
    }

    public void ToggleStarred(DateTimeOffset modifiedUtc)
    {
        IsStarred = !IsStarred;
        ModifiedUtc = modifiedUtc;
    }

    private void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(HasPendingEdit));
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(HasDraftContent));
    }

    private static string NormalizeCategory(string? value) => value?.Trim() ?? "";

    private static string CreateTitle(string title, string details)
    {
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length > 0)
            return normalizedTitle;

        foreach (var rawLine in details.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            line = Regex.Replace(line, @"^(#{1,6}\s*|[-*+]\s+|>\s*)", "").Trim();
            if (line.Length == 0)
                continue;

            return line.Length <= 72 ? line : $"{line[..69]}...";
        }

        return "Untitled";
    }

    private static string CreatePreview(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return "No details yet";

        var preview = Regex.Replace(details, @"\s+", " ").Trim();
        return preview.Length <= 140 ? preview : $"{preview[..137]}...";
    }
}
