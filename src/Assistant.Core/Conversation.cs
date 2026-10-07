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

    public void Clear() { lock (_lock) { _turns.Clear(); _floor = 0; } }

    public bool IsEmpty { get { lock (_lock) return _turns.Count == 0; } }

    /// <summary>The longest stretch of one person's talk that is sent whole. A monologue past this keeps only its
    /// latest part, so the newest turn (which changes with every request) stays small.</summary>
    public const int MaxTurnChars = 5_000;

    private const string OmittedNotice = "[earlier conversation omitted]";

    private int _floor;   // the first turn that is still sent; only ever moves forward, and in jumps

    /// <summary>
    /// The recent conversation for a request, oldest first, whole turns only. Unlike <see cref="Render"/> this
    /// trims in jumps: once the text passes <paramref name="maxChars"/> the oldest turns are dropped until
    /// about <paramref name="keepChars"/> remain, and then nothing more is dropped for a good while. The
    /// start of what Claude sees stays put from one request to the next, which is what lets Claude's
    /// prompt cache keep reading it at a fraction of the price instead of paying full price again.
    /// </summary>
    public string Window(int maxChars, int keepChars)
    {
        lock (_lock)
        {
            if (_floor > _turns.Count) _floor = 0;
            long total = 0;
            for (int i = _floor; i < _turns.Count; i++) total += Line(_turns[i]).Length + 1;
            long Size() => total + (_floor > 0 ? OmittedNotice.Length + 1 : 0);
            if (Size() > maxChars)
                while (_floor < _turns.Count - 1 && Size() > keepChars)   // trim well below the limit, not just under it
                    total -= Line(_turns[_floor++]).Length + 1;

            var lines = new List<string>();
            if (_floor > 0) lines.Add(OmittedNotice);
            for (int i = _floor; i < _turns.Count; i++) lines.Add(Line(_turns[i]));
            return string.Join("\n", lines);
        }
    }

    private static string Line(Turn t) =>
        $"{t.Speaker}: {(t.Text.Length > MaxTurnChars ? "… " + t.Text[^MaxTurnChars..] : t.Text)}";

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
