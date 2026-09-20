# Noted.

Noted is a Windows-only .NET 9 WPF utility app. It has a floating Notes button, organizes [supported text-based files](#supported-file-types) in folders, and includes a tabbed editor with recovery and Markdown preview. It also includes Checklist, Dictionary, Scratchpad, and MiniPad.

## Notes and folders

- Noted supports existing text-based note files with [supported extensions](#supported-file-types). New notes are saved as `.txt`.
- Notes can be organized in subfolders.
- Folders can expand in place or open in a drill-down view.
- The drill-down view keeps back and forward folder history.
- Notes can be renamed inline, and the list refreshes when files change on disk.
- Deleted notes go to `%LocalAppData%\Noted\DeletedNotes`. Noted permanently removes any that are more than 14 days old when it starts.
- Folder deletion is permanent and includes the folder's contents.

Choose how new notes are created:

- **Prompt** asks for a note name before creating it.
- **Quick** immediately creates a timestamp-named note.
- **Both** uses prompted creation on the New button and adds a separate Quick Note button.

## Editor

- Notes open in the built-in tabbed editor.
- Noted saves recovery copies of unsaved changes. Restoring the previous editor session can be turned off in Settings.
- Use `Ctrl+F` to find text and `Ctrl+H` to find and replace. Search highlights all matches and supports case-sensitive matching.
- The View menu controls line numbers, scroll speed, Word Wrap, Markdown preview, tab visibility, and whether tabs reopen on startup.
- Press `Ctrl+Shift+V` to toggle Markdown preview.
- Press `Ctrl+S` to save the active note.

## Built-in tools

- **Checklist** saves checklist items locally.
- **Dictionary** saves terms and definitions locally.
- **Scratchpad** is a lightweight rich-text editor that saves its contents locally before it is hidden or closed.
- **MiniPad** opens one compact plain-text window with find and Word Wrap controls. It saves its content locally and restores its previous visibility on startup.

## Hotkeys and visibility

Default hotkeys:

| Action | Hotkey |
| --- | --- |
| Show or hide Noted | `Ctrl+Shift+Space` |
| Show or hide Checklist | `Alt+C` |
| Show or hide Dictionary | `Alt+D` |

The Notes hotkey hides Notes, Checklist, Dictionary, Scratchpad, MiniPad, and the editor together. Pressing it again restores the same windows that were visible before they were hidden.

By default, the Notes List's Hide button also hides the editor, Checklist, Dictionary, Scratchpad, and MiniPad. This can be changed in Settings.
Clicking outside Notes or pressing `Esc` hides Notes and the editor. 
The notification-area icon provides Show/Hide Notes, Settings, and Exit commands.


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
- Notes, Checklist, and Dictionary hotkeys
- whether Noted starts with Windows
- whether the Notes Hide button also hides the other windows
- whether the previous editor session reopens
- display choice, ghost mode, and opacity

Noted stores its settings, window layouts, editor session, Scratchpad content, recovery data, and local backups under `%LocalAppData%\Noted`. Notes stay in the folder you choose.

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

- `Views` contains WPF windows, dialogs, and interaction code; `ViewModels` contains presentation state for the Notes list, Settings, Checklist, Dictionary, and Scratchpad.
- `Models` groups persisted state and domain models for notes, settings, windows, Checklist, and Dictionary.
- `Services` is organized by responsibility: application lifecycle, backup and restore, built-in tool content, notes and editor persistence, shared persistence primitives, settings, and window integration.
- `Search` contains the shared text-search session, presenter, viewport rules, and highlight overlay used by searchable editors.
- `Helpers` contains focused WPF and formatting helpers. `tests/Noted.Tests` mirrors these areas for automated tests.

The main windows retain WPF-specific interaction behavior. File operations, navigation and directory watching, editor workspace and persistence, settings, backup archives and restore, hotkeys, shutdown sequencing, and window ownership are handled by their corresponding models and services.

## Testing

Run the automated tests with:

```powershell
dotnet test tests/Noted.Tests/Noted.Tests.csproj
```

The project currently has 356 automated test cases. UI and window behavior still require manual testing on Windows.

## Basic use

1. Start Noted.
2. Click the floating Notes button or press `Ctrl+Shift+Space`.
3. Create a note with Prompt, Quick, or Both, depending on your setting.
4. Select a note, then press `Enter` or choose **Open** to open it in the built-in editor.
5. Use folders, search, rename, and delete actions to organize notes.
6. Open Checklist, Dictionary, Scratchpad, or MiniPad from the utility menu when needed.

## Limitations

- Noted only runs on Windows because it uses WPF and Win32 APIs.
- Only text-based files with [supported extensions](#supported-file-types) can be used as notes.
- Settings, recovery data, and backups are local to the same computer. Noted has no cloud sync or off-device backup.

## License

MIT. See `LICENSE`.

## Supported file types

Noted recognizes these file extensions, case-insensitively:

`.txt`, `.md`, `.markdown`, `.log`, `.csv`, `.tsv`, `.json`,
`.xml`, `.yaml`, `.yml`, `.toml`, `.ini`, `.cfg`, `.conf`,
`.sql`, `.cs`, `.xaml`, `.html`, `.htm`, `.css`, `.js`,
`.ts`, `.py`, `.ps1`, `.bat`, `.cmd`.

The authoritative list is [NoteFileExtensions.SupportedExtensions](Services/Notes/NoteFileExtensions.cs).

These extensions provide text editing support. Noted does not execute code or provide format-specific tooling for them.
