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

internal sealed class FileVerificationException : IOException
{
    internal FileVerificationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
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

    internal static FileWriteResult Write<T>(
        string path,
        T value,
        string? backupPath = null,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        var json = JsonSerializer.Serialize(value, options);
        var writeResult = FileWriter.WriteAllText(path, json, backupPath);

        string persistedJson;
        try
        {
            persistedJson = File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            throw new FileVerificationException(
                $"The JSON file at '{path}' was written but could not be read back for verification.",
                ex);
        }

        if (!string.Equals(persistedJson, json, StringComparison.Ordinal))
        {
            throw new FileVerificationException(
                $"The JSON file at '{path}' did not match the data that was written.");
        }

        var verification = Read<T>(path, options);
        if (!verification.Success)
        {
            throw new FileVerificationException(
                $"The JSON file at '{path}' was written but could not be validated.",
                verification.Error);
        }

        return writeResult;
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
