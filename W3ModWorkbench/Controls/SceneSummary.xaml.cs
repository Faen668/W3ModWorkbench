using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A scene at a glance, for the main window's preview: how big it is, how it starts (its first parts with their
/// lines and choices), who's in it, the cutscenes it plays, and the facts and scripts it uses.
/// </summary>
public partial class SceneSummary : UserControl
{
    // how many lines of the start are shown before "and more"
    private const int MostLines = 30;

    public SceneSummary() => InitializeComponent();

    public SceneFile? Scene { get; private set; }

    /// <param name="text">The words of a string by its ID; null while the game's strings are being read.</param>
    /// <param name="note">Why the lines show as string IDs, when they do.</param>
    public void Show(SceneFile? scene, Func<uint, string?>? text = null, string? note = null)
    {
        Scene = scene;
        if (scene is null)
        {
            Script.ItemsSource = ActorList.ItemsSource = KindList.ItemsSource = null;
            return;
        }
        var lines = scene.Lines.ToList();
        var nodes = scene.Nodes;
        PartsText.Text = nodes.Count.ToString("N0");
        LinesText.Text = lines.Count.ToString("N0");
        ChoicesText.Text = nodes.SelectMany(n => n.Elements).Count(e => e.Kind == SceneElementKind.Choice).ToString("N0");
        float spoken = lines.Sum(l => l.Duration);
        LengthText.Text = spoken > 0 ? CutsceneTimeline.FormatTime(spoken) : "–";
        KindList.ItemsSource = nodes.Where(n => n.Kind != SceneNodeKind.Hub).GroupBy(n => n.Kind).OrderBy(g => g.Key)
            .Select(g => new { Swatch = SceneNodeItem.BrushOf(g.Key), Label = $"{SceneNodeItem.KindName(g.Key)}  {g.Count():N0}" }).ToList();

        // the start of the flow, part by part, until enough lines are shown
        var words = text ?? (_ => null);
        var speakers = SceneNodeItem.SpeakerBrushes(scene);
        var shown = new List<SceneNodeItem>();
        int count = 0;
        foreach (var node in nodes.Where(n => n.Kind != SceneNodeKind.Hub))
        {
            if (count >= MostLines) break;
            shown.Add(new SceneNodeItem(node, words, pauses: false, speakers));
            count += node.Elements.Count(e => e.Kind == SceneElementKind.Line) + node.Elements.Sum(e => e.Options.Count);
        }
        Script.ItemsSource = shown;
        int left = nodes.Count(n => n.Kind != SceneNodeKind.Hub) - shown.Count;
        MoreText.Text = left > 0 ? L.T("SceneSummary.MoreParts", left) : "";
        MoreText.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        StringsText.Text = note ?? (text is null ? L.T("SceneSummary.ReadingGamesStrings") : "");
        StringsText.Visibility = StringsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var spokenBy = lines.GroupBy(l => l.Speaker ?? "").ToDictionary(g => g.Key, g => g.Count());
        ActorList.ItemsSource = scene.Actors.Select(a => new SceneActorItem(a, spokenBy.GetValueOrDefault(a.Name))).ToList();

        var cutscenes = nodes.Select(n => n.Cutscene).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName).ToList();
        CutscenesText.Text = string.Join("\n", cutscenes);
        CutscenesPanel.Visibility = cutscenes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var facts = scene.Facts.ToList();
        FactsText.Text = string.Join("\n", facts);
        FactsPanel.Visibility = facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var scripts = scene.Scripts.ToList();
        ScriptsText.Text = string.Join("\n", scripts);
        ScriptsPanel.Visibility = scripts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
