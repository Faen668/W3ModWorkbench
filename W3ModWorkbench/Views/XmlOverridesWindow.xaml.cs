using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A definition in the list.</summary>
public sealed class OverrideGroupItem(OverrideGroup group)
{
    public OverrideGroup Group => group;
    public string Id => $"{group.Tag}  {group.Id}";

    public string Caption => group.Overrides.Count == 1
        ? $"{group.Overrides[0].Mod.Name} · {group.Overrides[0].Mode}"
        : L.T("XmlOverridesWindow.Overrides", group.Overrides.Count, string.Join(", ", group.Overrides.Select(o => o.Mod.Name).Distinct()));

    public string Icon => group.IsClash ? "" : group.IsShared ? "" : "";

    public Brush IconBrush => (Brush)Application.Current.FindResource(group.IsClash ? "SystemFillColorCautionBrush"
        : group.IsShared ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
}

/// <summary>One mod's redefinition of a definition.</summary>
public sealed class OverrideRow(XmlOverride o, int rank)
{
    public XmlOverride Override => o;
    public string Rank => $"#{rank}  ";
    public string ModName => o.Mod.Name + (o.Mod.IsModio ? " (mod.io)" : o.Mod.IsDlc ? " (DLC)" : "");
    public string ModeText => $"   {o.Mode}" + (o.MergedFrom is { Count: > 0 } from ? L.T("XmlOverridesWindow.Merge", string.Join(", ", from))
        : o.CoveredBy is not null ? L.T("XmlOverridesWindow.AlreadyInside", o.CoveredBy) : "");
    public double Opacity => o.CoveredBy is null ? 1 : 0.55;
    public string Location => o.Line > 0 ? $"{o.Where}:{o.Line}" : o.Where;
    public string FieldsText => o.Fields.Count == 0 ? L.T("XmlOverridesWindow.NoFields") : string.Join("  ", o.Fields);
}

/// <summary>
/// Shows the XML definitions that mods redefine with on_conflict, and who wins what when more than one does, so a
/// clash in the Remastered's XML overrides doesn't have to be found by reading every mod's files.
/// </summary>
public partial class XmlOverridesWindow : Window, IMessageHost
{
    private static XmlOverridesWindow? _open;

    private readonly Settings _settings = App.Settings;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cancel;
    private List<OverrideGroup> _groups = [];
    private bool _busy;

    private XmlOverridesWindow()
    {
        InitializeComponent();
        string? mods = _settings.LoadOrderModsFolder;
        if (string.IsNullOrWhiteSpace(mods) || !Directory.Exists(mods))
            mods = GameLocator.Resolve(null) is { } game && Directory.Exists(Path.Combine(game, "mods")) ? Path.Combine(game, "mods") : "";
        FolderBox.Text = mods;
        FolderBox.TextChanged += (_, _) =>
        {
            FolderHint.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanButton.IsEnabled = _busy || Directory.Exists(ModsFolder);
        };
        FolderHint.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = Directory.Exists(ModsFolder);
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        Loaded += (_, _) =>
        {
            if (ScanButton.IsEnabled) _ = ScanAsync();
        };
    }

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _open = new XmlOverridesWindow().Standalone(owner);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private string ModsFolder => FolderBox.Text.Trim().Trim('"');

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("XmlOverridesWindow.ChooseGamesModsFolder"), InitialDirectory = Directory.Exists(ModsFolder) ? ModsFolder : "" };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _cancel?.Cancel();
        else _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        string folder = ModsFolder;
        if (!Directory.Exists(folder)) return;
        _settings.LoadOrderModsFolder = folder;
        _settings.Save();
        _cancel = new CancellationTokenSource();
        _busy = true;
        ScanLabel.Text = L.T("Common.Cancel");
        FolderBox.IsEnabled = false;
        var progress = new Progress<string>(text => StatusText.Text = text);
        var started = DateTime.UtcNow;
        try
        {
            var options = new LoadOrderOptions(folder, _settings.LoadOrderIncludeDlc, ModScanner.FindSettingsFile(), GameLocator.Resolve(folder));
            var token = _cancel.Token;
            int mods = 0;
            _groups = await Task.Run(() =>
            {
                var entries = LoadOrderAnalyzer.DiscoverMods(options);
                mods = entries.Count;
                return XmlOverrideScanner.Scan(entries, ScriptMerger.ReadInventory(_settings.ScriptMergerPath, options.GameFolder), progress, token);
            }, token);
            int modio = ModioLocator.InstalledMods().Select(m => m.ModId).Distinct().Count();
            StatusText.Text = L.T("XmlOverridesWindow.Status", L.T("Count.Mod", mods), modio > 0 ? L.T("XmlOverridesWindow.FromModio", modio) : "", (DateTime.UtcNow - started).TotalSeconds,
                              L.T("Count.OverriddenDefinition", _groups.Count), _groups.Count(g => g.IsShared), _groups.Count(g => g.IsClash));
            CopyButton.IsEnabled = _groups.Count > 0;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Common.Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("XmlOverridesWindow.ModsCouldntRead"), ex.Message, MessageKind.Error);
            StatusText.Text = "";
        }
        finally
        {
            _busy = false;
            ScanLabel.Text = L.T("XmlOverridesWindow.Scan");
            FolderBox.IsEnabled = true;
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        bool sharedOnly = SharedCheck.IsChecked == true;
        var shown = _groups.Where(g => (!sharedOnly || g.IsShared || g.IsClash)
            && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
                g.Key.Contains(w, StringComparison.OrdinalIgnoreCase) || g.Overrides.Any(o => o.Mod.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || o.Fields.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase))))).Select(g => new OverrideGroupItem(g)).ToList();
        GroupList.ItemsSource = shown;
        if (shown.Count > 0) GroupList.SelectedIndex = 0;
        else ShowDetail(null);
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count == 0)
        {
            bool any = _groups.Count > 0;
            EmptyTitle.Text = any ? L.T("Common.NothingMatches") : L.T("XmlOverridesWindow.NoXMLOverridesFound");
            EmptyText.Text = any ? L.T("XmlOverridesWindow.ClearSearchTurnOff")
                : L.T("XmlOverridesWindow.NoneScannedModsSet");
        }
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail((GroupList.SelectedItem as OverrideGroupItem)?.Group);

    private void ShowDetail(OverrideGroup? g)
    {
        DetailPanel.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        if (g is null) return;
        DetailTitle.Text = g.Key;
        DetailSummary.Text = Summary(g);
        OverrideRows.ItemsSource = g.Overrides.Select((o, i) => new OverrideRow(o, i + 1)).ToList();
        ClashText.Text = string.Join(Environment.NewLine, g.FieldClashes.Select(c =>
            c.Field.StartsWith('[')
                ? L.T("XmlOverridesWindow.WinsWholeDefinitionOver", c.Winner.Mod.Name, string.Join(", ", c.Losers.Select(l => l.Mod.Name)))
                : L.T("XmlOverridesWindow.Beats", c.Field, c.Winner.Mod.Name, Shorten(c.Winner.Values[c.Field]), string.Join(", ", c.Losers.Select(l => $"{l.Mod.Name} = {Shorten(l.Values[c.Field])}")))));
        ClashText.Visibility = ClashCaption.Visibility = g.FieldClashes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "…";

    private static string Summary(OverrideGroup g)
    {
        if (g.Overrides.Count == 1)
            return L.T("XmlOverridesWindow.OnlySetsNothingClash", g.Overrides[0].Mod.Name, g.Overrides[0].Mode);
        var active = g.Overrides.Where(o => o.CoveredBy is null).ToList();
        if (active.Count == 1 && g.Overrides.Count > 1)
            return L.T("XmlOverridesWindow.OnlyLiveVersionOther", active[0].Mod.Name);
        string winner = active[0].Mod.Name;
        if (g.FieldClashes.Count > 0 && g.FieldClashes[0].Field.StartsWith('['))
            return L.T("XmlOverridesWindow.WinsDefinitionReplaceCard", winner);
        return g.FieldClashes.Count == 0
            ? L.T("XmlOverridesWindow.NoTwoModsGive")
            : L.T("XmlOverridesWindow.FieldsClash", g.FieldClashes.Count);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not OverrideRow { Override: var o } || !File.Exists(o.FullPath)) return;
        if (o.Bundle is null)
        {
            XmlWindow.Open(o.FullPath, this);
            return;
        }
        try
        {
            var entry = Bundle.Open(o.Bundle).Entries.First(x => x.Path.Replace('/', '\\').Equals(o.File, StringComparison.OrdinalIgnoreCase));
            using var fs = new FileStream(o.Bundle, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            XmlWindow.OpenData(Path.GetFileName(o.File), $"{o.Mod.Name}  ·  {o.Where}", entry.Read(fs), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _ = ShowMessage(L.T("XmlOverridesWindow.FileCouldntOpened"), ex.Message, MessageKind.Error);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = _groups.Select(g => $"{g.Key}: " + string.Join(" > ", g.Overrides.Select(o => $"{o.Mod.Name} ({o.Mode})")));
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        _ = Message.Show(null, L.T("Common.CopiedClipboard"), MessageKind.Info, autoClose: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.F5) { if (!_busy && ScanButton.IsEnabled) _ = ScanAsync(); }
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cancel?.Cancel();
}
