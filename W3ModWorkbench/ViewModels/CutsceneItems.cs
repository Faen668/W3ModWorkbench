using System.IO;
using System.Windows;
using System.Windows.Media;
using W3ModWorkbench.Core.Cutscenes;

namespace W3ModWorkbench.ViewModels;

/// <summary>An actor in the cutscene viewer and preview: its name and kind, the entity it's made from, and what it does.</summary>
public sealed class CutsceneActorItem(CutsceneActor actor)
{
    public CutsceneActor Actor { get; } = actor;
    public string Name => Actor.Name;
    public string Type => Actor.Type;
    public string TemplateName => Actor.Template is { } t ? Path.GetFileName(t) : "no entity";
    public string? Template => Actor.Template;

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Actor.Appearance is { } appearance) parts.Add($"appearance {appearance}");
            if (Actor.VoiceTag is { } voice) parts.Add($"voice {voice}");
            if (Actor.Tags.Count > 0) parts.Add($"tag {string.Join(", ", Actor.Tags)}");
            if (Actor.KillMe) parts.Add("despawned after");
            int events = Actor.Events.Count(e => e.Kind != CutsceneEventKind.Cloth);
            if (events > 0) parts.Add(events == 1 ? "1 event" : $"{events:N0} events");
            return string.Join("  ·  ", parts);
        }
    }

    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Animations => string.Join("\n", Actor.Animations.Select(a => $"{(a.Part == "Root" ? "Body" : a.Part.Length > 0 ? char.ToUpperInvariant(a.Part[0]) + a.Part[1..] : "Animation")}: {a.Name}  ({CutsceneTimeline.FormatTime(a.Duration)}, {a.FramesPerSecond:0.#} fps)"));
}

/// <summary>An event in the cutscene viewer's list.</summary>
public sealed class CutsceneEventItem(CutsceneEvent e)
{
    public CutsceneEvent Event { get; } = e;
    public string Time => CutsceneTimeline.FormatTime(Event.Start);
    public string Length => Event.Duration > 0 ? CutsceneTimeline.FormatTime(Event.Duration) : "";
    public double Start => Event.Start;
    public string Actor => Event.Actor ?? "Scene";
    public string Kind => Event.Kind.ToString();
    public Brush Swatch => CutsceneTimeline.BrushOf(Event.Kind);
    public string Title => Event.Title;
    public string Detail => Event.Detail;
    public string Tip => $"{Event.ClassName}" + (Event.Link is { } link ? $"\n{link}" : "");
}

/// <summary>A file a cutscene refers to, in the viewer's list: click to open it where the tool can.</summary>
public sealed class CutsceneLinkItem(CutsceneLink link)
{
    public CutsceneLink Link { get; } = link;
    public string Name => Path.GetFileName(Link.Path);
    public string Role => Link.Role;
    public string GamePath => Link.Path;
}

/// <summary>An effect the cutscene defines, in the viewer's list.</summary>
public sealed class CutsceneEffectItem(CutsceneEffect effect)
{
    public CutsceneEffect Effect { get; } = effect;
    public string Name => Effect.Name;
    public string Detail => (Effect.Length > 0 ? CutsceneTimeline.FormatTime(Effect.Length) : "no length") + (Effect.Looped ? ", looped" : "");
    public string Parts => string.Join("\n", Effect.Parts);
}

/// <summary>A kind of event with its colour and how many there are, for the legend and the filters.</summary>
public sealed class CutsceneKindItem(CutsceneEventKind kind, int count)
{
    public CutsceneEventKind Kind { get; } = kind;
    public int Count { get; } = count;
    public string Label => $"{Kind}  {Count:N0}";
    public Brush Swatch => CutsceneTimeline.BrushOf(Kind);
}
