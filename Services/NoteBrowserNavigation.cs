using System.IO;

namespace Noted.Services;

internal sealed class NoteBrowserNavigation
{
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();

    public NoteBrowserNavigation(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        CurrentDirectory = RootDirectory;
    }

    public string RootDirectory { get; private set; }
    public string CurrentDirectory { get; private set; }

    public string CurrentFolderName
    {
        get
        {
            if (PathsEqual(CurrentDirectory, RootDirectory))
                return string.Empty;

            var relative = Path.GetRelativePath(RootDirectory, CurrentDirectory);
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ");
        }
    }

    public bool CanNavigateUp => !PathsEqual(CurrentDirectory, RootDirectory);
    public bool CanNavigateBack => _backHistory.Count > 0;
    public bool CanNavigateForward => _forwardHistory.Count > 0;

    public void ChangeRoot(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        CurrentDirectory = RootDirectory;
        _backHistory.Clear();
        _forwardHistory.Clear();
    }

    public void EnsureCurrentDirectoryExists()
    {
        if (!Directory.Exists(CurrentDirectory))
            CurrentDirectory = RootDirectory;
    }

    public void NavigateTo(string folderName)
    {
        if (!TryResolveChildPath(CurrentDirectory, folderName, out var target))
            return;

        NavigateToDirectory(target, clearForwardHistory: true);
    }

    public void NavigateUp()
    {
        if (!CanNavigateUp)
            return;

        var parent = Path.GetDirectoryName(CurrentDirectory);
        var target = parent is not null && TryNormalizeContainedPath(parent, out var normalizedParent)
            ? normalizedParent
            : RootDirectory;
        NavigateToDirectory(target, clearForwardHistory: true);
    }

    public void NavigateBack()
    {
        if (!TryPopValidHistoryEntry(_backHistory, out var target))
            return;

        _forwardHistory.Push(CurrentDirectory);
        CurrentDirectory = target;
    }

    public void NavigateForward()
    {
        if (!TryPopValidHistoryEntry(_forwardHistory, out var target))
            return;

        _backHistory.Push(CurrentDirectory);
        CurrentDirectory = target;
    }

    private void NavigateToDirectory(string target, bool clearForwardHistory)
    {
        if (!TryNormalizeContainedPath(target, out var normalizedTarget) ||
            IsInArchive(normalizedTarget) ||
            !Directory.Exists(normalizedTarget) ||
            PathsEqual(normalizedTarget, CurrentDirectory))
            return;

        _backHistory.Push(CurrentDirectory);
        CurrentDirectory = normalizedTarget;

        if (clearForwardHistory)
            _forwardHistory.Clear();
    }

    private bool TryPopValidHistoryEntry(Stack<string> history, out string target)
    {
        while (history.Count > 0)
        {
            var historyPath = history.Pop();
            if (!TryNormalizeContainedPath(historyPath, out var fullPath) ||
                IsInArchive(fullPath) ||
                !Directory.Exists(fullPath))
                continue;

            target = fullPath;
            return true;
        }

        target = string.Empty;
        return false;
    }

    private bool TryResolveChildPath(string parentDirectory, string childPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (!TryNormalizeContainedPath(parentDirectory, out var normalizedParent))
            return false;

        try
        {
            var combinedPath = Path.Combine(normalizedParent, childPath);
            if (!TryNormalizeContainedPath(normalizedParent, combinedPath, out var normalizedChild, allowRoot: false))
                return false;

            return TryNormalizeContainedPath(normalizedChild, out resolvedPath);
        }
        catch (Exception ex) when (IsExpectedPathException(ex))
        {
            return false;
        }
    }

    private bool TryNormalizeContainedPath(string path, out string normalizedPath, bool allowRoot = true) =>
        TryNormalizeContainedPath(RootDirectory, path, out normalizedPath, allowRoot);

    private bool IsInArchive(string path) =>
        TryNormalizeContainedPath(Path.Combine(RootDirectory, ".archive"), path, out _);

    private static bool TryNormalizeContainedPath(
        string rootDirectory,
        string path,
        out string normalizedPath,
        bool allowRoot = true)
    {
        normalizedPath = string.Empty;
        try
        {
            var normalizedRoot = Path.GetFullPath(rootDirectory);
            normalizedPath = Path.GetFullPath(path);
            if (PathsEqual(normalizedPath, normalizedRoot))
                return allowRoot;

            var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsExpectedPathException(ex))
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException;
}
