# Changelog

All notable changes to this project will be documented in this file.

## 1.0.0 - 2026-04-18

### Note management

- Create notes with an auto-generated timestamp filename or a custom name at creation time
- Notes are stored as plain `.txt` files and can be opened or edited with external tools
- Note list is sorted by last-modified time (newest first)
- Timestamp-named notes display a friendly date in the UI (e.g. `Apr 18, 2026 3:05:00 PM`) while retaining the original filename on disk
- Custom-named notes display last-modified time as a subtitle
- Rename notes inline using `F2`, the Rename button, or the context menu
- Windows reserved device names (`CON`, `NUL`, `COM1`, etc.) are blocked during rename
- Deleted notes are moved to a local recycle folder instead of being permanently removed
- Deleted notes older than 7 days are automatically cleaned up on startup

### Editor integration

- Opens selected notes in Windows Notepad
- Ensures only one Notepad instance is managed per note
- Hides Notepad when the notes panel is dismissed and restores it when reopened
- Prompts the user to close the current note before switching folders

### UI and interaction

- Floating button provides quick access to notes at all times
- Notes panel displays a count when notes exist
- Panel can be dismissed via `Esc` or clicking outside
- Note list updates automatically within ~200ms of filesystem changes

### Settings

- Custom notes folder support (default is a `Notes` folder next to the app)
- Toggle for prompting note naming on creation
- Control over timestamp placement (top, bottom, or none)
- Settings persisted to `%LocalAppData%\MyNotes\settings.json`
- Deleted notes stored under `%LocalAppData%\MyNotes\DeletedNotes`