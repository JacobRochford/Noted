namespace Noted.Models;

public enum TextFileEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LE,
    Utf16BE,
    Windows1252,
    // Retain compatibility with UTF-32 BOMs recognized by the previous reader.
    Utf32LE,
    Utf32BE
}

public sealed record TextFileContent(string Content, TextFileEncoding Encoding);
