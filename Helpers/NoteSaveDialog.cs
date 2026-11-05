using System.IO;
using Microsoft.Win32;
using Noted.Models;
using Noted.Services;

namespace Noted.Helpers;

internal static class NoteSaveDialog
{
    internal static SaveFileDialog Create(string filePath) => new()
    {
        Title = "Save Note",
        InitialDirectory = Path.GetDirectoryName(filePath),
        FileName = Path.GetFileNameWithoutExtension(filePath),
        AddExtension = true,
        OverwritePrompt = false,
        Filter = NoteFileExtensions.SaveFileDialogFilter,
        FilterIndex = GetFilterIndex(filePath)
    };

    internal static NoteSaveDestination? ConfirmDestination(SaveFileDialog dialog,
        INoteContentService contentService, Func<string, bool> confirmOverwrite)
    {
        var path = dialog.FileName;
        if (!Path.HasExtension(path) && !path.EndsWith('.'))
            path += NoteFileExtensions.SupportedExtensions[dialog.FilterIndex - 1];
        if (!NoteFileService.IsValidFileName(Path.GetFileName(path)) || !NoteFileExtensions.IsSupported(path))
            throw new ArgumentException("Choose a valid Windows filename with a supported text file extension.");

        path = Path.GetFullPath(path);
        dialog.FileName = path;
        dialog.FilterIndex = GetFilterIndex(path);
        TextFileContent? existing = null;
        try
        {
            existing = contentService.LoadWithEncoding(path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        // capture the destination before asking, so changes during confirmation are not approved.
        if (existing is not null && !confirmOverwrite(path))
            return null;
        return new NoteSaveDestination(path, existing);
    }

    private static int GetFilterIndex(string filePath) =>
        NoteFileExtensions.SupportedExtensions
            .Select((extension, index) => (extension, index))
            .First(item => item.extension.Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase))
            .index + 1;
}
