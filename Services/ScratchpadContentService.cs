using System.IO;
using System.Security;
using System.Text;

namespace Noted.Services;

public sealed class ScratchpadContentService : IScratchpadContentService
{
    private readonly string _contentFilePath;

    public ScratchpadContentService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _contentFilePath = Path.Combine(storageDirectory, "scratchpad.rtf");
    }

    public (bool Success, bool Exists, string? Content, string? Error) TryLoadContent()
    {
        try
        {
            var content = File.ReadAllText(_contentFilePath, Encoding.UTF8);
            return (true, true, content, null);
        }
        catch (FileNotFoundException)
        {
            return (true, false, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (true, false, null, null);
        }
        catch (IOException ex)
        {
            return (false, false, null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, false, null, ex.Message);
        }
        catch (SecurityException ex)
        {
            return (false, false, null, ex.Message);
        }
    }

    public (bool Success, string? Error) TrySaveContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(_contentFilePath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_contentFilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_contentFilePath))
                File.Replace(temporaryPath, _contentFilePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _contentFilePath);

            return (true, null);
        }
        catch (IOException ex)
        {
            return (false, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, ex.Message);
        }
        catch (SecurityException ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }
}
