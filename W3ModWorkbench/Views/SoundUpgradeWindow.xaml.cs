using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A row of the results: a file of the cache, what happened to it, and the notes.</summary>
public sealed record SoundUpgradeRow(string Name, string Result, string Notes);

/// <summary>
/// Upgrades a mod's sounds for the Remastered, which plays only sound banks of version 150: a whole
/// <c>soundspc.cache</c> (its banks and streamed sounds) or one bank file, with what was done to each file listed.
/// </summary>
public partial class SoundUpgradeWindow : Window, IMessageHost
{
    private CancellationTokenSource? _running;

    private SoundUpgradeWindow(string? path)
    {
        InitializeComponent();
        (App.Settings.SoundUpgradeReplace ? ReplaceRadio : CopyRadio).IsChecked = true;
        PathBox.Text = path ?? "";
        Closing += (_, e) =>
        {
            if (_running is not null) e.Cancel = true;
        };
    }

    /// <summary>Opens the window, on <paramref name="path"/> (a sound cache or a bank) when one is given.</summary>
    public static void Open(Window owner, string? path = null) => new SoundUpgradeWindow(path).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    // ---------------------------------------------------------------- the file

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("SoundUpgrade.PickFile"), Filter = L.T("SoundUpgrade.Filter") };
        if (Directory.Exists(Path.GetDirectoryName(PathBox.Text))) dialog.InitialDirectory = Path.GetDirectoryName(PathBox.Text);
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) PathBox.Text = files[0];
    }

    /// <summary>What the file holds, said as soon as it's chosen: its banks by version, and its streamed sounds.</summary>
    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string path = PathBox.Text.Trim().Trim('"');
        UpgradeButton.IsEnabled = false;
        SurveyText.Text = "";
        if (path.Length == 0) return;
        if (!File.Exists(path))
        {
            SurveyText.Text = L.T("SoundUpgrade.NoSuchFile");
            return;
        }
        try
        {
            var (versions, streamed) = SoundCacheUpgrade.Survey(path);
            int banks = versions.Values.Sum(), old = versions.Where(v => v.Key != SoundCacheWriter.GameBankVersion).Sum(v => v.Value);
            string byVersion = string.Join(", ", versions.Select(v => L.T("SoundUpgrade.OfVersion", v.Value, v.Key)));
            SurveyText.Text = L.T("SoundUpgrade.Survey", L.T("Count.Bank", banks), byVersion, L.T("Count.StreamedSound", streamed))
                + "  " + (old == 0 ? L.T("SoundUpgrade.NothingOld") : L.T("SoundUpgrade.SomeOld", L.T("Count.Bank", old)));
            UpgradeButton.IsEnabled = banks > 0 || streamed > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SurveyText.Text = L.T("SoundUpgrade.CantRead", ex.Message);
        }
    }

    // ---------------------------------------------------------------- upgrading

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        string source = PathBox.Text.Trim().Trim('"');
        bool replace = ReplaceRadio.IsChecked == true;
        App.Settings.SoundUpgradeReplace = replace;
        App.Settings.Save();
        bool bank = source.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);

        string target;
        if (replace) target = source + ".upgrading";
        else
        {
            var dialog = new SaveFileDialog
            {
                Title = L.T("SoundUpgrade.SaveTitle"),
                Filter = L.T("SoundUpgrade.Filter"),
                InitialDirectory = Path.GetDirectoryName(source),
                FileName = Path.GetFileNameWithoutExtension(source) + "_150" + Path.GetExtension(source),
            };
            if (dialog.ShowDialog(this) != true) return;
            target = dialog.FileName;
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) target = source + ".upgrading";
        }

        string? game = GameLocator.Resolve(null);
        var caches = new List<SoundCache>();
        if (game is not null)
            foreach (string path in new[] { Path.Combine(game, "content", "content0", "soundspc.cache") }.Concat(DlcCaches(game)))
                if (File.Exists(path))
                    try { caches.Add(SoundCache.Open(path)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { }

        _running = new CancellationTokenSource();
        UpgradeButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = L.T("SoundUpgrade.Upgrading");
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            StatusText.Text = L.T("SoundUpgrade.UpgradingOf", p.Done, p.Total);
        });
        try
        {
            var token = _running.Token;
            var results = await Task.Run(() => bank
                ? [SoundCacheUpgrade.UpgradeBank(source, target, caches)]
                : SoundCacheUpgrade.Upgrade(source, target, caches, progress, token));
            bool wrote = File.Exists(target);
            if (wrote && target.EndsWith(".upgrading", StringComparison.Ordinal))
            {
                // the original stays beside it, so nothing is lost if the upgraded one doesn't play
                string original = target[..^".upgrading".Length];
                string backup = original + ".bak";
                File.Copy(original, backup, overwrite: true);
                File.Move(target, original, overwrite: true);
                target = original;
            }
            ResultGrid.ItemsSource = results.Select(r => new SoundUpgradeRow(r.Name, Outcome(r.Outcome), string.Join("\n", r.Notes))).ToList();
            int upgraded = results.Count(r => r.Outcome == UpgradeOutcome.Upgraded), kept = results.Count(r => r.Outcome == UpgradeOutcome.Kept);
            ResultsNote.Text = L.T("SoundUpgrade.Summary", L.T("Count.File", upgraded), L.T("Count.File", kept),
                L.T("Count.File", results.Count(r => r.Outcome == UpgradeOutcome.LeftOut)), L.T("Count.File", results.Count(r => r.Outcome == UpgradeOutcome.AlreadyCurrent)));
            StatusText.Text = !wrote ? L.T("SoundUpgrade.NothingWritten")
                : replace ? L.T("SoundUpgrade.Replaced", Path.GetFileName(target), Path.GetFileName(target) + ".bak")
                : L.T("SoundUpgrade.Saved", target);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = "";
            await ShowMessage(L.T("SoundUpgrade.Failed"), ex.Message, MessageKind.Error);
        }
        finally
        {
            if (File.Exists(source + ".upgrading")) File.Delete(source + ".upgrading");
            _running.Dispose();
            _running = null;
            Progress.Visibility = Visibility.Collapsed;
            UpgradeButton.IsEnabled = true;
        }
    }

    /// <summary>The DLCs' sound caches, whose streamed sounds a mod's banks may name too.</summary>
    private static IEnumerable<string> DlcCaches(string game)
    {
        string dlc = Path.Combine(game, "dlc");
        return Directory.Exists(dlc) ? Directory.GetDirectories(dlc).Select(d => Path.Combine(d, "content", "soundspc.cache")) : [];
    }

    private static string Outcome(UpgradeOutcome outcome) => outcome switch
    {
        UpgradeOutcome.Upgraded => L.T("SoundUpgrade.OutcomeUpgraded"),
        UpgradeOutcome.AlreadyCurrent => L.T("SoundUpgrade.OutcomeCurrent"),
        UpgradeOutcome.LeftOut => L.T("SoundUpgrade.OutcomeLeftOut"),
        _ => L.T("SoundUpgrade.OutcomeKept"),
    };

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var rows = ResultGrid.SelectedItems.Count > 0 ? ResultGrid.SelectedItems.OfType<SoundUpgradeRow>() : ResultGrid.Items.OfType<SoundUpgradeRow>();
        string text = string.Join("\n", rows.Select(r => $"{r.Name}\t{r.Result}\t{r.Notes.Replace("\n", " | ")}"));
        if (text.Length > 0) Clipboard.SetText(text);
    }
}
