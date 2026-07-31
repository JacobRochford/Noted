namespace Noted.Models;

/// Controls how clicking a folder item navigates.
public enum FolderNavigationMode
{
    DrillDown,  // replace list with folder's contents (default)
    Expand      // expand/collapse inline within the root list
}
