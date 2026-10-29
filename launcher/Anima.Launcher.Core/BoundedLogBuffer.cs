namespace Anima.Launcher.Core;

/// <summary>A thread-safe tail whose budget includes separators, before any string is joined.</summary>
public sealed class BoundedLogBuffer
{
    public const int MaxLines = 2000;
    public const int MaxCharacters = 240000;
    public const int MaxLineCharacters = 20000;
    private readonly Queue<(long Sequence, long Offset, string Text)> lines = new();
    private readonly object sync = new();
    private int characters;
    private long sequence;
    private long nextOffset;
    public long Sequence { get { lock (sync) return sequence; } }
    public int Count { get { lock (sync) return lines.Count; } }
    public int Characters { get { lock (sync) return characters; } }
    public void Add(string value)
    {
        lock (sync)
        {
            var remaining = value.AsSpan();
            while (true)
            {
                var separator = remaining.IndexOfAny('\r', '\n');
                if (separator < 0)
                {
                    AddLine(remaining, remaining.Length == value.Length ? value : null);
                    break;
                }
                AddLine(remaining[..separator]);
                var consumed = separator + 1;
                if (remaining[separator] == '\r' && consumed < remaining.Length && remaining[consumed] == '\n') consumed++;
                remaining = remaining[consumed..];
                // Preserve empty/trailing logical lines; Capture normalizes every separator to CRLF.
            }
        }
    }

    // The caller holds sync for the entire entry, so concurrent stacks cannot interleave their lines.
    private void AddLine(ReadOnlySpan<char> value, string? original = null)
    {
        string text;
        if (value.Length > MaxLineCharacters)
        {
            var length = MaxLineCharacters - 2;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            text = string.Concat(value[..length], " …".AsSpan());
        }
        else text = original ?? value.ToString();
        lines.Enqueue((++sequence, nextOffset, text));
        nextOffset += text.Length + 2;
        characters += text.Length + 2;
        while (lines.Count > MaxLines || characters > MaxCharacters)
            characters -= lines.Dequeue().Text.Length + 2;
    }
    public string[] Snapshot(long after = 0) { lock (sync) return lines.Where(line => line.Sequence > after).Select(line => line.Text).ToArray(); }
    public BoundedLogSnapshot Capture(long after = 0)
    {
        lock (sync)
        {
            var retained = lines.Where(line => line.Sequence > after).ToArray();
            return new(string.Join("\r\n", retained.Select(line => line.Text)), retained.Length == 0 ? nextOffset : retained[0].Offset);
        }
    }
    public string[] Drain()
    {
        lock (sync) { var result = lines.Select(line => line.Text).ToArray(); lines.Clear(); characters = 0; return result; }
    }
    public void Clear() { lock (sync) { lines.Clear(); characters = 0; } }
}

public sealed record BoundedLogSnapshot(string Text, long Offset)
{
    public (int Start, int Length) RebaseSelection(BoundedLogSnapshot previous, int start, int length)
    {
        var first = (int)Math.Clamp(previous.Offset + start - Offset, 0, Text.Length);
        var last = (int)Math.Clamp(previous.Offset + start + length - Offset, first, Text.Length);
        return (first, last - first);
    }
}
