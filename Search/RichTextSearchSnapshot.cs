using System.Text;
using System.Windows.Documents;

namespace Noted.Search;

internal sealed class RichTextSearchSnapshot
{
    private readonly List<TextPointer> _starts = [];
    private readonly List<TextPointer> _ends = [];

    internal RichTextSearchSnapshot(FlowDocument document)
    {
        var text = new StringBuilder();
        var position = document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        while (position is not null && position.CompareTo(document.ContentEnd) < 0)
        {
            var next = position.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next is null)
                break;

            var segment = new TextRange(position, next).Text;
            var textStart = position;
            while (textStart.CompareTo(next) < 0 && textStart.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                textStart = textStart.GetNextContextPosition(LogicalDirection.Forward) ?? next;
            var run = textStart.GetTextInRun(LogicalDirection.Forward);
            var isText = segment.Length > 0 && run.StartsWith(segment, StringComparison.Ordinal);
            for (var index = 0; index < segment.Length; index++)
            {
                text.Append(segment[index]);
                _starts.Add(isText ? textStart.GetPositionAtOffset(index)! : position);
                _ends.Add(isText ? textStart.GetPositionAtOffset(index + 1)! : next);
            }
            position = next;
        }
        Text = text.ToString();
    }

    internal string Text { get; }

    internal TextRange CreateRange(TextSearchMatch match) =>
        new(_starts[match.Start], _ends[match.End - 1]);

    internal TextPointer GetStart(int offset) => _starts[offset];
    internal TextPointer GetEnd(int offset) => _ends[offset];

    internal int GetOffset(TextPointer position)
    {
        // Count characters that end at or before this position.
        // CRLF and other structural separators can map to the same position.
        var low = 0;
        var high = _ends.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_ends[middle].CompareTo(position) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
