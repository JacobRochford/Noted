using System.IO;
using System.Text;
using Noted.Models;

namespace Noted.Services;

internal sealed class TextEncodingException(string message, Exception innerException)
    : IOException(message, innerException)
{
}

internal static class TextFileCodec
{
    internal static string GetDisplayName(TextFileEncoding encoding) => encoding switch
    {
        TextFileEncoding.Utf8 => "UTF-8 without BOM",
        TextFileEncoding.Utf8Bom => "UTF-8 with BOM",
        TextFileEncoding.Utf16LE => "UTF-16 LE",
        TextFileEncoding.Utf16BE => "UTF-16 BE",
        TextFileEncoding.Windows1252 => "Windows-1252",
        TextFileEncoding.Utf32LE => "UTF-32 LE",
        TextFileEncoding.Utf32BE => "UTF-32 BE",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static Encoding GetEncoding(TextFileEncoding encoding) => encoding switch
    {
        TextFileEncoding.Utf8 => new UTF8Encoding(false, true),
        TextFileEncoding.Utf8Bom => new UTF8Encoding(true, true),
        TextFileEncoding.Utf16LE => new UnicodeEncoding(false, true, true),
        TextFileEncoding.Utf16BE => new UnicodeEncoding(true, true, true),
        TextFileEncoding.Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(
            1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!,
        TextFileEncoding.Utf32LE => new UTF32Encoding(false, true, true),
        TextFileEncoding.Utf32BE => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    internal static TextFileContent Read(string path) => Decode(File.ReadAllBytes(path));

    internal static TextFileContent Decode(byte[] bytes)
    {
        // Check UTF-32 first because its LE BOM starts with the UTF-16 LE BOM.
        foreach (var kind in new[] { TextFileEncoding.Utf32LE, TextFileEncoding.Utf32BE,
                     TextFileEncoding.Utf8Bom, TextFileEncoding.Utf16LE, TextFileEncoding.Utf16BE })
        {
            var encoding = GetEncoding(kind);
            var preamble = encoding.GetPreamble();
            if (!bytes.AsSpan().StartsWith(preamble))
                continue;
            try
            {
                return new(encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), kind);
            }
            catch (DecoderFallbackException ex)
            {
                throw new TextEncodingException($"The file contains invalid {GetDisplayName(kind)} data. It was not opened to avoid losing characters.", ex);
            }
        }

        // BOM-less files: valid UTF-8 wins (including ASCII/empty files), otherwise Windows-1252.
        // Other legacy encodings and BOM-less UTF-16 cannot be identified reliably.
        try
        {
            return new(GetEncoding(TextFileEncoding.Utf8).GetString(bytes), TextFileEncoding.Utf8);
        }
        catch (DecoderFallbackException)
        {
            return new(GetEncoding(TextFileEncoding.Windows1252).GetString(bytes), TextFileEncoding.Windows1252);
        }
    }

    internal static byte[] Encode(string content, TextFileEncoding kind)
    {
        var encoding = GetEncoding(kind);
        try
        {
            var body = encoding.GetBytes(content);
            return [.. encoding.GetPreamble(), .. body];
        }
        catch (EncoderFallbackException ex)
        {
            throw new TextEncodingException(
                $"The text contains characters that cannot be saved as {GetDisplayName(kind)}. Choose a Unicode encoding such as UTF-8, or remove invalid Unicode characters. The file was not changed.", ex);
        }
    }
}
