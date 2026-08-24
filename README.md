# Noted.

Noted is a Windows-only .NET 9 WPF utility app. It has a floating Notes button, organizes `.txt`, `.md`, and `.markdown` files in folders, and includes a tabbed editor with recovery and Markdown preview. It also includes Checklist, Dictionary, Scratchpad, and Mini Pad.

## Notes and folders

- Noted supports existing `.txt`, `.md`, and `.markdown` note files. New notes are saved as `.txt`.
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
- Turn Word Wrap on or off from the View menu.
- Press `Ctrl+Shift+V` to toggle Markdown preview.
- Press `Ctrl+S` to save the active note.

## Built-in tools

- **Checklist** saves checklist items locally.
- **Dictionary** saves terms and definitions locally.
- **Scratchpad** is a lightweight rich-text editor that saves its contents locally before it is hidden or closed.
- **Mini Pad** opens one compact plain-text window. It saves a recovery copy locally and reopens after the app exits or crashes.

## Hotkeys and visibility

Default global hotkeys:

| Action | Hotkey |
| --- | --- |
| Toggle Main Workspace | `Ctrl+Shift+Space` |
| Toggle Checklist | `Alt+C` |
| Toggle Dictionary | `Alt+D` |

The workspace hotkey hides Notes, Checklist, Dictionary, Scratchpad, Mini Pad, and the editor when any of them are visible. If they are all hidden, it shows Notes.

By default, the Notes List's Hide button will also hide editor, Checklist, Dictionary, Scratchpad, Mini Pad, and can be adjusted in the settings.
Clicking outside Notes or pressing `Esc` hides Notes and the editor. 


## Download and run

Download the latest `Noted-*-win-x64.zip` from the [Releases](../../releases) page, extract it, and run `Noted.exe`. The ZIP is self-contained, so no installer or separate .NET runtime is needed.

If you have not chosen a notes folder, Noted creates a `Notes` folder beside `Noted.exe`. On startup, it also tries to create a desktop shortcut named `Noted.lnk` and checks GitHub Releases for updates.

> **Windows SmartScreen warning:** Since the app is not code-signed, Windows may show a "Windows protected your PC" dialog the first time it runs. If you downloaded it from this repository and want to continue, select **More info**, then **Run anyway**.

## Settings

Settings include:

- notes folder
- how new notes are created and whether they include a timestamp
- modified-time subtitles and deletion confirmation
- inline or drill-down folders
- global hotkeys
- whether Noted starts with Windows
- whether the Notes Hide button also hides the other tool windows
- whether the previous editor session reopens
- display choice, ghost mode, and opacity

Noted stores its settings, tool window layouts, editor session, Scratchpad content, and recovery data under `%LocalAppData%\Noted`. Notes stay in the folder you choose.

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

## Testing

There is no automated test project yet. Run `dotnet build` to check compilation. Test UI and window behavior manually on Windows.

## Basic use

1. Start Noted.
2. Click the floating Notes button or press `Ctrl+Shift+Space`.
3. Create a note with Prompt, Quick, or Both, depending on your setting.
4. Select a note, then press `Enter` or choose **Open** to open it in the built-in editor.
5. Use folders, search, rename, and delete actions to organize notes.
6. Open Checklist, Dictionary, Scratchpad, or Mini Pad from the Notes toolbar when needed.

## Limitations

- Noted only runs on Windows because it uses WPF and Win32 APIs.
- Only `.txt`, `.md`, and `.markdown` files can be used as notes.
- Settings and recovery data are stored locally. Noted has no built-in cloud sync or backup.

## License

MIT. See `LICENSE`.
