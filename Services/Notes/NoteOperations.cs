using System.IO;
using Noted.Models;

namespace Noted.Services;

internal sealed record NoteCreationResult(
    CreatedNote? Note,
    string? FilePath,
    string? NoteKey,
    string? Error);

internal sealed record NoteRenameResult(
    bool Success,
    string? OldNoteKey,
    string? NewNoteKey,
    string? NewFilePath,
    string? Error,
    bool IsExternalPath = false);

internal sealed record FolderRenameResult(
    bool Success,
    string? NewFolderPath,
    string? Error);

internal sealed class NoteOperations
{
    private readonly INoteFileService _fileService;
    private readonly NoteEditorWorkspace _workspace;

    internal NoteOperations(
        INoteFileService fileService,
        NoteEditorWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(workspace);
        _fileService = fileService;
        _workspace = workspace;
    }

    internal string SuggestNoteName() => _fileService.SuggestNoteName();

    internal NoteCreationResult CreateNote(string? requestedName = null)
    {
        var (note, error) = _fileService.CreateNote(requestedName);
        if (note is null)
            return new NoteCreationResult(null, null, null, error);

        var filePath = Path.Combine(_fileService.CurrentDirectory, note.FileName);
        return new NoteCreationResult(
            note,
            filePath,
            _fileService.GetNoteKey(filePath),
            null);
    }

    internal bool DeleteNote(string filePath) =>
        _fileService.DeleteNote(
            Path.GetFileName(filePath),
            Path.GetDirectoryName(filePath));

    internal (bool Success, string? Error) DeleteFolder(string folderPath) =>
        _fileService.DeleteFolder(Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath)));

    internal NoteRenameResult RenameNote(string filePath, string newDisplayName)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (!_fileService.TryGetNoteKey(normalizedPath, out var oldNoteKey))
        {
            return new NoteRenameResult(
                false,
                null,
                null,
                null,
                "This file is outside the notes folder and cannot be renamed here. Use Save As to choose a new name or location.",
                IsExternalPath: true);
        }

        var containingDirectory = Path.GetDirectoryName(normalizedPath)
            ?? _fileService.CurrentDirectory;
        var oldFileName = Path.GetFileName(normalizedPath);
        var (success, newFileName, error) = _fileService.RenameNote(
            oldFileName,
            newDisplayName,
            containingDirectory);
        if (!success)
            return new NoteRenameResult(false, oldNoteKey, null, null, error);

        var renamedFilePath = Path.Combine(
            containingDirectory,
            newFileName ?? oldFileName);
        var openDocument = _workspace.FindByPath(normalizedPath);
        if (openDocument is not null)
        {
            _workspace.UpdatePath(
                openDocument,
                renamedFilePath,
                clearGeneratedName: true,
                markPresent: true);
        }

        return new NoteRenameResult(
            true,
            oldNoteKey,
            _fileService.GetNoteKey(renamedFilePath),
            renamedFilePath,
            null);
    }

    internal FolderRenameResult RenameFolder(string folderPath, string newName)
    {
        var normalizedPath = Path.GetFullPath(folderPath);
        var oldFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedPath));
        var (success, newFolderName, error) = _fileService.RenameFolder(oldFolderName, newName);
        if (!success)
            return new FolderRenameResult(false, null, error);
        if (newFolderName is null)
            return new FolderRenameResult(true, null, null);

        var parentDirectory = Path.GetDirectoryName(normalizedPath)
            ?? _fileService.CurrentDirectory;
        var newFolderPath = Path.Combine(parentDirectory, newFolderName);
        _workspace.UpdateDirectoryPath(normalizedPath, newFolderPath);
        return new FolderRenameResult(true, newFolderPath, null);
    }
}
