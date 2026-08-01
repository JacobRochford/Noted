using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

internal enum JsonFileReadStatus
{
    Success,
    Missing,
    Corrupt,
    Unavailable
}

internal sealed record JsonFileReadResult<T>(
    JsonFileReadStatus Status,
    T? Value,
    Exception? Error)
{
    internal bool Success => Status == JsonFileReadStatus.Success;
}

internal static class JsonFileStore
{
    internal static JsonFileReadResult<T> Read<T>(
        string path,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var json = File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8);
            var value = JsonSerializer.Deserialize<T>(json, options);
            if (value is null)
            {
                return new JsonFileReadResult<T>(
                    JsonFileReadStatus.Corrupt,
                    default,
                    new JsonException("The JSON file contains no value."));
            }

            return new JsonFileReadResult<T>(JsonFileReadStatus.Success, value, null);
        }
        catch (FileNotFoundException)
        {
            return new JsonFileReadResult<T>(JsonFileReadStatus.Missing, default, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new JsonFileReadResult<T>(JsonFileReadStatus.Missing, default, null);
        }
        catch (JsonException ex)
        {
            return new JsonFileReadResult<T>(JsonFileReadStatus.Corrupt, default, ex);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return new JsonFileReadResult<T>(JsonFileReadStatus.Unavailable, default, ex);
        }
    }

    internal static void Write<T>(
        string path,
        T value,
        string? backupPath = null,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        var json = JsonSerializer.Serialize(value, options);
        FileWriter.WriteAllText(path, json, backupPath);
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
    }
}
