using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noted.Services;

internal static class BackupFormat
{
    internal const int BackupSchemaVersion = 1;
    internal const int SupportedSettingsSchemaVersion = 1;
    internal const string ManifestFileName = "manifest.json";
    internal const string FilesDirectoryName = "files";

    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    internal static string GetDisplayPath(string logicalPath)
    {
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        var separator = normalizedPath.IndexOf('/');
        return separator >= 0 && separator < normalizedPath.Length - 1
            ? normalizedPath[(separator + 1)..]
            : normalizedPath;
    }

    internal static string GetFileCategory(string logicalPath)
    {
        var path = NormalizeLogicalPath(logicalPath);
        if (path.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
            return "Notes";
        if (path.StartsWith("app/DeletedNotes/", StringComparison.OrdinalIgnoreCase))
            return "Deleted notes";
        if (path.StartsWith("app/note-history/", StringComparison.OrdinalIgnoreCase))
            return "Note history";
        if (path.StartsWith("app/recovery/micro-scratchpads/", StringComparison.OrdinalIgnoreCase))
            return "MiniPad";
        if (path.StartsWith("app/recovery/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("app/session/editor-workspace.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Open notes";
        }
        if (path.Equals("app/checklist.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("app/checklist-tabs.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Checklist";
        }
        if (path.Equals("app/dictionary.json", StringComparison.OrdinalIgnoreCase))
            return "Dictionary";
        if (path.Equals("app/scratchpad.rtf", StringComparison.OrdinalIgnoreCase))
            return "Scratchpad";
        if (path.Equals("app/settings.json", StringComparison.OrdinalIgnoreCase))
            return "Settings";
        return "Application data";
    }

    internal static BackupContentFormat GetContentFormat(string logicalPath)
    {
        var extension = Path.GetExtension(logicalPath);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return BackupContentFormat.Json;
        if (extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            return BackupContentFormat.RichText;
        return BackupContentFormat.Text;
    }

    internal static string GetContainedPath(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var platformPath = NormalizeLogicalPath(relativePath)
            .Replace('/', Path.DirectorySeparatorChar);
        var fullPath = NormalizeBackupDataPath(Path.Combine(root, platformPath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The backup path '{relativePath}' escapes its backup directory.");
        return fullPath;
    }

    internal static string ToLogicalPath(string prefix, string relativePath) =>
        $"{prefix}/{NormalizeLogicalPath(relativePath)}";

    internal static string NormalizeLogicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("The backup contains an empty file path.");
        return path.Replace('\\', '/').TrimStart('/');
    }

    internal static string NormalizeBackupDataPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The backup contains an invalid file path.", ex);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record FullBackupManifest(
    int SchemaVersion,
    [property: JsonPropertyName("SnapshotId")]
    Guid BackupId,
    [property: JsonPropertyName("Role")]
    FullBackupType Type,
    DateTime CreatedUtc,
    string AppDataRoot,
    string NotesRoot,
    IReadOnlyList<FullBackupEntry> Entries);

internal sealed record FullBackupEntry(
    string LogicalPath,
    string OriginalPath,
    long Length,
    string Sha256,
    int? ItemCount,
    long? ContentLength);

internal sealed record FullBackupRestoreRequest(
    int SchemaVersion,
    [property: JsonPropertyName("SnapshotId")]
    Guid BackupId,
    DateTime RequestedUtc,
    Guid? ImportId = null);
