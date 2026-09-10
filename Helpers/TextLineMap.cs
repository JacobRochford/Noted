namespace Noted.Helpers;

internal sealed class TextLineMap
{
    internal string Text { get; }
    internal int[] Starts { get; }

    internal TextLineMap(string text)
    {
        Text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        Starts = starts.ToArray();
    }

    internal int LineAt(int characterIndex)
    {
        var index = Array.BinarySearch(Starts, Math.Clamp(characterIndex, 0, Text.Length));
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
