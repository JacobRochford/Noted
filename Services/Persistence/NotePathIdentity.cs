using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Noted.Services;

internal static class NotePathIdentity
{
    internal static string GetHash(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }
}
