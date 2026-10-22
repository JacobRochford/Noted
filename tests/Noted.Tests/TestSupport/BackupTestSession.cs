using Noted.Services;

namespace Noted.Tests.Services;

internal static class BackupTestSession
{
    internal static void Save(string appData, params string[] paths) =>
        new NoteEditorSessionService(appData).Save(new NoteEditorSession
        {
            Tabs = paths.Select(path => new NoteEditorTabState { FilePath = path }).ToList(),
            ActiveFilePath = paths.FirstOrDefault(),
            SecondaryFilePath = paths.Skip(1).FirstOrDefault()
        });
}
