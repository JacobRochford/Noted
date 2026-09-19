namespace Noted.Services;

// Keep user-facing settings failure messages together; technical causes stay in InnerException.
internal static class SettingsPersistenceMessages
{
    public static string StorageDirectoryUnavailable(string path)
        => $"Noted could not initialize its application data folder at '{path}'. "
                + "Check that the location exists and that you have permission to write to it, then restart Noted.";

    public static string SettingsFileUnreadable(string path)
        => $"Could not read the settings file '{path}'. Check file permissions and whether another program is using the file, then try again. Existing settings and recovery files have been preserved.";

    public static string RecoveryFileUnreadable(string path)
        => $"Noted could not inspect the settings recovery file '{path}'. No recovery files were changed. Check file permissions and whether another program is using the file, then try again.";

    public const string MissingSettingsWithoutRecovery = "Noted could not restore missing settings because no valid recovery copy was available. Existing recovery files were not changed. Restore a valid settings backup before trying again.";

    public const string InvalidSettingsWithoutRecovery = "Noted found invalid settings but could not restore a valid recovery copy. Existing files were not changed. Restore a valid settings backup before trying again.";

    public static string RecoveryWriteFailed(string path)
        => $"Noted found a valid settings recovery copy at '{path}', but could not restore it. The recovery copy was not changed. Check folder permissions, available disk space, and whether another program is using the settings file, then try again.";

    public static string VerificationFailed(string path)
        => $"Could not verify the saved settings in '{path}'. The save may have reached disk. Further saves are blocked to protect existing data. Check the settings file and folder access, then restart Noted.";

    public const string RecoveryScanFailed = "Noted could not completely inspect settings recovery files. No recovery files were changed. Check access to the settings folder and its recovery files, then try again.";

    public static string ConflictingRecoveryFiles(string description)
        => $"Noted found conflicting settings recovery files with the same {description}. No recovery file was selected or changed. Keep the recovery files and restore a known-good settings backup before trying again.";

    public const string WritesBlocked = "Further settings saves are blocked because an earlier settings operation failed. Resolve the file access or data problem, then restart Noted.";

    public static string CurrentSettingsUnreadable(string path)
        => $"Could not read or validate the current settings file '{path}'. Further saves are blocked to protect existing data. Check file access and the settings file, then restart Noted.";

    public const string RevisionLimitReached = "Settings cannot be saved because the settings revision reached its maximum value. Further settings writes are blocked until restart.";

    public static string SaveFailed(string path)
        => $"Could not save settings to '{path}'. Check folder permissions, available disk space, and whether another program is using the file, then try again.";
}
