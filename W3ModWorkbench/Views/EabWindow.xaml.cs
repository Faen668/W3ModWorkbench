using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>One file in the EAB window: what EasyAssetBrowser has for it, and the category and tags suggested for it.</summary>
public sealed class EabRow : INotifyPropertyChanged
{
    public EabRow(EntryItem item, EabAsset? asset, string origin)
    {
        Item = item;
        Asset = asset;
        Name = asset?.Name is { Length: > 0 } name ? name : System.IO.Path.GetFileNameWithoutExtension(item.Name);
        Origin = asset?.Origin is { Length: > 0 } listed ? listed : origin;
        Category = asset?.Category ?? EasyAssetBrowser.CategoryFor(item.Path);
        Tags = new(asset?.Tags ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EntryItem Item { get; }

    /// <summary>What EasyAssetBrowser lists for the file, or null when it doesn't list it.</summary>
    public EabAsset? Asset { get; }

    public string Name { get; }
    public string Origin { get; }
    public string EabPath => EasyAssetBrowser.EabPath(Item.Path);

    /// <summary>The category suggested, at first EAB's, or the one it would put the file in by its type; null when it has none for it.</summary>
    public string? Category { get; private set; }

    /// <summary>The tags suggested, at first EAB's.</summary>
    public HashSet<string> Tags { get; }

    public string TagsText => string.Join(", ", Tags.Order(StringComparer.OrdinalIgnoreCase));

    public IEnumerable<string> Added => Tags.Where(t => Asset?.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) != true);
    public IEnumerable<string> Removed => (Asset?.Tags ?? []).Where(t => !Tags.Contains(t));
    public bool CategoryChanged => Asset is not null && Category != Asset.Category;

    /// <summary>Whether there's anything to suggest: tags added or taken off, a category changed, or a file EAB doesn't list given tags.</summary>
    public bool Changed => Category is not null && (Added.Any() || Removed.Any() || CategoryChanged);

    /// <summary>What's changed, in a few words: "+Ruin −Cave", "Meshes → Entities", "new: +Ruin".</summary>
    public string ChangeText
    {
        get
        {
            if (!Changed) return "";
            var parts = new List<string>();
            if (Asset is null) parts.Add("new:");
            if (CategoryChanged) parts.Add($"{Asset!.Category} → {Category}");
            parts.AddRange(Added.Order(StringComparer.OrdinalIgnoreCase).Select(t => "+" + t));
            parts.AddRange(Removed.Order(StringComparer.OrdinalIgnoreCase).Select(t => "−" + t));
            return string.Join(" ", parts);
        }
    }

    public void SetCategory(string? category)
    {
        Category = category;
        Notify();
    }

    public void SetTag(string tag, bool on)
    {
        if (on) Tags.Add(tag);
        else Tags.Remove(tag);
        Notify();
    }

    public void Undo()
    {
        Category = Asset?.Category ?? EasyAssetBrowser.CategoryFor(Item.Path);
        Tags.Clear();
        Tags.UnionWith(Asset?.Tags ?? []);
        Notify();
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(Category)));
        PropertyChanged?.Invoke(this, new(nameof(TagsText)));
        PropertyChanged?.Invoke(this, new(nameof(ChangeText)));
    }

    /// <summary>The file as EasyAssetBrowser's lists hold an asset, with the category and tags suggested.</summary>
    public object ToEab() => new
    {
        name = Name,
        category = Category,
        origin = Origin,
        tags = Tags.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        path = EabPath,
    };
}

/// <summary>
/// What EasyAssetBrowser (by Vincent.02) has for some files: the name, category, origin, tags and path its lists give
/// each, with their tags and category to change and send to it as a suggestion, a new issue on its GitHub repository,
/// as that's where its tags are kept and reviewed. Nothing is sent from here: the issue opens in the browser to post.
/// </summary>
public partial class EabWindow : Window
{
    private const int MostFiles = 500;

    private readonly EasyAssetBrowser _eab;
    private readonly List<EabRow> _rows;
    private bool _filling;

    private EabWindow(EasyAssetBrowser eab, List<EabRow> rows, int left)
    {
        InitializeComponent();
        _eab = eab;
        _rows = rows;
        int listed = rows.Count(r => r.Asset is not null);
        SourceText.Text = $"Categories and tags by Vincent.02's EasyAssetBrowser, from its GitHub repository (Apache License 2.0): {eab.Count:N0} assets, {eab.Note}, {eab.Read:g}. "
            + $"{listed:N0} of the {rows.Count:N0} files here are in its lists" + (left > 0 ? $"; the first {MostFiles} of the files highlighted are shown." : ".");
        CategoryBox.ItemsSource = eab.Categories.Select(c => c.Name).ToList();
        Files.ItemsSource = rows;
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateChanges();
        Loaded += (_, _) =>
        {
            Files.SelectAll();
            Files.Focus();
        };
        UpdateChanges();
    }

    /// <summary>Opens the EAB window for files, each with the part of the game it's from as EAB names them (<paramref name="originOf"/>).</summary>
    public static void Show(Window owner, EasyAssetBrowser eab, IReadOnlyList<EntryItem> files, Func<EntryItem, string> originOf)
    {
        var rows = files.Take(MostFiles).Select(f => new EabRow(f, eab.Find(f.Path), originOf(f))).ToList();
        new EabWindow(eab, rows, Math.Max(0, files.Count - MostFiles)).Standalone(owner).Show();
    }

    private List<EabRow> Picked => [.. Files.SelectedItems.OfType<EabRow>()];

    // ---------------------------------------------------------------- the files highlighted

    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPicked();

    /// <summary>Shows what EAB has for the files highlighted: one's details, or what several share.</summary>
    private void ShowPicked()
    {
        var picked = Picked;
        Details.IsEnabled = picked.Count > 0;
        _filling = true;
        try
        {
            if (picked.Count == 1)
            {
                var row = picked[0];
                DetailTitle.Text = row.Name;
                ListedText.Text = row.Asset is not null ? $"Listed on EasyAssetBrowser · {row.Item.Path}"
                    : row.Category is null ? $"EasyAssetBrowser lists meshes, entities, decals (.xbm), particles, communities and SpeedTree trees (.srt) for world building. "
                        + $"It has no category for {System.IO.Path.GetExtension(row.Item.Name)} files, so this one can't be tagged."
                    : "Not listed on EasyAssetBrowser yet. Give it tags to suggest adding it.";
                PathText.Text = row.EabPath;
                OriginText.Text = row.Origin;
            }
            else
            {
                DetailTitle.Text = picked.Count == 0 ? "Highlight a file" : $"{picked.Count:N0} files";
                int listed = picked.Count(r => r.Asset is not null);
                int cannot = picked.Count(r => r.Category is null);
                ListedText.Text = picked.Count == 0 ? "" : $"{listed:N0} listed on EasyAssetBrowser, {picked.Count - listed - cannot:N0} not yet"
                    + (cannot > 0 ? $", and {cannot:N0} of kinds it doesn't list, which tags leave alone" : "");
                PathText.Text = string.Join(Environment.NewLine, picked.Take(20).Select(r => r.EabPath)) + (picked.Count > 20 ? $"{Environment.NewLine}… and {picked.Count - 20:N0} more" : "");
                OriginText.Text = string.Join(", ", picked.Select(r => r.Origin).Distinct());
            }
            var categories = picked.Select(r => r.Category).Distinct().ToList();
            CategoryBox.SelectedItem = categories.Count == 1 ? categories[0] : null;
            // a file of a kind EAB doesn't list has no tags to give: the section goes rather than sitting there greyed out
            bool canTag = picked.Any(r => r.Category is not null);
            CategoryBox.IsEnabled = canTag;
            TagSection.Visibility = canTag ? Visibility.Visible : Visibility.Collapsed;
            OpenOnSiteButton.Visibility = picked is [{ Asset: not null }] ? Visibility.Visible : Visibility.Collapsed;
            FillTags(picked);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>
    /// A box for each tag the category has (every tag when the files are in more than one, or none), and each the
    /// files have: ticked when all of them have it, filled when some do.
    /// </summary>
    private void FillTags(List<EabRow> picked)
    {
        TagPanel.Children.Clear();
        var categories = picked.Select(r => r.Category).OfType<string>().Distinct().ToList();
        IEnumerable<string> tags = categories.Count == 1 && _eab.Categories.FirstOrDefault(c => c.Name == categories[0]) is { } category ? category.Tags : _eab.Tags;
        tags = tags.Concat(picked.SelectMany(r => r.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        bool canTag = picked.Any(r => r.Category is not null);
        foreach (string tag in tags)
        {
            int with = picked.Count(r => r.Tags.Contains(tag));
            var box = new CheckBox
            {
                Content = tag,
                Margin = new Thickness(0, 2, 14, 2),
                MinWidth = 0,
                IsEnabled = canTag,
                IsThreeState = false,
                IsChecked = with == 0 ? false : with == picked.Count ? true : null,
            };
            string name = tag;
            bool? last = box.IsChecked;
            // however it's changed (a click, Space, a screen reader), and from some the first change gives it to all
            void Changed()
            {
                bool on = box.IsChecked == true || (last is null && box.IsChecked == false);
                last = on;
                if (box.IsChecked != on)
                {
                    box.IsChecked = on;
                    return;
                }
                foreach (var row in Picked.Where(r => r.Category is not null)) row.SetTag(name, on);
            }
            box.Checked += (_, _) => Changed();
            box.Unchecked += (_, _) => Changed();
            TagPanel.Children.Add(box);
        }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || CategoryBox.SelectedItem is not string category) return;
        foreach (var row in Picked.Where(r => r.Category is not null || r.Asset is null)) row.SetCategory(category);
        ShowPicked();
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => AddTag();

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTag();
        e.Handled = true;
    }

    private void NewTagBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NewTagPlaceholder.Visibility = NewTagBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gives the files highlighted a tag typed in, spelled as EAB spells it when it has it already.</summary>
    private void AddTag()
    {
        string typed = NewTagBox.Text.Trim();
        if (typed.Length == 0) return;
        string tag = _eab.Tags.FirstOrDefault(t => t.Equals(typed, StringComparison.OrdinalIgnoreCase)) ?? typed;
        foreach (var row in Picked.Where(r => r.Category is not null)) row.SetTag(tag, true);
        NewTagBox.Clear();
        ShowPicked();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Picked) row.Undo();
        ShowPicked();
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (PathText.Text.Length > 0) Clipboard.SetText(PathText.Text);
    }

    private void OpenOnSite_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is [{ Asset: not null } row]) AssetBrowserWindow.OpenLink(SiteLink(row));
    }

    /// <summary>EasyAssetBrowser's site showing one file: its category, and its name as a regular expression that matches only it.</summary>
    private static string SiteLink(EabRow row) =>
        $"{EasyAssetBrowser.Site}?cat={Uri.EscapeDataString(row.Category ?? "")}&q={Uri.EscapeDataString("^" + System.Text.RegularExpressions.Regex.Escape(row.Name) + "$")}&nR=1"
        + $"&p={Uri.EscapeDataString(System.IO.Path.GetDirectoryName(row.EabPath)?.Replace('\\', '/') ?? "")}";

    private void Site_Click(object sender, RoutedEventArgs e) => AssetBrowserWindow.OpenLink(EasyAssetBrowser.Site);
    private void Repository_Click(object sender, RoutedEventArgs e) => AssetBrowserWindow.OpenLink(EasyAssetBrowser.Repository);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !NewTagBox.IsKeyboardFocused)
        {
            Close();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- suggesting

    private List<EabRow> ChangedRows => [.. _rows.Where(r => r.Changed)];

    private void UpdateChanges()
    {
        var changed = ChangedRows;
        ChangesText.Text = changed.Count == 0
            ? "Change a file's tags or category to suggest it to EasyAssetBrowser. Suggestions go to its GitHub repository as an issue, for its author to review."
            : $"{changed.Count:N0} file{(changed.Count == 1 ? "" : "s")} changed. Suggest on GitHub opens a new issue on EasyAssetBrowser's repository with them, for you to post (you need a GitHub account).";
        SuggestButton.IsEnabled = CopyButton.IsEnabled = changed.Count > 0;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The files changed in EasyAssetBrowser's JSON form, as its lists hold them.</summary>
    private static string Json(List<EabRow> rows) => JsonSerializer.Serialize(rows.Select(r => r.ToEab()), Indented);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ChangedRows is { Count: > 0 } rows) Clipboard.SetText(Json(rows));
    }

    /// <summary>
    /// Opens a new issue on EasyAssetBrowser's repository with the changes: a table of what changed, then the files as
    /// its lists would hold them. When that's too long for a link, it goes on the clipboard to paste into the issue.
    /// </summary>
    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        var rows = ChangedRows;
        if (rows.Count == 0) return;
        string title = rows.Count == 1 ? $"Tag suggestion: {rows[0].Name}" : $"Tag suggestions: {rows[0].Name} and {rows.Count - 1} more";
        var body = new StringBuilder();
        body.AppendLine($"Suggested from W3 Mod Workbench's asset browser ({rows.Count} file{(rows.Count == 1 ? "" : "s")}).");
        body.AppendLine();
        body.AppendLine("| Asset | Category | Change |");
        body.AppendLine("| --- | --- | --- |");
        foreach (var row in rows)
            body.AppendLine($"| `{row.EabPath}` | {row.Category} | {row.ChangeText.Replace("|", "\\|")} |");
        body.AppendLine();
        body.AppendLine("As the lists hold them:");
        body.AppendLine();
        body.AppendLine("```json");
        body.AppendLine(Json(rows));
        body.AppendLine("```");

        string Link(string text) => $"{EasyAssetBrowser.Repository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(text)}";
        string link = Link(body.ToString());
        // browsers and GitHub cut links off at about 8,000 characters
        if (link.Length > 7500)
        {
            Clipboard.SetText(body.ToString());
            link = Link("The suggestion is on the clipboard: paste it here (Ctrl+V), replacing this line.");
            MessageBox.Show(this, "The suggestion is too long to put in the link, so it's on the clipboard. Paste it into the issue that opens (Ctrl+V).",
                "Suggest on GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        AssetBrowserWindow.OpenLink(link);
    }
}
