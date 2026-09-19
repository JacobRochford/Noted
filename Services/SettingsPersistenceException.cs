namespace Noted.Services;

/// <summary>
/// Thrown when application settings cannot be loaded or saved and provides a user-friendly error message
/// </summary>
public sealed class SettingsPersistenceException : Exception
{
    public SettingsPersistenceException(string message)
        : base(message)
    {
    }

    public SettingsPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
