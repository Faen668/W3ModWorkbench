using System.Windows;
using System.Windows.Media;
using W3ModWorkbench.Core.Scenes;

namespace W3ModWorkbench.ViewModels;

/// <summary>A part of a scene's flow in the scene viewer: a card with its lines, choices and where it leads.</summary>
public sealed class SceneNodeItem
{
    public SceneNode Node { get; }
    public List<object> Rows { get; }
    public List<SceneLinkItem> Next { get; }

    /// <param name="text">The words of a string by its ID, or null when they aren't known.</param>
    /// <param name="speakers">Each speaker's colour, from <see cref="SpeakerBrushes"/>.</param>
    public SceneNodeItem(SceneNode node, Func<uint, string?> text, bool pauses, IReadOnlyDictionary<string, Brush> speakers)
    {
        Node = node;
        Rows = [];
        foreach (var e in node.Elements)
        {
            switch (e.Kind)
            {
                case SceneElementKind.Line:
                    Rows.Add(new SceneLineRow(e.Speaker ?? "(no one)", speakers.GetValueOrDefault(e.Speaker ?? "", Brushes.Gray), e.SpeakingTo is { } to && to != e.Speaker ? "to " + to : "",
                        Words(e.StringId, text), Join(e.Duration > 0 ? $"{e.Duration:0.#} s" : null, e.Background ? "in passing" : null, e.Detail), e.StringId));
                    break;
                case SceneElementKind.Comment:
                    // the writers' comments are mostly left out of the game's strings, so one with no words isn't shown
                    if (e.StringId is { } comment && text(comment) is { } words) Rows.Add(new SceneNoteRow(Join(words, e.Detail), true));
                    else if (e.Detail is not null) Rows.Add(new SceneNoteRow(e.Detail, true));
                    break;
                case SceneElementKind.Pause:
                    if (pauses) Rows.Add(new SceneNoteRow($"pause, {e.Duration:0.#} s", false));
                    break;
                case SceneElementKind.Cutscene:
                    Rows.Add(new SceneNoteRow($"plays the cutscene, {e.Duration:0.#} s", false));
                    break;
                case SceneElementKind.Video:
                    Rows.Add(new SceneNoteRow(Join("plays a video", e.Detail, e.Duration > 0 ? $"{e.Duration:0.#} s" : null), false));
                    break;
                case SceneElementKind.Choice:
                    Rows.Add(new SceneNoteRow(Join("Choice", e.Detail), false, true));
                    for (int o = 0; o < e.Options.Count; o++)
                        Rows.Add(new SceneOptionRow(o + 1, Words(e.Options[o].StringId, text), e.Options[o].Detail ?? "", e.Options[o].Emphasis, new SceneLinkItem(e.Options[o])));
                    break;
                default:
                    Rows.Add(new SceneNoteRow(e.Detail ?? "", false));
                    break;
            }
        }
        Next = node.Next.Select(l => new SceneLinkItem(l)).ToList();
    }

    public string Number => $"{Node.Number}";
    public string Kind => KindName(Node.Kind);
    public string Title => Node.Title;
    public string Detail => Node.Detail ?? "";
    public Visibility DetailVisibility => Node.Detail is null ? Visibility.Collapsed : Visibility.Visible;
    public Brush Swatch => BrushOf(Node.Kind);
    public string From => Node.From.Count == 0 ? (Node.Kind == SceneNodeKind.Input ? "" : "nothing leads here")
        : "from " + string.Join(", ", Node.From.Select(f => f.Number).Order());
    public Visibility RowsVisibility => Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NextVisibility => Next.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string? Cutscene => Node.Cutscene;
    public Visibility CutsceneVisibility => Node.Cutscene is null ? Visibility.Collapsed : Visibility.Visible;
    public string CutsceneName => System.IO.Path.GetFileName(Node.Cutscene ?? "");

    /// <summary>How many lines it has, for the flow list.</summary>
    public string Summary
    {
        get
        {
            int lines = Node.Elements.Count(e => e.Kind == SceneElementKind.Line);
            int options = Node.Elements.Sum(e => e.Options.Count);
            return Join(lines > 0 ? (lines == 1 ? "1 line" : $"{lines} lines") : null, options > 0 ? $"{options} options" : null);
        }
    }

    /// <summary>Whether a search finds it: in its title, its lines or who says them.</summary>
    public bool Matches(string search) =>
        Title.Contains(search, StringComparison.OrdinalIgnoreCase) || Detail.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Rows.Any(r => r switch
        {
            SceneLineRow l => l.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || l.Speaker.Contains(search, StringComparison.OrdinalIgnoreCase),
            SceneOptionRow o => o.Text.Contains(search, StringComparison.OrdinalIgnoreCase),
            SceneNoteRow n => n.Text.Contains(search, StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    public static string KindName(SceneNodeKind kind) => kind switch
    {
        SceneNodeKind.Input => "Start",
        SceneNodeKind.Output => "End",
        SceneNodeKind.Random => "Random",
        SceneNodeKind.Hub => "Link",
        _ => kind.ToString(),
    };

    public static SolidColorBrush BrushOf(SceneNodeKind kind) => KindBrushes[(int)kind];

    /// <summary>
    /// A colour for each speaker, in the order they first speak, so the people in a conversation never share one
    /// (until there are more than eight of them).
    /// </summary>
    public static Dictionary<string, Brush> SpeakerBrushes(SceneFile scene)
    {
        var brushes = new Dictionary<string, Brush>();
        foreach (var line in scene.Lines)
            if (line.Speaker is { } speaker && !brushes.ContainsKey(speaker))
                brushes[speaker] = SpeakerPalette[brushes.Count % SpeakerPalette.Length];
        return brushes;
    }

    private static readonly SolidColorBrush[] SpeakerPalette = new[]
    {
        "#4C9AFF", "#F2994A", "#2EB67D", "#E06FAE", "#B57BE0", "#E6BE3A", "#3DB8D8", "#E5675F",
    }.Select(c =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
        b.Freeze();
        return b;
    }).ToArray();

    private static readonly SolidColorBrush[] KindBrushes = new[]
    {
        "#2EB67D", // input
        "#4C9AFF", // section
        "#3DB8D8", // cutscene
        "#3DB8D8", // video
        "#E6BE3A", // condition
        "#E6BE3A", // switch
        "#B57BE0", // random
        "#F2994A", // script
        "#8E8E93", // hub
        "#E5675F", // output
    }.Select(c =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
        b.Freeze();
        return b;
    }).ToArray();

    private static string Words(uint? id, Func<uint, string?> text) =>
        id is not { } i ? "" : i == 0 ? "(no text)" : text(i) ?? $"(string {i})";

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
}

/// <summary>A spoken line: who says it, to whom, what, and how long it takes.</summary>
public sealed class SceneLineRow(string speaker, Brush speakerBrush, string to, string text, string detail, uint? stringId) : System.ComponentModel.INotifyPropertyChanged
{
    public string Speaker { get; } = speaker;
    public Brush SpeakerBrush { get; } = speakerBrush;
    public string To { get; } = to;
    public string Text { get; } = text;
    public string Detail { get; } = detail;
    public uint? StringId { get; } = stringId;
    public string Tip => StringId is { } id ? $"String {id}" + (Detail.Length > 0 ? $"  ·  {Detail}" : "") : Detail;

    /// <summary>Whether its voice is playing, to mark it in the list.</summary>
    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
        }
    }
    private bool _playing;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A comment for the writers, a pause, or a choice's heading.</summary>
public sealed record SceneNoteRow(string Text, bool Comment, bool Heading = false)
{
    public FontStyle Style => Comment ? FontStyles.Italic : FontStyles.Normal;
    public FontWeight Weight => Heading ? FontWeights.SemiBold : FontWeights.Normal;
    public double Opacity => Heading ? 1 : 0.65;
}

/// <summary>An option of a choice: its number, the line it shows, what it needs or does, whether it's picked out, and where it leads.</summary>
public sealed record SceneOptionRow(int Number, string Text, string Detail, bool Emphasis, SceneLinkItem Link)
{
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public FontWeight Weight => Emphasis ? FontWeights.SemiBold : FontWeights.Normal;
    public string Tip => Emphasis ? "Picked out in the game: the story's way on" : "";
}

/// <summary>A way out of a part, as a button that goes to the part it reaches.</summary>
public sealed class SceneLinkItem(SceneLink link)
{
    public SceneLink Link { get; } = link;
    public SceneNode? Target => Link.Target;

    public string Text => (Link.Label.Length > 0 && Link.StringId is null ? Link.Label + "  →  " : "→  ")
        + (Target is { } t ? $"{t.Number}  {SceneNodeItem.KindName(t.Kind)}: {t.Title}" : "nowhere");
    public bool CanGo => Target is not null;
}

/// <summary>A voice tag or prop the scene uses, with the entity it's made from.</summary>
public sealed class SceneActorItem(SceneActor actor, int lines)
{
    public SceneActor Actor { get; } = actor;
    public string Name => Actor.Name;
    public string Detail => (Actor.Prop ? "prop" : lines == 1 ? "1 line" : $"{lines} lines");
    public string? Template => Actor.Template;
    public string TemplateName => Actor.Template is { } t ? System.IO.Path.GetFileName(t) : "no entity";
}
