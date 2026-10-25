# Noted.

Noted is a Windows-only .NET 9 WPF utility app. It has a floating Notes button, organizes [supported text-based files](#supported-file-types) in folders, and includes a tabbed editor with recovery and Markdown preview. It also includes Checklist, Dictionary, Jots, Quick Jot, Scratchpad, and MiniPad.

## Notes and folders

- Noted supports existing text-based note files with [supported extensions](#supported-file-types). New notes are saved as `.txt`.
- Notes can be organized in subfolders.
- Folders can expand in place or open in a drill-down view.
- The drill-down view keeps back and forward folder history.
- Notes can be renamed inline, and the list refreshes when files change on disk.
- Deleted notes go to `%LocalAppData%\Noted\DeletedNotes`. At startup, Noted attempts to permanently remove notes deleted more than 14 days ago.
- Folder deletion is permanent and includes the folder's contents.

Choose how new notes are created:

- **Prompt** asks for a note name before creating it.
- **Quick** immediately creates a timestamp-named note.
- **Both** uses prompted creation on the New button and adds a separate Quick Note button.

## Editor

- Notes open in the built-in tabbed editor.
- Noted saves recovery copies of unsaved changes after a short delay. Session reopening can be turned off in Settings without disabling unsaved-text recovery.
- Use `Ctrl+F` to find text and `Ctrl+H` to find and replace. Search highlights all matches and supports case-sensitive matching, whole-word matching, and regular expressions.
- The View menu controls line numbers, scroll speed, Word Wrap, Markdown preview, tab visibility, and whether tabs reopen on startup.
- Press `Ctrl+Shift+V` to toggle Markdown preview.
- Press `Ctrl+S` to save the active note.
- Use Format JSON or Minify JSON from the Edit menu to format JSON text.

## Built-in tools

- **Checklist** saves checklist items locally.
- **Dictionary** saves terms and definitions locally.
- **Jots** saves short text entries locally, with a collection window and separate windows for individual jots.
- **Quick Jot** opens a capture window with `Alt+J`. Press `Enter` to save to Jots or `Shift+Enter` to insert a newline. Unfinished text is saved locally for recovery.
- **Scratchpad** is a lightweight rich-text editor that saves its contents locally before it is hidden or closed.
- **MiniPad** supports up to five independent compact plain-text windows with find and Word Wrap controls. Each saves its content locally, and windows marked to reopen are restored on startup.

## Hotkeys and visibility

Default hotkeys:

| Action | Hotkey |
| --- | --- |
| Show or hide Noted | `Alt+N` |
| Show or hide Checklist | `Alt+C` |
| Show or hide Dictionary | `Alt+D` |
| Show MiniPad | `Alt+M` |
| Show or hide Note Editor | `Alt+E` |
| Open Quick Jot | `Alt+J` |
| Show or hide Scratchpad | `Alt+S` |
| Show or hide Jots collection | `Alt+Shift+J` |

These shortcuts can be changed or disabled in Settings. If a shortcut is unavailable, Noted may use a fallback for that session and show a warning.

The Notes hotkey hides Notes, Checklist, Dictionary, the Jots collection and individual jot windows, Scratchpad, MiniPads, and the editor together. Pressing it again restores the same windows that were visible before they were hidden.

The MiniPad hotkey shows the most recently active MiniPad or creates one if none is open. Use New MiniPad in a MiniPad window to open another.

By default, the Notes List's Hide button hides the same group of windows. This can be changed in Settings.
Clicking the overlay outside the Notes panel and floating button, or pressing `Esc` while the Notes panel has keyboard focus, hides Notes and the editor.
The notification-area icon provides Show/Hide Notes, Restore Window Positions, Settings, and Exit commands.


## Download and run

When a packaged build is available, download the latest `Noted-*-win-x64.zip` from the [Releases](../../releases) page, extract it, and run `Noted.exe`. The ZIP is self-contained, so no installer or separate .NET runtime is needed. Until then, use the instructions under **Running from source**.

If you have not chosen a notes folder, Noted creates a `Notes` folder beside `Noted.exe`. On startup, it also tries to create a desktop shortcut named `Noted.lnk` and checks GitHub Releases for updates.

> **Windows SmartScreen warning:** Since the app is not code-signed, Windows may show a "Windows protected your PC" dialog the first time it runs. If you downloaded it from this repository and want to continue, select **More info**, then **Run anyway**.

## Settings

Settings include:

- notes folder
- how new notes are created and whether they include a timestamp
- modified-time subtitles and deletion confirmation
- inline or drill-down folders
- global hotkeys for Notes, Checklist, Dictionary, MiniPad, Note Editor, Quick Jot, Scratchpad, and Jots
- whether Noted starts with Windows
- whether the Notes Hide button also hides the other windows
- whether the previous editor session reopens
- display choice, ghost mode, and opacity
- local backup creation, preview, restore, export, and import

Noted stores its settings, window layouts, editor session, Checklist, Dictionary, Jots, Scratchpad content, recovery data, and local backups under `%LocalAppData%\Noted`. Notes stay in the folder you choose. Eligible user backups can be exported as `.notedbackup` files to an external destination and imported from Settings.

Full backups include application-managed content, registered MiniPads (including closed instances), retained recovery drafts, saved-note history, deleted notes, supported files in `.archive`, and saved documents open in the freshly prepared editor session within the configured notes folder. Other files in that folder are not collected automatically. Saved files and unsaved recovery drafts are separate backup entries. Open documents outside the notes folder retain their session and recovery data, but their saved files are excluded. See [backup and recovery](docs/backup-and-recovery-information.md) for restore behavior and portability limits.

## Requirements

**Published package:** A 64-bit Windows version supported by .NET 9. No separate .NET runtime is required.

**Build from source:** Windows with the .NET 9 SDK.

## Running from source

From the repository root:

```powershell
dotnet build
dotnet run
```

You can also open `Noted.sln` in Visual Studio 2022 17.12 or later.

## Project organization

Noted uses one WPF application project and one MSTest project. `App.xaml.cs` composes the application and coordinates startup. Runtime responsibilities are separated into focused areas:

- `Views` contains WPF windows, dialogs, and interaction code; `ViewModels` contains presentation state for the Notes list, Settings, Checklist, Dictionary, Jots, Quick Jot, and Scratchpad.
- `Models` groups persisted state and domain models for notes, settings, windows, Checklist, Dictionary, and Jots.
- `Services` is organized by responsibility: application lifecycle, backup and restore, built-in tool content, notes and editor persistence, shared persistence primitives, settings, and window integration.
- `Search` contains the shared text-search session, presenter, viewport rules, and highlight overlay used by searchable editors.
- `Helpers` contains focused WPF and formatting helpers. `tests/Noted.Tests` mirrors these areas for automated tests.

The main windows retain WPF-specific interaction behavior. File operations, navigation and directory watching, editor workspace and persistence, settings, backup archives and restore, hotkeys, shutdown sequencing, and window ownership are handled by their corresponding models and services.

## Testing

Run the automated tests with:

```powershell
dotnet test tests/Noted.Tests/Noted.Tests.csproj
```

## Basic use

1. Start Noted.
2. Click the floating Notes button or press `Alt+N` with the default hotkeys.
3. Create a note with Prompt, Quick, or Both, depending on your setting.
4. Select a note, then press `Enter` or choose **Open** to open it in the built-in editor.
5. Use folders, search, rename, and delete actions to organize notes.
6. Open Checklist, Dictionary, Jots, Scratchpad, or MiniPad from the utility menu when needed. Press `Alt+J` to capture a Quick Jot with the default hotkeys.

## Limitations

- Noted only runs on Windows because it uses WPF and Win32 APIs.
- Only text-based files with [supported extensions](#supported-file-types) can be used as notes.
- Settings, recovery data, and automatic backups are stored locally. Noted has no built-in cloud sync or automatic off-device backup; exported `.notedbackup` files can be stored elsewhere.

## License

MIT. See `LICENSE`.

## Supported file types

Noted recognizes these file extensions, case-insensitively:

`.txt`, `.md`, `.markdown`, `.log`, `.csv`, `.tsv`, `.json`,
`.xml`, `.yaml`, `.yml`, `.toml`, `.ini`, `.cfg`, `.conf`,
`.sql`, `.cs`, `.xaml`, `.html`, `.htm`, `.css`, `.js`,
`.ts`, `.py`, `.ps1`, `.bat`, `.cmd`.

The authoritative list is [NoteFileExtensions.SupportedExtensions](Services/Notes/NoteFileExtensions.cs).

These extensions provide text editing support. Noted does not execute code. It includes Markdown preview and JSON formatting and minification.
