using System.IO;

namespace Noted.Services;

internal enum FixedBackupContent { Settings, Checklist, ChecklistTabs, Dictionary, Jots, Scratchpad, EditorSession, JotCapture }

internal sealed record FixedBackupFile(
    FixedBackupContent Content,
    string RelativePath,
    string Category,
    bool ProtectMissing,
    bool ProtectEmptyCollection)
{
    internal string LogicalPath => PersistencePaths.AppBackupPrefix + RelativePath.Replace('\\', '/');
}

internal static class BackupContentDefinitions
{
    internal static IReadOnlyList<FixedBackupFile> FixedFiles { get; } = Array.AsReadOnly(new[]
    {
        new FixedBackupFile(FixedBackupContent.Settings, PersistencePaths.SettingsFile, "Settings", true, false),
        new FixedBackupFile(FixedBackupContent.Checklist, PersistencePaths.ChecklistFile, "Checklist", true, true),
        new FixedBackupFile(FixedBackupContent.ChecklistTabs, PersistencePaths.ChecklistTabsFile, "Checklist", true, true),
        new FixedBackupFile(FixedBackupContent.Dictionary, PersistencePaths.DictionaryFile, "Dictionary", true, true),
        new FixedBackupFile(FixedBackupContent.Jots, PersistencePaths.JotsFile, "Jots", true, true),
        new FixedBackupFile(FixedBackupContent.JotCapture, PersistencePaths.JotCaptureDraftFile, "Quick Jot", true, false),
        new FixedBackupFile(FixedBackupContent.Scratchpad, PersistencePaths.ScratchpadFile, "Scratchpad", true, false),
        new FixedBackupFile(FixedBackupContent.EditorSession,
            Path.Combine(PersistencePaths.SessionDirectory, PersistencePaths.EditorSessionFile), "Open notes", false, false)
    });

    internal static FixedBackupFile? Find(string normalizedLogicalPath) => FixedFiles.FirstOrDefault(
        file => file.LogicalPath.Equals(normalizedLogicalPath, StringComparison.OrdinalIgnoreCase));
}
