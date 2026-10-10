using System.IO;
using System.Security.Cryptography;

namespace Noted.Services;

internal static class MiniPadRecoveryMigration
{
    internal static void Migrate(string appDataDirectory)
    {
        var source = MiniPadStorage.GetLegacyRecoveryDirectory(appDataDirectory);
        var destination = MiniPadStorage.GetRecoveryDirectory(appDataDirectory);
        try
        {
            // check parent folders first to reject junctions before looking inside.
            FileSystemLinkValidator.ValidateDirectory(Path.GetDirectoryName(source)!);
            var sourceAttributes = GetAttributes(source);
            var destinationAttributes = GetAttributes(destination);
            RequireDirectory(destination, destinationAttributes);
            if (sourceAttributes is null)
                return;
            RequireDirectory(source, sourceAttributes);

            var entries = new List<MigrationEntry>();
            Inspect(source, "", entries);
            if (destinationAttributes is null)
            {
                Directory.Move(source, destination);
                Verify(destination, entries);
                return;
            }

            // check every source entry for conflicts before moving anything, even unrecognized files.
            foreach (var entry in entries)
            {
                var target = Path.Combine(destination, entry.RelativePath);
                var attributes = GetAttributes(target);
                if (attributes is not null &&
                    (!entry.IsDirectory || (attributes.Value & FileAttributes.Directory) == 0))
                {
                    throw new IOException($"Recovery migration conflict between '{Path.Combine(source, entry.RelativePath)}' and '{target}'. Both entries must be preserved, even if their contents match.");
                }
            }

            MoveContents(source, destination);

            Verify(destination, entries);
            // delete only empty folders so new or unexpected files stop cleanup.
            foreach (var entry in entries.Where(entry => entry.IsDirectory).Reverse())
            {
                var path = Path.Combine(source, entry.RelativePath);
                if (GetAttributes(path) is not null)
                    Directory.Delete(path, recursive: false);
            }
            Directory.Delete(source, recursive: false);
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
        {
            throw new MiniPadMigrationException(
                $"MiniPad recovery migration from '{source}' to '{destination}' could not finish. " +
                "Noted has stopped before loading or saving MiniPad drafts. Preserve both folders, " +
                "resolve the reported conflict, filesystem link, or access problem, then restart Noted to retry. " +
                ex.Message, ex);
        }
    }

    private static void MoveContents(string source, string destination)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(path));
            var attributes = GetAttributes(path)
                ?? throw new IOException($"Recovery entry '{path}' disappeared during migration.");
            if ((attributes & FileAttributes.Directory) == 0)
                File.Move(path, target, overwrite: false);
            else if (GetAttributes(target) is null)
                Directory.Move(path, target);
            else
            {
                RequireDirectory(target, GetAttributes(target));
                MoveContents(path, target);
            }
        }
    }

    private static void Inspect(string root, string relativePath, ICollection<MigrationEntry> entries)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(root, relativePath)))
        {
            var attributes = GetAttributes(path)
                ?? throw new IOException($"Recovery entry '{path}' disappeared during migration.");
            var relative = Path.GetRelativePath(root, path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            entries.Add(new MigrationEntry(relative, isDirectory, isDirectory ? null : HashFile(path)));
            if (isDirectory)
                Inspect(root, relative, entries);
        }
    }

    private static void Verify(string destination, IEnumerable<MigrationEntry> entries)
    {
        foreach (var entry in entries)
        {
            var path = Path.Combine(destination, entry.RelativePath);
            var attributes = GetAttributes(path);
            if (attributes is null ||
                ((attributes.Value & FileAttributes.Directory) != 0) != entry.IsDirectory ||
                (!entry.IsDirectory && HashFile(path) != entry.Hash))
            {
                throw new IOException($"Migrated recovery entry '{path}' failed verification.");
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RequireDirectory(string path, FileAttributes? attributes)
    {
        if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0)
            throw new IOException($"Recovery folder '{path}' is occupied by a file.");
    }

    private static FileAttributes? GetAttributes(string path) =>
        FileSystemLinkValidator.GetAttributes(path);

    private sealed record MigrationEntry(string RelativePath, bool IsDirectory, string? Hash);
}

internal sealed class MiniPadMigrationException(string message, Exception innerException)
    : IOException(message, innerException);
