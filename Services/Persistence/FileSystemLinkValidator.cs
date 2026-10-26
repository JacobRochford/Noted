using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Noted.Services;

// recheck paths before each access, but another process can still replace them
// between the check and the file operation.
internal static class FileSystemLinkValidator
{
    internal static FileAttributes? GetAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Entry '{path}' is a filesystem link and cannot be accessed safely.");
            return attributes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null)
            ValidateDirectory(parent);
        GetAttributes(fullPath);
    }

    internal static void ValidateDirectory(string path)
    {
        ValidatePath(path);
        var attributes = GetAttributes(path);
        if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0)
            throw new IOException($"Folder '{path}' is occupied by a file.");
    }

    internal static void ValidateTree(string path)
    {
        ValidateDirectory(path);
        if (!Directory.Exists(path))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            ValidatePath(entry);
            var attributes = GetAttributes(entry)
                ?? throw new IOException($"Entry '{entry}' disappeared during validation.");
            if ((attributes & FileAttributes.Directory) != 0)
                ValidateTree(entry);
        }
    }

    // allow links only in explicitly selected root paths. reject links beneath them,
    // including along paths where the destination doesn't exist yet.
    internal static string ValidateWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"Entry '{path}' is outside '{root}'.");
        var destination = Path.GetFullPath(Path.Combine(ResolveDirectory(root), relative));
        ValidatePath(destination);
        return destination;
    }

    internal static string ResolveDirectory(string path)
    {
        var resolved = ResolveDirectory(path, 0);
        if (!Directory.Exists(resolved))
        {
            var parent = Path.GetDirectoryName(resolved);
            return parent is null ? resolved : Path.Combine(ResolveDirectory(parent), Path.GetFileName(resolved));
        }

        // resolve aliases such as Windows short names and mapped drives.
        // this handle finds the location; it doesn't protect later file operations.
        using var handle = OpenDirectory(resolved, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Folder '{resolved}' could not be resolved safely.", new Win32Exception(Marshal.GetLastWin32Error()));
        return GetFinalPath(handle, resolved);
    }

    internal static string ResolveFile(string path)
    {
        ValidatePath(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetFinalPath(handle, path);
    }

    private static string GetFinalPath(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"Entry '{path}' has no verifiable filesystem location.", new Win32Exception(Marshal.GetLastWin32Error()));
        var finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + finalPath[8..];
        if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var dosPath = finalPath[4..];
            if (Path.IsPathFullyQualified(dosPath))
                return dosPath;
        }
        throw new IOException($"Entry '{path}' has an unsupported resolved filesystem location.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint sharing,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    private static string ResolveDirectory(string path, int depth)
    {
        if (depth > 64)
            throw new IOException($"Filesystem links in '{path}' could not be resolved safely.");
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        var resolved = parent is null
            ? fullPath
            : Path.Combine(ResolveDirectory(parent, depth), Path.GetFileName(fullPath));
        FileAttributes attributes;
        try { attributes = File.GetAttributes(resolved); }
        catch (FileNotFoundException) { return resolved; }
        catch (DirectoryNotFoundException) { return resolved; }
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException($"Folder '{resolved}' is occupied by a file.");
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return resolved;
        var target = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"Filesystem link '{resolved}' has no resolvable target.");
        var result = ResolveDirectory(target.FullName, depth + 1);
        if (!Directory.Exists(result))
            throw new IOException($"Filesystem link '{resolved}' has a missing target.");
        return result;
    }
}
