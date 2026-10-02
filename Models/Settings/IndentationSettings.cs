namespace Noted.Models;

public enum IndentationMode
{
    Spaces,
    Tabs
}

public sealed record IndentationSettings
{
    public const int DefaultTabSize = 4;
    public const int MaximumTabSize = 32;
    private IndentationMode _mode;
    private int _tabSize = DefaultTabSize;

    public IndentationMode Mode
    {
        get => _mode;
        init => _mode = Enum.IsDefined(value) ? value : IndentationMode.Spaces;
    }

    public int TabSize
    {
        get => _tabSize;
        init => _tabSize = value is >= 1 and <= MaximumTabSize ? value : DefaultTabSize;
    }
}
