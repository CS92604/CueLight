namespace Assistant.Core;

public enum Speaker { Them, Me }

public sealed record Turn(Speaker Speaker, string Text);

/// <summary>Thread-safe rolling transcript.</summary>
public sealed class Conversation
{
    private readonly List<Turn> _turns = new();
    private readonly object _lock = new();

    public void Add(Speaker speaker, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (_lock)
        {
            // Merge consecutive turns by the same speaker (one person, several pauses).
            if (_turns.Count > 0 && _turns[^1].Speaker == speaker)
                _turns[^1] = _turns[^1] with { Text = _turns[^1].Text + " " + text };
            else
                _turns.Add(new Turn(speaker, text));
        }
    }

    public void Clear() { lock (_lock) _turns.Clear(); }

    public bool IsEmpty { get { lock (_lock) return _turns.Count == 0; } }

    /// <summary>Newest turns that fit in <paramref name="maxChars"/>, oldest first. Whole turns only.</summary>
    public string Render(int maxChars)
    {
        List<Turn> turns;
        lock (_lock) turns = _turns.ToList();
        var lines = new List<string>();
        int used = 0;
        bool dropped = false;
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            string line = $"{turns[i].Speaker}: {turns[i].Text}";
            if (lines.Count > 0 && used + line.Length > maxChars) { dropped = true; break; }
            lines.Add(line);
            used += line.Length + 1;
        }
        lines.Reverse();
        if (dropped) lines.Insert(0, "[earlier conversation omitted]");
        return string.Join("\n", lines);
    }
}
