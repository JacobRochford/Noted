using System.IO;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteEditorSessionService : INoteEditorSessionService
{
    private readonly string _sessionFilePath;

    public NoteEditorSessionService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var sessionDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "session");
        _sessionFilePath = Path.Combine(sessionDirectory, "editor-workspace.json");
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

        var json = JsonSerializer.Serialize(session);
        AtomicFileWriter.WriteAllText(_sessionFilePath, json);
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
}
