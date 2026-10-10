using System.IO;

namespace Noted.Services;

internal static class MiniPadStorage
{
    // keep these storage names stable when display names change.
    internal const string DirectoryName = "minipads";
    internal const string LegacyDirectoryName = "micro-scratchpads";
    internal const string BackupPrefix = PersistencePaths.RecoveryBackupPrefix + DirectoryName + "/";
    internal const string LegacyBackupPrefix = PersistencePaths.RecoveryBackupPrefix + LegacyDirectoryName + "/";

    internal static string GetRecoveryDirectory(string appDataDirectory) =>
        Path.Combine(Path.GetFullPath(appDataDirectory), PersistencePaths.RecoveryDirectory, DirectoryName);

    internal static string GetLegacyRecoveryDirectory(string appDataDirectory) =>
        Path.Combine(Path.GetFullPath(appDataDirectory), PersistencePaths.RecoveryDirectory, LegacyDirectoryName);

    internal static bool IsBackupPath(string normalizedPath) =>
        normalizedPath.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeBackupPath(string normalizedPath) =>
        normalizedPath.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase)
            ? BackupPrefix + normalizedPath[LegacyBackupPrefix.Length..]
            : normalizedPath;
}
