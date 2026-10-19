using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>A cutscene at a glance, for the main window's preview: its length, who's in it, and a timeline of its events.</summary>
public partial class CutsceneSummary : UserControl
{
    public CutsceneSummary() => InitializeComponent();

    public CutsceneFile? Cutscene { get; private set; }

    public void Show(CutsceneFile? cutscene)
    {
        Cutscene = cutscene;
        Timeline.Show(cutscene);
        if (cutscene is null)
        {
            ActorList.ItemsSource = null;
            KindList.ItemsSource = null;
            return;
        }
        var events = cutscene.AllEvents.ToList();
        LengthText.Text = CutsceneTimeline.FormatTime(cutscene.Duration);
        ActorsText.Text = cutscene.Actors.Count.ToString("N0");
        EventsText.Text = events.Count(e => !Timeline.HiddenKinds.Contains(e.Kind)).ToString("N0");
        EffectsText.Text = cutscene.Effects.Count.ToString("N0");
        NoEventsText.Visibility = events.Any(e => !Timeline.HiddenKinds.Contains(e.Kind)) ? Visibility.Collapsed : Visibility.Visible;
        KindList.ItemsSource = events.Where(e => !Timeline.HiddenKinds.Contains(e.Kind)).GroupBy(e => e.Kind).OrderBy(g => g.Key)
            .Select(g => new CutsceneKindItem(g.Key, g.Count())).ToList();
        ActorList.ItemsSource = cutscene.Actors.Select(a => new CutsceneActorItem(a)).ToList();

        var played = new List<string>();
        played.AddRange(cutscene.UsedIn.Select(Path.GetFileName).OfType<string>());
        if (cutscene.AudioTrack is { } audio) played.Add($"sound: {audio}");
        if (cutscene.Level is { } level) played.Add($"made in {Path.GetFileNameWithoutExtension(level)}");
        PlayedText.Text = string.Join("\n", played);
        PlayedPanel.Visibility = played.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
