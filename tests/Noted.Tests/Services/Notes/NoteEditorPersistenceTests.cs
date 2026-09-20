using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorPersistenceTests
{
    [TestMethod]
    public void DuplicateNormalizedSessionPathsCreateOneDocumentFromTheFirstSuccessfulEntry()
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.txt", "saved");
        var alias = Path.Combine(directory.Path, "unused", "..", "note.txt");
        var sessions = new StubSessionService(new NoteEditorSession
        {
            Tabs =
            [
                new NoteEditorTabState
                {
                    FilePath = alias,
                    CaretIndex = 2,
                    VerticalOffset = 7,
                    MarkdownPreviewEnabled = true,
                    UsesGeneratedName = true,
                    InitialFileContent = "initial"
                },
                new NoteEditorTabState { FilePath = path, CaretIndex = 5 }
            ]
        });
        var persistence = new NoteEditorPersistence(
            new StubContentService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = "saved"
            }),
            new StubRecoveryService(),
            sessions);

        var result = persistence.LoadWorkspace(loadSavedSession: true);

        Assert.HasCount(1, result.Documents);
        var document = result.Documents[0];
        Assert.AreEqual(Path.GetFullPath(path), document.FilePath);
        Assert.AreEqual(2, document.CaretIndex);
        Assert.AreEqual(7, document.VerticalOffset);
        Assert.IsTrue(document.MarkdownPreviewEnabled);
        Assert.IsTrue(document.UsesGeneratedName);
        Assert.AreEqual("initial", document.InitialFileContent);
        Assert.AreNotEqual(Guid.Empty, document.DocumentId);
    }

    [TestMethod]
    public void LaterDuplicateCanRestoreWhenTheFirstEntryFails()
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.txt", "saved");
        var content = new StubContentService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = "saved"
        })
        {
            FailedLoadsRemaining = 1
        };
        var persistence = new NoteEditorPersistence(
            content,
            new StubRecoveryService(),
            new StubSessionService(new NoteEditorSession
            {
                Tabs =
                [
                    new NoteEditorTabState { FilePath = path, CaretIndex = 1 },
                    new NoteEditorTabState { FilePath = path, CaretIndex = 4 }
                ]
            }));

        var result = persistence.LoadWorkspace(loadSavedSession: true);

        Assert.HasCount(1, result.Documents);
        Assert.AreEqual(4, result.Documents[0].CaretIndex);
    }

    [TestMethod]
    public void RecoveryDraftAttachesToTheSessionDocumentIdentity()
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.txt", "saved");
        var alias = Path.Combine(directory.Path, ".", "note.txt");
        var persistence = new NoteEditorPersistence(
            new StubContentService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = "saved"
            }),
            new StubRecoveryService
            {
                Drafts = [new NoteRecoveryDraft(alias, "recovered", DateTime.UtcNow)]
            },
            new StubSessionService(new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = path }]
            }));

        var result = persistence.LoadWorkspace(loadSavedSession: true);

        Assert.HasCount(1, result.Documents);
        Assert.AreEqual("recovered", result.Documents[0].Content);
        Assert.IsTrue(result.Documents[0].IsDirty);
        Assert.AreEqual(result.Documents[0].DocumentId, result.ActiveDocumentId);
    }

    [TestMethod]
    public void SamePrimaryAndSecondaryPathRestoresOnlyThePrimaryPane()
    {
        using var directory = new TemporaryTestDirectory();
        var primaryPath = CreateNote(directory, "primary.txt", "primary");
        var secondaryPath = CreateNote(directory, "secondary.txt", "secondary");
        var primaryAlias = Path.Combine(directory.Path, "unused", "..", "primary.txt");
        var persistence = new NoteEditorPersistence(
            new StubContentService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryPath] = "primary",
                [secondaryPath] = "secondary"
            }),
            new StubRecoveryService(),
            new StubSessionService(new NoteEditorSession
            {
                Tabs =
                [
                    new NoteEditorTabState { FilePath = primaryPath },
                    new NoteEditorTabState { FilePath = secondaryPath }
                ],
                ActiveFilePath = primaryPath,
                SecondaryFilePath = primaryAlias
            }));

        var result = persistence.LoadWorkspace(loadSavedSession: true);

        Assert.HasCount(2, result.Documents);
        Assert.AreEqual(result.Documents[0].DocumentId, result.ActiveDocumentId);
        Assert.IsNull(result.SecondaryDocumentId);
    }

    [TestMethod]
    public void SavedSessionUsesPathsWithoutPersistingDocumentIds()
    {
        using var directory = new TemporaryTestDirectory();
        var sessions = new StubSessionService(new NoteEditorSession());
        var workspace = new NoteEditorWorkspace();
        var primary = new OpenNoteDocument(
            directory.File("primary.txt"),
            "primary",
            "primary",
            isDirty: false);
        var secondary = new OpenNoteDocument(
            directory.File("secondary.txt"),
            "secondary",
            "secondary",
            isDirty: false);
        workspace.Add(primary);
        workspace.Add(secondary);
        workspace.Activate(primary);
        workspace.ShowSecondary(secondary, focus: false);
        var persistence = new NoteEditorPersistence(
            new StubContentService(new Dictionary<string, string>()),
            new StubRecoveryService(),
            sessions);

        var result = persistence.SaveSession(workspace);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(sessions.SavedSession);
        Assert.AreEqual(primary.FilePath, sessions.SavedSession.ActiveFilePath);
        Assert.AreEqual(secondary.FilePath, sessions.SavedSession.SecondaryFilePath);
        Assert.HasCount(2, sessions.SavedSession.Tabs);
        Assert.IsNull(typeof(NoteEditorTabState).GetProperty("DocumentId"));
    }

    private static string CreateNote(
        TemporaryTestDirectory directory,
        string fileName,
        string content)
    {
        var path = directory.File(fileName);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private sealed class StubContentService(
        IReadOnlyDictionary<string, string> contentByPath) : INoteContentService
    {
        internal int FailedLoadsRemaining { get; set; }

        public string Load(string filePath)
        {
            if (FailedLoadsRemaining > 0)
            {
                FailedLoadsRemaining--;
                throw new IOException("Simulated read failure.");
            }

            return contentByPath[Path.GetFullPath(filePath)];
        }

        public string? Save(string filePath, string content) => throw new NotSupportedException();
        public string? SaveAs(string filePath, string content) => throw new NotSupportedException();
        public string? SaveNewNoteAs(
            string initialPath,
            string destinationPath,
            string content,
            string initialContent) => throw new NotSupportedException();
    }

    private sealed class StubRecoveryService : INoteRecoveryService
    {
        internal IReadOnlyList<NoteRecoveryDraft> Drafts { get; init; } = [];

        public NoteRecoveryDraftLoadResult LoadDraft(string filePath) =>
            new(null, []);

        public NoteRecoveryLoadResult LoadDrafts() =>
            new(Drafts, []);

        public string? SaveDraft(string filePath, string content) =>
            throw new NotSupportedException();

        public void DeleteDraft(string filePath)
        {
        }
    }

    private sealed class StubSessionService(NoteEditorSession session) : INoteEditorSessionService
    {
        internal NoteEditorSession? SavedSession { get; private set; }

        public NoteEditorSessionLoadResult Load() => new(session, []);

        public string? Save(NoteEditorSession sessionToSave)
        {
            SavedSession = sessionToSave;
            return null;
        }
    }
}
