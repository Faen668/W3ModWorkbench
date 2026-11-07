using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Makes a Native Quest Hook listener for the quest blocks picked in the quest graph viewer: a WitcherScript class
/// that watches them, the events ticked (each a switch on the block's GUID, with what the block fires noted), and the
/// call that registers it. It can be copied, saved into the open project's scripts, or saved anywhere. The mod's name
/// and the events ticked are remembered for next time.
/// </summary>
public partial class QuestStubWindow : Window, IMessageHost
{
    private readonly IReadOnlyList<NqhWatchedBlock> _blocks;
    private bool _ready;

    /// <summary>A block as the list shows it.</summary>
    private sealed record Row(string Name, string OutputsText);

    private QuestStubWindow(IReadOnlyList<NqhWatchedBlock> blocks)
    {
        InitializeComponent();
        _blocks = blocks;
        string remembered = App.Settings.QuestStubModName;
        ModNameBox.Text = remembered.Length > 0 ? remembered : ProjectWorkspace.Current?.Name ?? "MyMod"; // loc-ignore: a script identifier
        var events = (NqhEvents)App.Settings.QuestStubEvents;
        if (events == NqhEvents.None) events = NqhEvents.Finished;
        FinishedCheck.IsChecked = events.HasFlag(NqhEvents.Finished);
        ActivatedCheck.IsChecked = events.HasFlag(NqhEvents.Activated);
        ActiveCheck.IsChecked = events.HasFlag(NqhEvents.Active);
        StoppedCheck.IsChecked = events.HasFlag(NqhEvents.Stopped);
        BlocksTitle.Text = L.T("QuestStub.Blocks", blocks.Count);
        BlockList.ItemsSource = blocks.Select(b => new Row($"{b.Name}  ·  {b.ClassName}", string.Join(", ", b.Outputs))).ToList();
        SaveProjectButton.IsEnabled = ProjectWorkspace.Current is not null;
        SaveProjectButton.ToolTip = ProjectWorkspace.Current is { } project
            ? L.T("QuestStub.SaveProjectTip", Path.Combine(project.ScriptsPath, "local"))
            : L.T("QuestStub.NoProject");
        _ready = true;
        Update();
        Closed += (_, _) => App.Settings.Save();
    }

    /// <summary>Opens the listener maker for blocks.</summary>
    public static void Open(IReadOnlyList<NqhWatchedBlock> blocks, Window owner) => new QuestStubWindow(blocks).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private NqhEvents Events =>
        (FinishedCheck.IsChecked == true ? NqhEvents.Finished : 0) | (ActivatedCheck.IsChecked == true ? NqhEvents.Activated : 0)
        | (ActiveCheck.IsChecked == true ? NqhEvents.Active : 0) | (StoppedCheck.IsChecked == true ? NqhEvents.Stopped : 0);

    private string ModName => ModNameBox.Text.Trim();

    private void Options_Changed(object sender, RoutedEventArgs e) => Update();

    /// <summary>Writes the script again for the name and events chosen, and remembers them.</summary>
    private void Update()
    {
        if (!_ready) return;
        string id = NqhListenerStub.Identifier(ModName);
        NamesText.Text = $"'{id}'  ·  {NqhListenerStub.ClassName(ModName)}";
        FileText.Text = NqhListenerStub.FileName(ModName);
        ScriptText.Text = NqhListenerStub.Write(ModName, _blocks, Events);
        App.Settings.QuestStubModName = ModName;
        App.Settings.QuestStubEvents = (int)Events;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ScriptText.Text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        InfoText.Text = L.T("QuestStub.Copied");
    }

    /// <summary>Saves it into the open project's scripts, in local, asking first when a file of that name is there.</summary>
    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        string file = Path.Combine(project.ScriptsPath, "local", NqhListenerStub.FileName(ModName));
        if (File.Exists(file) && !await Message.Show(L.T("QuestStub.Replace"), L.T("QuestStub.ReplaceText", file), MessageKind.Warning,
                primary: L.T("QuestStub.ReplaceButton"), secondary: L.T("Common.Cancel")))
            return;
        await Save(file);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("QuestStub.SaveAs"),
            Filter = L.T("QuestStub.Filter"),
            FileName = NqhListenerStub.FileName(ModName),
        };
        if (ProjectWorkspace.Current is { } project && Directory.Exists(project.ScriptsPath)) dialog.InitialDirectory = project.ScriptsPath;
        if (dialog.ShowDialog(this) != true) return;
        await Save(dialog.FileName);
    }

    private async Task Save(string file)
    {
        try
        {
            FileSafety.CheckWrite(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // UTF-8 with its mark, which wcc_lite and the game read, and every editor knows
            await File.WriteAllTextAsync(file, ScriptText.Text, new UTF8Encoding(true));
            InfoText.Text = L.T("QuestStub.Saved", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProtectedFileException)
        {
            await ShowMessage(L.T("QuestStub.NotSaved"), ex.Message, MessageKind.Error);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
