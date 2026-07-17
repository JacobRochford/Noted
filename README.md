# Noted.

Noted is a Windows-only .NET 9 WPF notes utility. It provides a floating Notes button, manages plain `.txt` notes and folders, and opens notes for editing in Windows Notepad. Checklist, Dictionary, and Scratchpad overlays provide related tools without replacing the plain-text note workflow.

## Notes and folders

- Notes are regular `.txt` files in the configured notes directory.
- Notes can be organized in subfolders.
- Folders can either expand inline or open using drill-down navigation.
- Drill-down navigation maintains back and forward folder history.
- Notes can be renamed inline and are refreshed when files change on disk.
- Deleted notes are moved to `%LocalAppData%\Noted\DeletedNotes` and retained for 14 days.
- Folder deletion is permanent and includes the folder's contents.

New-note behavior is configurable:

- **Prompt** asks for a note name before creating it.
- **Quick** immediately creates a timestamp-named note.
- **Both** keeps prompted creation on the New button and also shows a separate Quick Note button.

## Auxiliary overlays

- **Checklist** maintains locally persisted checklist items.
- **Dictionary** maintains locally persisted term and definition entries.
- **Scratchpad** provides a lightweight rich-text workspace. Its content is persisted locally and pending changes are flushed when the window is hidden or closed.

Auxiliary windows are created when needed and do not appear automatically at startup.

## Hotkeys and visibility

Default global hotkeys:

| Action | Hotkey |
| --- | --- |
| Toggle the managed workspace | `Ctrl+Shift+Space` |
| Toggle Checklist | `Alt+C` |
| Toggle Dictionary | `Alt+D` |

The workspace hotkey hides all visible managed windows. If every managed window is hidden, it shows Notes.

The Notes Hide button has a setting that also hides Checklist, Dictionary, and Scratchpad. This setting is enabled by default. Clicking outside Notes or pressing `Esc` hides the Notes panel and Notepad, but does not necessarily hide an auxiliary window.

## Download and running

Download the latest `Noted-*-win-x64.zip` from the [Releases](../../releases) page, extract it, and run `Noted.exe`. The published package is self-contained, so it does not require a separate .NET installation or installer.

> **Windows SmartScreen warning:** Because the app is not code-signed, Windows may show a "Windows protected your PC" dialog the first time it runs. Select **More info**, then **Run anyway**, to continue.

## Settings

Settings include:

- notes directory
- note-creation mode and timestamp placement
- inline or drill-down folder navigation
- global hotkeys
- launch when Windows starts
- whether the Notes Hide button also hides auxiliary windows
- overlay opacity and related window behavior

Settings and auxiliary-window state are stored under `%LocalAppData%\Noted`. Scratchpad content is also stored locally.

## Requirements

**Published package:** Windows 10 or 11, x64. No separate .NET runtime is required.

**Build from source:** Windows and the .NET 9 SDK.

## Running from source

From the project root:

```powershell
dotnet build
dotnet run
```

You can also open `Noted.sln` in Visual Studio or VS Code and run the project there.

## Basic use

1. Start Noted.
2. Click the floating Notes button or press `Ctrl+Shift+Space`.
3. Create a note using the configured Prompt, Quick, or Both workflow.
4. Select a note or press `Enter` to open it in Notepad.
5. Use folders, search, rename, and delete actions to organize notes.
6. Open Checklist, Dictionary, or Scratchpad from the Notes toolbar when needed.

## Current limitations

- Noted is Windows-only because it uses WPF and Win32 window behavior.
- Notes are plain `.txt` files only. Markdown is not supported.
- Windows Notepad is the note editor.
- Data is stored locally. Cloud synchronization and cloud backup are not provided.
- Folder deletion is permanent.

## License

MIT. See `LICENSE`.
