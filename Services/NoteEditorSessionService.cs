using System.IO;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteEditorSessionService : INoteEditorSessionService
{
    private readonly string _sessionDirectory;
    private readonly string _sessionFilePath;

    public NoteEditorSessionService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _sessionDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "session");
        _sessionFilePath = Path.Combine(_sessionDirectory, "editor-workspace.json");
    }

    public NoteEditorSession Load()
    {
        if (!File.Exists(_sessionFilePath))
            return new NoteEditorSession();

        try
        {
            var json = File.ReadAllText(_sessionFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<NoteEditorSession>(json)
                ?? new NoteEditorSession();
        }
        catch (Exception ex) when (IsExpectedSessionException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new NoteEditorSession();
        }
    }

    public void Save(NoteEditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Directory.CreateDirectory(_sessionDirectory);
        var temporaryPath = _sessionFilePath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(session);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_sessionFilePath))
                File.Replace(temporaryPath, _sessionFilePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _sessionFilePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsExpectedSessionException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (IsExpectedSessionException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
