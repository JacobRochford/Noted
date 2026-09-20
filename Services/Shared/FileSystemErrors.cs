using System.IO;
using System.Security;

namespace Noted.Services;

internal static class FileSystemErrors
{
    // Exceptions that can normally occur while accessing the file system.
    // Invalid arguments are programming/input errors
    internal static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}
