namespace Noted.Services;

/// <summary>
/// Reports an expected settings storage failure with an actionable message.
/// Startup treats this as fatal. The dispatcher may recover from it at runtime
/// after telling the user that the requested setting was not loaded or saved.
/// </summary>
public sealed class SettingsPersistenceException : Exception
{
    public SettingsPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
