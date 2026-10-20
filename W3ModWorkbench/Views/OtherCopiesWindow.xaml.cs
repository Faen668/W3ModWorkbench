using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>One copy of the file: where it is, where it comes in the load order, and whether it's the same as the highlighted one.</summary>
public sealed class CopyRow(EntryItem item, int rank, OtherCopiesWindow.Place place, bool wins, bool highlighted) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public EntryItem Item => item;
    public string Rank => $"#{rank}";
    public string Mod => place.Name;
    public string Where => place.Kind;
    public bool Wins => wins;
    public bool IsHighlighted => highlighted;
    public string Location => item.Entry.Archive.FilePath;
    public string SizeText => $"{item.SizeText}  ·  {item.CompressionText}";

    public Visibility CompareVisibility => highlighted ? Visibility.Collapsed : Visibility.Visible;
    public string CompareTip => item.IsPreviewable ? "Open the two side by side with the differences marked" : "Compare the two byte for byte";
    public string RevealTip => $"Show {item.Entry.Archive.Name} in Explorer";

    /// <summary>The same as the highlighted copy: null while it's being read, or when it couldn't be.</summary>
    private bool? _same;
    private bool _failed;

    public void SetSame(bool? same, bool failed = false)
    {
        _same = same;
        _failed = failed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SameText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SameGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SameBrush)));
    }

    public string SameText => highlighted ? "The highlighted copy"
        : _failed ? "Couldn't be read"
        : _same is null ? "Reading…"
        : _same.Value ? "Same content as the highlighted copy" : "Different content";

    public string SameGlyph => highlighted ? "" : _same is null ? "" : _same.Value ? "" : "";

    public Brush SameBrush => (Brush)Application.Current.FindResource(highlighted || _same is null ? "TextFillColorSecondaryBrush"
        : _same.Value ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
}

/// <summary>
/// Every open copy of a file at the same game path, in the order the game loads them, so it's plain which mod's copy the
/// game uses and which copies are really different.
/// </summary>
public partial class OtherCopiesWindow : Window, IMessageHost
{
    /// <summary>Where an archive sits in the load order: its group (mod.io, DLC, mod, the game, elsewhere), its priority and its name.</summary>
    public sealed record Place(int Group, int? Priority, string Name, string Kind);

    private readonly MainWindow _main;
    private readonly EntryItem _item;

    private OtherCopiesWindow(MainWindow main, EntryItem item, List<EntryItem> copies)
    {
        InitializeComponent();
        _main = main;
        _item = item;
        FileTitle.Text = item.Name;
        FilePath.Text = item.Path + (item.Kind is { } kind ? "  ·  " + kind.Name : "");

        var settings = ModScanner.ReadSettings(ModScanner.FindSettingsFile());
        string? modio = ModioLocator.Root();
        var placed = copies.Select(c => (Item: c, Place: PlaceOf(c.Entry.Archive.FilePath, settings, modio)))
            .OrderBy(c => c.Place.Group).ThenBy(c => c.Place.Priority is null ? 1 : 0).ThenBy(c => c.Place.Priority ?? 0)
            .ThenBy(c => c.Place.Name, StringComparer.OrdinalIgnoreCase).ToList();
        bool knownWinner = placed.Count > 1 && placed[0].Place.Group < OutsideGroup;
        var rows = placed.Select((c, i) => new CopyRow(c.Item, i + 1, c.Place, knownWinner && i == 0, ReferenceEquals(c.Item, item))).ToList();
        CopyRows.ItemsSource = rows;

        int others = rows.Count - 1;
        Summary.Text = others == 0
            ? "This is the only copy in what's open. The asset browser (Tools > Asset Browser) looks in every installed mod, the DLC and the game."
            : $"{Format.Count(others, "other copy", "other copies")} at the same path. Files at the same path replace each other: the game uses the copy from whatever loads first, and the rest are ignored.";
        OrderNote.Text = (settings.Count > 0 ? "Load order from mods.settings: " : "mods.settings wasn't found, so mods are in folder name order: ")
            + "mod.io mods, then DLC, then mods by priority (unlisted ones after, by name), then the game's own files. The same order the load order analyser uses. Only open archives are listed.";

        Loaded += async (_, _) => await CompareContentsAsync(rows);
    }

    public static void Open(MainWindow owner, EntryItem item, List<EntryItem> copies)
    {
        if (!copies.Contains(item)) copies.Insert(0, item);
        new OtherCopiesWindow(owner, item, copies).Standalone(owner).Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private const int OutsideGroup = 4;

    /// <summary>Where a file from <paramref name="path"/> loads: from mod.io, the dlc or mods folder, the game's content, or somewhere else.</summary>
    public static Place PlaceOf(string path, Dictionary<string, (bool Enabled, int? Priority)> settings, string? modioRoot)
    {
        string full = Path.GetFullPath(path);
        if (modioRoot is not null && full.StartsWith(Path.GetFullPath(modioRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return new Place(0, null, ArchiveEntry.OwnerOf(full), "mod.io");
        var parts = full.Split('\\');
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            string part = parts[i];
            if (part.Equals("mods", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length - 1)
            {
                string mod = parts[i + 1];
                return new Place(2, settings.TryGetValue(mod, out var s) ? s.Priority : null, mod,
                    settings.TryGetValue(mod, out var e) && !e.Enabled ? "mod, switched off" : "mod");
            }
            if (part.Equals("dlc", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length - 1)
                return new Place(1, null, parts[i + 1], "DLC");
            if (part.Equals("content", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length - 1 && GameLocator.Resolve(null) is { } game
                && full.StartsWith(Path.GetFullPath(game).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return new Place(3, null, parts[i + 1], "the game");
        }
        return new Place(OutsideGroup, null, ArchiveEntry.OwnerOf(full), "outside the game folder");
    }

    /// <summary>Reads every copy and says whether it's the same as the highlighted one.</summary>
    private async Task CompareContentsAsync(List<CopyRow> rows)
    {
        string? mine;
        try { mine = await MainWindow.HashAsync(_item.Entry); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { mine = null; }
        foreach (var row in rows.Where(r => !r.IsHighlighted))
        {
            if (!IsLoaded) return;
            if (mine is null || row.Item.Size != _item.Size)
            {
                row.SetSame(mine is null ? null : false, failed: mine is null);
                continue;
            }
            try { row.SetSame(await MainWindow.HashAsync(row.Item.Entry) == mine); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { row.SetSame(null, failed: true); }
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        // the copy the game uses is the newer side: it's what the others are measured against
        if ((sender as FrameworkElement)?.Tag is CopyRow row) _ = _main.CompareAsync(row.Item, _item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CopyRow row) _main.Open(row.Item);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CopyRow row) _main.Reveal(row.Item);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
