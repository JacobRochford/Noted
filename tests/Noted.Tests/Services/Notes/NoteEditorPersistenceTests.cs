using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorPersistenceTests
{
    [TestMethod]
    public void SidePreviewRestoresThroughWorkspacePersistenceWithoutChangingText()
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.md", "# source");
        var workspace = new NoteEditorWorkspace();
        var document = new OpenNoteDocument(path, "# source", "# source", false);
        workspace.Add(document);
        workspace.SetMarkdownPreviewMode(document, MarkdownPreviewMode.SideBySide);
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            new StubRecoveryService(), new StubSessionService(NoteEditorPersistence.CreateSession(workspace)));

        var restored = persistence.LoadWorkspace(loadSavedSession: true).Documents.Single();

        Assert.AreEqual(MarkdownPreviewMode.SideBySide, restored.MarkdownPreviewMode);
        Assert.AreEqual("# source", restored.Content);
        Assert.IsFalse(restored.IsDirty);
    }

    [TestMethod]
    public void SessionRestoresDetectedEncodingAndPendingConversionWithoutChangingText()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("encoded.txt");
        File.WriteAllBytes(path, Convert.FromHexString("FFFEE900"));
        var workspace = new NoteEditorWorkspace();
        var document = new OpenNoteDocument(path, "é", "é", isDirty: false,
            encoding: TextFileEncoding.Utf16LE);
        workspace.Add(document);
        workspace.SetEncoding(document, TextFileEncoding.Windows1252);
        var session = NoteEditorPersistence.CreateSession(workspace);
        Assert.AreEqual(TextFileEncoding.Windows1252, session.Tabs[0].PendingEncoding);
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            new StubRecoveryService(), new StubSessionService(session));

        var restored = persistence.LoadWorkspace(loadSavedSession: true).Documents.Single();

        Assert.AreEqual("é", restored.Content);
        Assert.AreEqual(TextFileEncoding.Utf16LE, restored.SavedEncoding);
        Assert.AreEqual(TextFileEncoding.Windows1252, restored.Encoding);
        Assert.IsTrue(restored.IsDirty);
        CollectionAssert.AreEqual(Convert.FromHexString("FFFEE900"), File.ReadAllBytes(path));
    }

    [TestMethod]
    public void SessionWithoutPendingConversionUsesCurrentDiskEncoding()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("encoded.txt");
        File.WriteAllBytes(path, Convert.FromHexString("FEFF00E9"));
        var session = new NoteEditorSession
        {
            Tabs = [new NoteEditorTabState { FilePath = path }]
        };
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            new StubRecoveryService(), new StubSessionService(session));

        var restored = persistence.LoadWorkspace(loadSavedSession: true).Documents.Single();

        Assert.AreEqual(TextFileEncoding.Utf16BE, restored.Encoding);
        Assert.AreEqual(TextFileEncoding.Utf16BE, restored.SavedEncoding);
        Assert.IsFalse(restored.IsDirty);
        var workspace = new NoteEditorWorkspace();
        workspace.Add(restored);
        Assert.IsNull(NoteEditorPersistence.CreateSession(workspace).Tabs[0].PendingEncoding);
    }

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

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnreadableOriginalReportsOnePreservedDraftAfterAllAttempts(bool loadSavedSession)
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "locked.txt", "original content");
        var originalBytes = File.ReadAllBytes(path);
        var recovery = new NoteRecoveryService(directory.Path);
        recovery.SaveDraft(path, "recoverable edits");
        var draftPath = Directory.GetFiles(directory.File("recovery"), "*.json").Single();
        var draftBytes = File.ReadAllBytes(draftPath);
        var session = new NoteEditorSession
        {
            Tabs =
            [
                new NoteEditorTabState { FilePath = path },
                new NoteEditorTabState { FilePath = Path.Combine(directory.Path, ".", "locked.txt") }
            ]
        };
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            recovery, new StubSessionService(session));

        NoteEditorWorkspaceLoadResult result;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            result = persistence.LoadWorkspace(loadSavedSession);

        Assert.HasCount(0, result.Documents);
        var issue = result.RecoveryIssues.Single();
        Assert.AreEqual(path, issue.FilePath);
        StringAssert.Contains(issue.Message, path);
        StringAssert.Contains(issue.Message, "remains preserved");
        StringAssert.Contains(issue.Message, "recovery folder");
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(draftBytes, File.ReadAllBytes(draftPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TransientOriginalReadFailureDoesNotReportAResolvedDraft(bool redundantDraft)
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.txt", "saved");
        var content = new StubContentService(new Dictionary<string, string> { [path] = "saved" })
        {
            FailedLoadsRemaining = 1
        };
        var persistence = new NoteEditorPersistence(content,
            new StubRecoveryService
            {
                Drafts = [new NoteRecoveryDraft(path, redundantDraft ? "saved" : "recovered", DateTime.UtcNow)]
            },
            new StubSessionService(new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = path }]
            }));

        var result = persistence.LoadWorkspace(loadSavedSession: true);

        Assert.HasCount(0, result.RecoveryIssues);
        Assert.HasCount(redundantDraft ? 0 : 1, result.Documents);
        if (!redundantDraft)
        {
            Assert.AreEqual("recovered", result.Documents[0].Content);
            Assert.IsTrue(result.Documents[0].IsDirty);
        }
        Assert.AreEqual("saved", File.ReadAllText(path));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MissingOriginalStillOpensItsPreservedDraft(bool loadSavedSession)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("missing.txt");
        var recovery = new NoteRecoveryService(directory.Path);
        recovery.SaveDraft(path, "recoverable edits");
        var draftPath = Directory.GetFiles(directory.File("recovery"), "*.json").Single();
        var before = File.ReadAllBytes(draftPath);
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path), recovery,
            new StubSessionService(new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = path }]
            }));

        var result = persistence.LoadWorkspace(loadSavedSession);

        var document = result.Documents.Single();
        Assert.IsTrue(document.IsMissing);
        Assert.IsTrue(document.IsDirty);
        Assert.AreEqual("recoverable edits", document.Content);
        Assert.HasCount(0, result.RecoveryIssues);
        Assert.IsFalse(File.Exists(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(draftPath));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InvalidOriginalEncodingReportsRecoveryWithoutChangingEitherFile(bool loadSavedSession)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("invalid.txt");
        var invalidBytes = Convert.FromHexString("FFFE00");
        File.WriteAllBytes(path, invalidBytes);
        var recovery = new NoteRecoveryService(directory.Path);
        recovery.SaveDraft(path, "valid recovered text");
        var draftPath = Directory.GetFiles(directory.File("recovery"), "*.json").Single();
        var before = File.ReadAllBytes(draftPath);
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path), recovery,
            new StubSessionService(new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = path }]
            }));

        var result = persistence.LoadWorkspace(loadSavedSession);

        Assert.HasCount(0, result.Documents);
        Assert.HasCount(1, result.RecoveryIssues);
        CollectionAssert.AreEqual(invalidBytes, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(draftPath));
    }

    [TestMethod]
    public void InvalidRecoveryPrimaryStillFallsBackAndReportsItsExistingIssues()
    {
        using var directory = new TemporaryTestDirectory();
        var path = CreateNote(directory, "note.txt", "saved");
        var recovery = new NoteRecoveryService(directory.Path);
        recovery.SaveDraft(path, "previous draft");
        recovery.SaveDraft(path, "latest draft");
        var draftPath = Directory.GetFiles(directory.File("recovery"), "*.json").Single();
        File.WriteAllText(draftPath, "{broken");
        var backupBytes = File.ReadAllBytes(draftPath + ".bak");
        var persistence = new NoteEditorPersistence(new NoteContentService(directory.Path),
            new NoteRecoveryService(directory.Path), new StubSessionService(new NoteEditorSession()));

        var result = persistence.LoadWorkspace(loadSavedSession: false);

        Assert.AreEqual("previous draft", result.Documents.Single().Content);
        Assert.IsTrue(result.Documents[0].IsDirty);
        Assert.IsTrue(result.RecoveryIssues.Count > 0);
        Assert.IsFalse(result.RecoveryIssues.Any(issue => issue.Message.Contains("original document")));
        CollectionAssert.AreEqual(backupBytes, File.ReadAllBytes(draftPath + ".bak"));
        Assert.AreEqual("saved", File.ReadAllText(path));
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

        public TextFileContent LoadWithEncoding(string filePath) => new(Load(filePath), TextFileEncoding.Utf8);

        public string? Save(string filePath, string content, TextFileEncoding? encoding = null, TextFileContent? expectedVersion = null) => throw new NotSupportedException();
        public string? SaveAs(string filePath, string content, TextFileEncoding? encoding = null) => throw new NotSupportedException();
        public string? SaveNewNoteAs(
            string initialPath,
            string destinationPath,
            string content,
            string initialContent, TextFileEncoding? encoding = null) => throw new NotSupportedException();
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
