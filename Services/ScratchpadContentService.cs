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

        try
        {
            FileWriter.WriteAllText(_contentFilePath, content);
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
    }
}
