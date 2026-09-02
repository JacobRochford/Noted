# Noted File Persistence and Recovery

This document explains how Noted protects its files, chooses recovery data, and creates or restores local backups. It describes the current implementation and compatibility-sensitive storage names.

## How file protection works

| Area and meaning | What Noted does |
| --- | --- |
| **Atomic writes** - prevents a crash from exposing a half-written main file | Noted writes the complete new content to a unique temporary file in the destination directory. It uses write-through, flushes the file to disk, and then atomically replaces the existing file. |
| **Post-write verification** - confirms the saved file contains what Noted intended to write | Text files are read back and compared exactly. JSON files are also deserialized, and feature-specific stores validate required structure such as item collections, tab lists, note paths, and MiniPad IDs. |
| **Duplicate-write avoidance** - prevents unnecessary disk activity and backup rotation | Settings, Scratchpad content, MiniPad content, note recovery drafts, editor sessions, and saved-note history skip writes when the relevant content has not changed. |
| **Rolling backup** - keeps the immediately previous file version | Most application data uses one `.bak` file. When the main file is replaced, the previous main file becomes the rolling backup. |
| **Backup failure preservation** - protects the previous file if `.bak` cannot be updated | If the main file is safely saved but the rolling backup cannot be replaced, Noted preserves the previous main file under a unique `.pending-*` name and reports a warning. A backup warning does not incorrectly mark the verified main-file save as failed. |
| **Corrupt-file preservation** - keeps damaged evidence instead of silently deleting it | When supported recovery code identifies an invalid JSON file, it moves that file to a unique `.corrupt-*` name before continuing. This prevents a later save from overwriting the only remaining evidence. |
| **Unsafe recovery blocking** - stops Noted from guessing when existing data cannot be protected | If recovery files are unreadable, cannot be preserved, use an unsupported schema, or conflict at the same Settings revision, Noted blocks automatic replacement and later writes for that process. The user must resolve the reported file problem and restart Noted. |
| **Recovery ordering** - determines which file is trusted first | A valid main file wins. Settings then use saved revision numbers to compare recovery files. Simpler feature stores try the main file and then their rolling `.bak`. Settings do not use filesystem time when revision metadata is available. |
| **Settings history** - keeps older Settings states beyond the rolling backup | Settings retain up to 10 time-spaced history files. They provide older recovery points when both the main Settings file and immediate rolling backup are unusable. |
| **User backup** - keeps a verified copy of important Noted data under the user's control | The user creates or updates this backup from Settings. Noted builds and verifies the replacement before switching folders, then keeps the prior user backup as an additional previous copy. A valid user backup can also be exported to one `.notedbackup` file without changing the local backup. Importing and restoring an exported file also leaves the local user backup unchanged. If an existing backup is damaged or contains unexpected files, later backup work stops and leaves it unchanged for review. |
| **Recent backup** - keeps a newer automatic recovery point without replacing the user backup | After a valid user backup exists, Noted may create or update a separate recent backup at most once every 24 hours after a clean, warning-free shutdown. The replacement is built and verified before the older recent copy is retired. |
| **Before-restore backup** - protects the data that a restore is about to replace | After pending edits are flushed, Noted creates and verifies a separate copy of current application and note data before scheduling any full restore. A failed protection step cancels the restore. Noted retains the three newest before-restore copies without changing the user backup. |
| **Backup safety gate** - prevents a recovery problem from becoming trusted backup data | Backups are blocked for the rest of a run when Settings, Checklist, Dictionary, Scratchpad, MiniPad, or the Note Editor used recovery or reported a persistence warning. Automatic backups do not run at startup or during crash recovery. |
| **Saved-note history** - keeps older text after an intentional note save | Before replacing a changed note, Noted saves the previous text as verified JSON under `note-history`. It keeps up to 20 distinct previous versions per note. Saved-note history has no browser or restore button. |
| **Unsaved-change recovery** - protects editor text that has not been saved to the note itself | The editor stores separate recovery drafts for dirty notes. Each note has a path-validated recovery JSON file and a rolling backup. Draft recovery is separate from saved-note history. |
| **Deleted-note retention** - allows recently deleted notes to be recovered manually | Deleted notes are moved to `%LocalAppData%\Noted\DeletedNotes`. Noted removes entries older than 14 days when it starts. Folder deletion remains permanent. |
| **Recovery messages** - tells the user when protection is reduced or recovery occurred | Settings show a recovery notice. Checklist, Dictionary, Scratchpad, MiniPad, and the Note Editor surface persistence or backup warnings through their existing warning UI. Debug output also records expected file failures. |
| **Automated fault tests** - verifies expected behavior under controlled file failures | The project includes 114 service-level tests covering Settings recovery and normalization, shared atomic writes, locked files, rolling backups, corrupt files, Scratchpad and MiniPad recovery, note drafts, editor sessions, saved-note history, atomic note creation, application theme resources, and full-backup creation, preview, export, import, replacement, and storage compatibility. |

## Protection by data type

| Data | Main copy | Additional local protection | Recovery behavior |
| --- | --- | --- | --- |
| Settings and window state | `settings.json` | `settings.json.bak` plus up to 10 history files in `backups` | Validate the main file first; otherwise compare recovery revisions, reject conflicts or newer schemas, preserve the damaged main file, and verify the restored file |
| Checklist items | `checklist.json` | `checklist.json.bak` | Validate the item collection, fall back to `.bak`, preserve invalid JSON, and block unsafe writes |
| Checklist tabs | `checklist-tabs.json` | `checklist-tabs.json.bak` | Same collection recovery rules as Checklist items |
| Dictionary | `dictionary.json` | `dictionary.json.bak` | Validate the collection, fall back to `.bak`, preserve invalid JSON, and block unsafe writes |
| Scratchpad | `scratchpad.rtf` | `scratchpad.rtf.bak` | Read the main content first, use the backup when the main file is missing or unreadable, verify saves exactly, and avoid rotating the backup for duplicate content |
| MiniPad | one singleton JSON recovery file | rolling `.json.bak` | Validate the fixed MiniPad ID, use the backup when needed, preserve invalid JSON, and block unsafe writes |
| Unsaved note changes | one hashed JSON recovery file per note | rolling `.json.bak` for each draft | Validate the saved note path, use the backup when needed, preserve invalid JSON, and block only the affected draft when verification becomes unsafe |
| Editor session | `session\editor-workspace.json` | `session\editor-workspace.json.bak` | Validate the tab list, fall back to `.bak`, preserve invalid JSON, and block unsafe writes |
| Saved note text | the selected `.txt`, `.md`, or `.markdown` file | up to 20 distinct previous versions under `note-history` | Detect outside changes before overwriting, save the previous version to history, atomically replace the note, and verify the new note text exactly |
| New notes | the newly created `.txt` file | unsaved-change recovery begins after the note is opened and edited | Write the complete initial note atomically and read it back before reporting successful creation |
| Deleted notes | moved copy under `DeletedNotes` | 14-day local retention | Retain the moved note until startup cleanup removes expired entries |
| Important Noted data together | current files listed above | one user backup, its previous version, and one recent backup | Copy only verified current data, verify the manifest and hashes, block suspicious data-loss patterns, and never automatically replace the user backup |

## Recovery file names

| Name or suffix | Meaning | Recommended handling |
| --- | --- | --- |
| Main file | The file Noted normally loads and updates | Do not replace it manually while Noted is running |
| `.tmp` | A same-directory file used while preparing an atomic write | A leftover file may show that a write was interrupted, but it is not automatically trusted as recoverable content |
| `.bak` | The immediately previous main-file version | Keep it until the main file has been confirmed healthy |
| `.pending-*` | A previous main file that could not be moved into the normal `.bak` path | Do not delete it without checking the related warning and file contents |
| `.corrupt-*` | An invalid file preserved before recovery continued | Keep it as evidence until the recovered data has been reviewed |
| Settings history file | A time-spaced older Settings state under `backups` | Used only after validation and only when the main Settings file is unusable |
| Note-history entry | A verified JSON record containing an earlier saved-note version | Keep it until Noted has a supported history browser and restore workflow |
| `full-snapshots\protected` | Legacy version-one folder name for the current user backup | The name is retained so existing backups remain readable. Noted replaces it only when the user chooses Update Backup. |
| `full-snapshots\previous` | The prior verified user backup | Noted preserves this while replacing the current user backup. |
| `full-snapshots\latest` | Legacy version-one folder name for the recent automatic backup | The name is retained for compatibility. Noted may update it after a clean shutdown when the backup is due. |
| `SnapshotId`, `Role`, `Protected`, and `Latest` | Legacy version-one manifest names | The source uses `BackupId`, `Type`, `User`, and `Recent`, but still reads and writes the older JSON names so existing backups remain valid. |
| `.building-*` or `.replacing-*` | Evidence that full-backup replacement was interrupted | Noted blocks further backup work and leaves the directory unchanged for review. |
| `.importing-*` or `pending-import-*` | A local copy being prepared from an exported backup, or a verified imported copy waiting to restore | Noted verifies imported data again before restoration. Interrupted build folders block later backup work and remain available for review. A pending import with no matching restore request is temporary cleanup residue and is removed at startup. |
| `before-restore-*` | A verified copy of the current data captured immediately before a full restore | Noted keeps the three newest copies. These are separate from the user and recent backups, so beginning a restore does not replace either of those recovery points. |

## Recovery safety rules

Contributors changing persistence code should preserve these rules:

1. Validate the main file before considering recovery files.
2. Discover and validate recovery files before modifying any of them.
3. Never choose between conflicting Settings revisions by guessing.
4. Never overwrite the only readable recovery file during restoration.
5. Preserve invalid files before allowing replacement data to use the original path.
6. Read back and validate every important write.
7. Treat a main-file failure differently from a rolling-backup warning.
8. Avoid retry loops that repeatedly rotate otherwise healthy backups.
9. Keep feature-specific validation outside the low-level file writer.
10. Add focused failure tests whenever recovery ordering or file replacement changes.

## Full backup protection

The full-backup service keeps two current backup types:

- The user backup changes only when the user chooses Create Backup or Update Backup.
- The recent backup is separate and may update after a clean shutdown when at least 24 hours have passed since the newest valid backup.

When the user backup is updated, its prior verified version is retained as `previous`. New backup content is copied into a private build folder, hashed, and verified before it replaces a current backup folder. If replacement fails, Noted attempts to return the retired folder to its original location and leaves interrupted work for review.

The service copies verified current files rather than `.bak`, `.tmp`, `.pending-*`, `.corrupt-*`, archived recovery evidence, or obsolete MiniPad files. It includes top-level editor recovery drafts and only the active MiniPad singleton. Its manifest records source paths, lengths, hashes, record counts, and content sizes. Backup creation stops when an existing backup is damaged, unfinished backup work exists, important files disappear, protected collections unexpectedly become empty, or several content files appear to lose data together.

Restore is user-initiated. Noted verifies the user backup, records a restore request, closes normally, applies the backup with atomic file writes, and leaves the backup unchanged. A failed restore request remains available for retry instead of being silently discarded.

View Backup verifies the full user backup before listing it. Each backed-up file is compared by length and hash with the exact location that restore would replace, so the preview can mark it as unchanged, changed, missing, or unavailable for comparison. Opening a file checks that the backup ID and file hash still match the verified preview before showing read-only text, formatted JSON, or Scratchpad rich text. Restore performs its own full verification again and does not rely on the preview result.

Export verifies the user backup, writes its manifest and files into one `.notedbackup` archive, and verifies the archive's file list, sizes, and hashes. When replacing an existing export, Noted keeps the old file until the new archive has passed verification. If final verification fails, Noted restores the prior export when one existed and preserves the failed file for review. Export does not update or replace the local user backup.

Import treats the selected `.notedbackup` file as untrusted. Noted rejects unsafe or repeated archive paths, unexpected files, unsupported schemas and data types, excessive file counts or sizes, invalid application JSON, and any file that does not match its declared length and hash. The preview reads directly from the selected file and compares rebased data with this installation's restore locations. No local import folder is created unless the user confirms restoration.

Before any full restore, Noted flushes pending edits and creates a separate verified before-restore backup of the current data. If that copy cannot be completed, Noted cancels the restore and stays open. For an imported restore, Noted also verifies the archive again and copies it into a separate managed folder. Settings, editor tabs, unsaved note recovery, and saved-note history are rebased from the exported notes folder to the current notes folder; recovery filenames and history folders that depend on note-path hashes are rebuilt. The managed copy receives a new backup ID and is verified as a complete local backup before a restore request is written. Restoration uses the same atomic file writes as the user backup, removes the managed copy after success, and never replaces either the local user backup or the selected export file.
