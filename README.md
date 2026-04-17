# MyNotes

MyNotes is a lightweight WPF overlay for managing plain-text notes on Windows. It keeps a small floating "Notes" button on screen, opens a note list on demand, and uses Windows Notepad as the editor for each note.

## What It Does

- Shows a draggable overlay button that stays on top of other windows
- Opens a notes panel with the current `.txt` files in the local `Notes` folder
- Creates new notes with timestamp-based filenames
- Opens the selected note in Notepad
- Renames notes inline with `F2`, double-click, or the context menu
- Deletes the selected note from the panel
- Auto-refreshes the list when note files change on disk
- Hides the notes panel and Notepad when you click outside the panel or press `Esc`

## Tech Stack

- .NET 9
- WPF
- Windows Notepad
- Win32 interop for basic Notepad window show/hide behavior

## Requirements

- Windows
- .NET 9 SDK

## Run The App

From the project root:

```powershell
dotnet build
dotnet run
```

You can also open `MyNotes.sln` in Visual Studio or VS Code and run the WPF project from there.

## How Notes Are Stored

- Notes are stored as plain `.txt` files
- The app creates a `Notes` directory next to the built application if it does not already exist
- New notes are created with names like `Note_2026-04-04_13-45-00.txt`
- The UI formats timestamp-based filenames into a friendlier display name in the list

## Usage

1. Launch the app.
2. Click the floating `Notes` button to open the notes panel.
3. Click `+ New` to create a new note.
4. Select a note to open it in Notepad.
5. Rename a note with `F2`, double-click, or right-click and choose `Rename`.
6. Delete a note with the `Delete` button or the context menu.
7. Press `Esc` or click outside the panel to hide it.

## Project Structure

- `MainWindow.xaml`: overlay UI layout
- `MainWindow.xaml.cs`: overlay behavior, file management, Notepad integration, drag handling, and cleanup
- `Models/NoteItem.cs`: bindable note model used by the list UI
- `App.xaml` and `App.xaml.cs`: WPF app startup

## Notes

- This app does not include a custom text editor. It delegates editing to Windows Notepad.
- Notepad window restore/minimize behavior depends on standard Windows window lookup and is intended for local desktop use.
- The app watches the notes directory and refreshes the file list automatically when note files are created, deleted, or renamed.

## Development

Useful commands:

```powershell
dotnet build
dotnet run
```

Current target framework:

- `net9.0-windows`

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.
