# MyNotes

MyNotes is a small Windows note utility built with WPF. It stays out of the way as a floating button, opens a simple note list when you need it, and hands editing off to plain old Notepad.

The app is meant to be quick: open a note, jot something down, hide it again.

## What it does

- Keeps a floating button on screen so your notes are always one click away
- Shows your notes from a folder of plain `.txt` files
- Opens the selected note in Windows Notepad
- Creates notes with either a timestamp-based name or a custom name
- Lets you rename notes inline with `F2`, the Rename button, or the context menu
- Moves deleted notes into a local recycle-style folder instead of removing them immediately
- Refreshes the note list automatically when files change on disk
- Hides the panel and the Notepad window when you click away or press `Esc`

## Settings

The settings view lets you:

- choose a different notes folder
- decide whether new notes should prompt for a name
- add a timestamp to new notes at the top, bottom, or not at all

## Requirements

- Windows
- .NET 9 SDK

## Running The App

From the project root:

```powershell
dotnet build
dotnet run
```

You can also open `MyNotes.sln` in Visual Studio or VS Code and run it there.

## How storage works

- Notes are regular `.txt` files
- By default, the app creates a `Notes` folder next to the built app
- You can switch the notes folder in Settings
- App settings are stored under `%LocalAppData%\MyNotes\settings.json`
- Deleted notes are moved to `%LocalAppData%\MyNotes\DeletedNotes`
- Deleted note files older than 7 days are cleaned up automatically

If a note name matches the built-in timestamp format, the app shows a friendlier date in the list while keeping the real filename on disk.

## Basic use

1. Start the app.
2. Click the floating button to show the notes panel.
3. Click `+ New` to create a note.
4. Click a note or press `Enter` to open it in Notepad.
5. Use `F2` or the row actions to rename or delete a note.
6. Press `Esc` or click outside the panel to hide everything.

## Project layout

- `MainWindow.xaml` and `MainWindow.xaml.cs` handle the overlay UI, interaction, and note workflow
- `ViewModels/MainWindowViewModel.cs` manages the note list and refresh logic
- `Services/NoteFileService.cs` handles file creation, rename, delete, and folder watching
- `Services/AppSettingsService.cs` stores user settings
- `Services/NotepadProcessService.cs` manages launching and hiding Notepad
- `Models/NoteItem.cs` represents items shown in the note list

## Notes

- This project uses Notepad as the editor. It does not include a custom text editor.
- The app is Windows-only because it depends on WPF and basic Win32 window handling.
- When you change note folders, the app may ask you to finish with the currently open note first.

## License

MIT. See `LICENSE`.
