# Backup and recovery

This document explains what Noted backs up, how recovery works, and the limits of each operation. Unless otherwise stated, application-data paths are relative to `%LocalAppData%\Noted`. Local backups are stored on the same machine. For an off-device copy, export an eligible backup and store it on another device or remote storage.

## What full backups include

Noted collects Settings, Checklist items and tabs, Dictionary, Jots, the Quick Jot draft, Scratchpad, the editor session, top-level editor recovery drafts, saved-note history, and retained deleted notes. It includes existing primary MiniPad drafts registered in Settings, including closed instances. Unregistered MiniPad drafts are not collected.

Saved documents are selected from every tab in the freshly prepared Note Editor session, including inactive tabs and both panes, and must be inside the configured notes folder. Supported files in the dedicated `.archive` directory are also collected recursively, whether open or closed. The rest of the notes folder is not scanned: unrelated closed documents and other user-managed files are excluded. An empty session still includes application-managed data and archived files.

Each selected saved file contains the bytes currently on disk, including external edits; unsaved editor text remains in a separate recovery draft. Open documents outside the notes folder retain their session data and any retained recovery drafts and saved-note history, but their saved files are excluded with a backup warning. Closing a tab excludes its saved file from future backups unless it is in `.archive`; retained recovery drafts and saved-note history remain eligible independently.

Settings-history files, rolling `.bak` files, `.tmp`, `.pending-*`, `.corrupt-*`, archived recovery evidence, and obsolete MiniPad storage are normally excluded. Quick Jot is an exception: when its primary file is missing, collection can use its validated `.bak`.

## User and recent backups

- The user backup changes only when you choose Create Backup or Update Backup in Settings.
- The recent backup is separate and may update during a normal shutdown after pending saves and window preparation succeed. A valid user backup must exist. At least 24 hours must have passed since the recent backup, or since the user backup if no recent backup exists.
- At most one user or recent backup is created per application run. Before-restore backups are separate from that limit.
- No replacement is created when that backup already matches the selected data. Creation also skips when there is no data to collect.

When the user backup is updated, its prior verified version is normally retained as `previous`. A new backup is built and verified before it replaces the current copy. If replacement fails, Noted attempts to return the retired folder to its original location and leaves interrupted work for review. If filing the previous copy or removing retired copies fails, the new backup can still succeed with a warning. Extra folders may remain and block later backup creation.

### When creation is blocked

Both manual and automatic backups flush pending content and check current save and recovery warnings. Automatic backups additionally check recovery problems recorded during the run. Manual backups do not apply that general run-history check, but the Note Editor blocks them while recovered notes remain unresolved or if it recorded a recovery problem during the current run. Automatic backups do not run at startup or during crash recovery.

Creation stops if the current user or recent backup is invalid, leaving that backup unchanged. Unfinished backup work also blocks creation. A recent backup without its expected user backup blocks creation of a replacement user backup.

Missing or unreadable selected documents, invalid session data, an unavailable notes folder, and archive read failures stop collection before replacing an existing backup. When updating a backup, Noted also checks for previously backed-up application files that are now missing, newly empty Checklist, Dictionary, or Jots collections, and large reductions in retained content. Intentional deletions can trigger these checks. They do not guarantee detection of every lost recovery, history, MiniPad, or deleted-note file.

Saved documents no longer selected are excluded from these comparisons, except files in `.archive`. Closing tabs alone therefore does not count as lost saved documents. Missing application data, including the session file, is still checked even when collection is empty.

## Export and import

Export verifies the user backup, writes it into one `.notedbackup` archive, and applies the same archive and content eligibility checks used by import before publishing it. Choose an existing export folder outside Noted's local backup folder. Export does not update or replace the local user backup.

A valid local backup may still be ineligible for export. Session, recovery, or saved-note history references outside the recorded notes folder prevent export and import; they are not silently dropped. Archive size limits and unsupported content can also prevent export.

Import treats the selected archive as untrusted. Noted rejects unsafe or repeated paths, unexpected files, unsupported schemas and data types, invalid application JSON, and files that do not match their declared size and hash. Export and import share these limits:

| Limit | Maximum |
| --- | --- |
| Payload files | 10,000 |
| Manifest | 4 MiB |
| Individual `.json` file, including JSON notes | 16 MiB |
| Other individual files | 256 MiB |
| Combined uncompressed payload | 2 GiB |

When replacing an existing export, Noted keeps the old file until the new archive has passed verification. If final verification fails, Noted attempts to restore the prior export and preserve the failed file for review.

Import preview reads directly from the selected archive and compares data after redirecting supported note paths to this installation's notes folder. No managed import folder is created unless you confirm restoration. During preparation, Noted verifies the archive again, prepares a separate local copy with the updated paths, and verifies it before recording the restore request. After successful restoration, Noted attempts to remove that copy and warns if cleanup fails. Neither import nor restoration replaces the local user backup or the selected archive.

Successful export does not guarantee restoration on every installation. Destination access, path restrictions, before-restore protection, and later file changes can still prevent restoration.

## Preview and restoration

View Backup verifies the full user backup before listing it. Each backed-up file is compared with its restore destination and marked unchanged, changed, missing, or unavailable for comparison. Opening a file rechecks its identity and content before displaying it. Files larger than 4 MiB cannot be displayed. Comparisons reflect current content; they do not guarantee successful restoration or override Quick Jot conflict handling. Restore verifies the backup again.

### Before restoring

After pending saves and window preparation succeed, Noted creates and verifies a separate before-restore backup of current data before scheduling restoration. If there is no current data to protect, this step is skipped. If protection fails, Noted cancels the restore and stays open. It attempts to keep the three newest before-restore copies; cleanup failures leave extra copies and report a warning. These copies do not replace the user or recent backup.

Protection also captures existing files the incoming backup could overwrite, including closed documents in older backups and application files outside normal collection. Unreadable destinations stop preparation. If a local backup targets an older notes folder, protection covers the overwritten documents there together with current application-managed data; saved documents in the different current notes folder are not overwritten or included in that copy.

### What restoration changes

Restore is user-initiated. Noted records a restore request, closes normally, applies the backup, and leaves the source backup unchanged. Open Noted again to load the restored data.

Restoration generally replaces the files listed in the backup, including settings and collections as whole files, subject to the Quick Jot conflict policy. Files absent from it are generally left untouched, so existing recovery and history files can coexist with restored files. Local restores use the backup's recorded notes folder. Imported restores use the current notes folder and redirect supported editor-session, recovery, and history references to it. Older backups retain their original contents and scope, so they can restore more documents than newly created backups.

Restoration is performed file by file with atomic writes and verification. An I/O failure can leave some files restored before the failure; files already restored are not automatically rolled back. The pending request remains for retry before normal data loading on the next launch. If that retry fails, normal startup is stopped.

### Quick Jot restore policy

Direct and imported restores use the same Quick Jot conflict policy. If the local draft has nonempty text and differs from the incoming draft, Noted keeps the local draft. A different nonempty incoming draft is saved and verified under `full-snapshots\quick-jot-conflicts` before other backup files are restored. The completed restore reports a warning so you can review both copies. Noted does not automatically merge the drafts.

If the local draft is empty or identical to the incoming draft, restoration may replace it. Restoring an empty draft also clears its rolling fallback so previously saved text is not recovered later. Backups created before Quick Jot drafts were included remain supported; when a backup has no draft entry, restore leaves the local draft unchanged.

## Unsaved note recovery and history

The editor stores separate recovery drafts for notes with unsaved changes. Draft writes happen after a short delay, so the latest edits may be lost if Noted stops before they are written. Turning off session reopening does not disable recovery of unsaved text. A missing original file can still reopen from a draft; missing files without drafts are skipped.

If an existing original note cannot be read, its draft remains preserved but may not open automatically. Resolve the reported file-access or encoding problem and restart Noted to retry. Recovery drafts are removed after saving or explicitly discarding changes, and drafts matching the saved text are removed during recovery. Cleanup failures are reported. Drafts are not a history of discarded edits.

Before replacing a changed note, Noted attempts to save the previous text under `note-history`. If history creation fails with an expected file error, the note save continues and reports a warning. Noted normally retains up to 20 distinct previous text versions per file path; cleanup failures can leave extras. Earlier history remains associated with the old path after a rename or move. Saved-note history has no browser or restore button and requires manual inspection.

Ordinary editor saves check for outside changes and refuse conflicting overwrites. Saved notes and newly created notes are written atomically and read back for verification.

Individual deleted notes are moved to `DeletedNotes` and can be recovered manually. At startup, Noted attempts to permanently remove retained notes deleted more than 14 days ago. Files that cannot be deleted, or whose deletion timestamp cannot be recognized, remain. Folder deletion is permanent and includes its contents.

## File recovery behavior

Most application data has a rolling `.bak` containing the previous file version. Writes use a temporary file in the destination directory, flush it to disk, and atomically replace the main file. Saved content is read back for verification. If updating the rolling backup fails after a successful save, Noted reports a warning and attempts to retain the previous content under a `.pending-*` name.

Checklist, Dictionary, Jots, Quick Jot, MiniPad, editor sessions, and note recovery drafts try the main file first, then a validated rolling backup. Damaged JSON is preserved under a separate name where possible. If neither copy is usable but damaged files can be preserved, the affected feature can start empty, report the problem, and allow new saves. If existing files cannot safely be read or preserved and no usable fallback exists, saving is blocked. Write-verification failures can also block saving. Resolve the reported problem and restart Noted when writes are blocked; note drafts and MiniPads are blocked independently.

When a valid fallback is loaded but the main file could not be read or preserved, the next save keeps that fallback instead of replacing it. Quick Jot clears its fallback before completing a draft clear, so cleared text does not return through backup recovery.

Scratchpad uses its backup when the main file is missing or unreadable. If the main file is readable but its rich-text formatting cannot load, Scratchpad opens the saved content as plain text and warns you instead of trying the backup.

### Settings recovery

A valid main Settings file wins. Missing or corrupt Settings can be recovered from validated `settings.json.bak`, `settings.json.bak.pending-*`, and history files. An unreadable main file or a newer unsupported main schema blocks loading instead of falling back. When recovery is needed, unreadable or newer-schema candidates and conflicting eligible recovery states also block it.

The highest saved revision determines the eligible recovery state. Timestamps only order equivalent candidates at that revision. If all candidates have revision zero, filesystem time determines selection. Damaged main Settings are preserved as `settings.corrupt_<timestamp>.json` during recovery.

Settings normally retain up to 10 valid, time-spaced history files under `backups`. Invalid history files are not pruned by that limit, and cleanup failures can leave extras. Settings report a recovery notice; the other tools report recovery and save problems through their warning UI.

## Storage reference

| Data | Main copy | Additional local protection |
| --- | --- | --- |
| Settings and window state | `settings.json` | `.bak` plus Settings history in `backups` |
| Checklist items and tabs | `checklist.json`, `checklist-tabs.json` | `.bak` for each file |
| Dictionary | `dictionary.json` | `.bak` |
| Jots | `jots.json` | `.bak` |
| Quick Jot draft | `jot-capture-draft.json` | `.bak` |
| Scratchpad | `scratchpad.rtf` | `.bak` |
| MiniPad | one file per instance under `recovery\minipads` | `.json.bak` for each instance |
| Unsaved note changes | one file per note under `recovery` | `.json.bak` for each draft |
| Editor session | `session\editor-workspace.json` | `.bak` |
| Saved note text | the selected [supported text file](../README.md#supported-file-types) | previous text under `note-history` |
| Deleted notes | moved copy under `DeletedNotes` | attempted cleanup after 14 days |

### Recovery file names

| Name or suffix | Meaning | Recommended handling |
| --- | --- | --- |
| Main file | The file Noted normally loads and updates | Do not replace it manually while Noted is running |
| `.tmp` | A temporary file used while preparing a write | A leftover file is not automatically trusted as recoverable content |
| `.bak` | Normally the immediately previous main-file version | Keep it until the main file has been confirmed healthy |
| `.pending-*` | Previous content that could not be moved into its normal backup path | Check the related warning and contents before deleting it |
| `.corrupt-*` or `settings.corrupt_*.json` | Damaged content preserved during recovery | Review it before deleting it manually; editor draft cleanup can remove related corrupt copies |
| Settings history file | An older Settings state under `backups` | A recovery candidate when main Settings are missing or corrupt |
| Note-history entry | Earlier saved-note text | Recovery currently requires manual inspection |
| `full-snapshots\protected` | The current user backup | The legacy folder name is retained for compatibility |
| `full-snapshots\previous` | The prior user backup | Normally replaced when the user backup is updated |
| `full-snapshots\latest` | The recent automatic backup | The legacy folder name is retained for compatibility |
| `.building-*`, `.replacing-*`, or `.failed-*` | Unfinished backup work | Can block further backup creation; review the warning before changing these folders |
| `.importing-*` or `pending-import-*` | An import being prepared or waiting to restore | Interrupted work can block backups; pending imports without a matching restore request are eligible for cleanup |
| `full-snapshots\quick-jot-conflicts` | Incoming Quick Jot drafts retained during a restore conflict | Review alongside the local draft; text is not merged automatically |
| `before-restore-*` | Current data protected before a restore | Separate from the user and recent backups; retention normally keeps three copies |
