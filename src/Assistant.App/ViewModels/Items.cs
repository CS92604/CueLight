using Assistant.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Assistant.App.ViewModels;

public sealed class TurnVm
{
    public TurnVm(Speaker who, string text) { Who = who == Speaker.Me ? "You" : "Them"; IsMe = who == Speaker.Me; Text = text; }
    public string Who { get; }
    public bool IsMe { get; }
    public string Text { get; }
}

/// <summary>One suggestion: text to say or paste, with a Copy button.</summary>
public sealed partial class OptionVm : ObservableObject
{
    private readonly Func<string, Task> _copy;

    public OptionVm(string text, bool isComplete, Func<string, Task> copy)
    {
        Text = text;
        IsComplete = isComplete;
        _copy = copy;
    }

    public string Text { get; }
    /// <summary>False while Claude is still writing this option.</summary>
    public bool IsComplete { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _justCopied;

    public string CopyLabel => JustCopied ? "Copied" : "Copy";

    [RelayCommand]
    private async Task Copy()
    {
        await _copy(Text);
        JustCopied = true;
        await Task.Delay(1600);
        JustCopied = false;
    }
}

/// <summary>A SAY or TYPE group of options (or a plain note from Claude).</summary>
public sealed class SectionVm
{
    public SectionVm(SectionKind kind, IEnumerable<OptionVm> options)
    {
        Kind = kind;
        Options = options.ToList();
    }

    public SectionKind Kind { get; }
    public bool IsSay => Kind == SectionKind.Say;
    public bool IsType => Kind == SectionKind.Type;
    public bool IsNote => Kind == SectionKind.Note;
    public bool IsChip => Kind != SectionKind.Note;
    public string Title => Kind == SectionKind.Say ? "Say" : "Type";
    public string Hint => Kind == SectionKind.Say ? "out loud" : "reply to the text on screen";
    public IReadOnlyList<OptionVm> Options { get; }
    public string NoteText => string.Join("\n", Options.Select(o => o.Text));
}
