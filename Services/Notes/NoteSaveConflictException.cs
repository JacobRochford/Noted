using System.IO;

namespace Noted.Services;

internal sealed class NoteSaveConflictException : IOException
{
    internal NoteSaveConflictException(string filePath, string? preservedFilePath = null)
        : base(preservedFilePath is null
            ? $"'{Path.GetFileName(filePath)}' changed on disk after it was opened. " +
              "Noted did not overwrite the newer file. Copy your edits if needed, " +
              "then close and reopen the tab to load the current file."
            : $"'{Path.GetFileName(filePath)}' changed on disk during saving. Your editor text was written, " +
              $"and the displaced disk version was kept at:\n\n{preservedFilePath}\n\n" +
              "Review both versions before saving again or closing this tab.")
    {
        PreservedFilePath = preservedFilePath;
    }

    internal string? PreservedFilePath { get; }
}
