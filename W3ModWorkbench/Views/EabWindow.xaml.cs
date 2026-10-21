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
    public EabRow(EntryItem item, EabAsset? asset, string origin, string? blocked = null)
    {
        Item = item;
        Asset = asset;
        Blocked = blocked;
        Name = asset?.Name is { Length: > 0 } name ? name : System.IO.Path.GetFileNameWithoutExtension(item.Name);
        Origin = asset?.Origin is { Length: > 0 } listed ? listed : origin;
        Category = asset?.Category ?? EasyAssetBrowser.CategoryFor(item.Path);
        Tags = new(asset?.Tags ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EntryItem Item { get; }

    /// <summary>
    /// Why the file can't be suggested, or null when it can: only the game's own files (its content, its official DLC,
    /// and REDkit's copies of them) are EasyAssetBrowser's to list, never a file only a mod has.
    /// </summary>
    public string? Blocked { get; }

    public bool CanSuggest => Blocked is null;

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

    /// <summary>Whether a file EAB doesn't list is suggested for adding, with tags or without.</summary>
    public bool Adding { get; private set; }

    /// <summary>Whether there's anything to suggest: tags added or taken off, a category changed, or a file EAB doesn't list to add.</summary>
    public bool Changed => CanSuggest && (Asset is null ? Adding : Added.Any() || Removed.Any() || CategoryChanged);

    /// <summary>What's changed, in a few words: "+Ruin −Cave", "Meshes → Entities", "new: +Ruin".</summary>
    public string ChangeText
    {
        get
        {
            if (!CanSuggest) return "a mod's file: can't be suggested";
            if (!Changed) return "";
            var parts = new List<string>();
            if (Asset is null) parts.Add((Category is null ? "new, no category" : "new") + (Tags.Count == 0 ? "" : ":"));
            if (CategoryChanged) parts.Add($"{Asset!.Category} → {Category}");
            parts.AddRange(Added.Order(StringComparer.OrdinalIgnoreCase).Select(t => "+" + t));
            parts.AddRange(Removed.Order(StringComparer.OrdinalIgnoreCase).Select(t => "−" + t));
            return string.Join(" ", parts);
        }
    }

    public void SetCategory(string? category)
    {
        if (!CanSuggest) return;
        Category = category;
        // giving a file EAB doesn't list a category suggests adding it
        if (Asset is null && category is not null) Adding = true;
        Notify();
    }

    public void SetTag(string tag, bool on)
    {
        if (!CanSuggest) return;
        if (on) Tags.Add(tag);
        else Tags.Remove(tag);
        // tagging a file EAB doesn't list suggests adding it
        if (on && Asset is null) Adding = true;
        Notify();
    }

    /// <summary>Suggests adding a file EAB doesn't list, or stops; nothing for one it lists already.</summary>
    public void SetAdding(bool on)
    {
        if (Asset is not null || !CanSuggest) return;
        Adding = on;
        Notify();
    }

    public void Undo()
    {
        Category = Asset?.Category ?? EasyAssetBrowser.CategoryFor(Item.Path);
        Tags.Clear();
        Tags.UnionWith(Asset?.Tags ?? []);
        Adding = false;
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
    /// <param name="blocked">Why a file can't be suggested (a file only a mod has), or null when it can.</param>
    public static void Show(Window owner, EasyAssetBrowser eab, IReadOnlyList<EntryItem> files, Func<EntryItem, string> originOf, Func<EntryItem, string?> blocked)
    {
        var rows = files.Take(MostFiles).Select(f => new EabRow(f, eab.Find(f.Path), originOf(f), blocked(f))).ToList();
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
                ListedText.Text = row.Blocked is { } why ? why
                    : row.Asset is not null ? $"Listed on EasyAssetBrowser · {row.Item.Path}"
                    : row.Category is null ? $"Not listed on EasyAssetBrowser yet, and it has no category for {System.IO.Path.GetExtension(row.Item.Name)} files. "
                        + "Pick one of its categories, or type a new one, or suggest it without and leave that to its author."
                    : "Not listed on EasyAssetBrowser yet.";
                PathText.Text = row.EabPath;
                OriginText.Text = row.Origin;
            }
            else
            {
                DetailTitle.Text = picked.Count == 0 ? "Highlight a file" : $"{picked.Count:N0} files";
                int listed = picked.Count(r => r.Asset is not null);
                int mods = picked.Count(r => !r.CanSuggest);
                ListedText.Text = picked.Count == 0 ? "" : $"{listed:N0} listed on EasyAssetBrowser, {picked.Count - listed - mods:N0} not yet"
                    + (mods > 0 ? $", and {mods:N0} only a mod has, which can't be suggested: EasyAssetBrowser lists only the game's own files" : "");
                PathText.Text = string.Join(Environment.NewLine, picked.Take(20).Select(r => r.EabPath)) + (picked.Count > 20 ? $"{Environment.NewLine}… and {picked.Count - 20:N0} more" : "");
                OriginText.Text = string.Join(", ", picked.Select(r => r.Origin).Distinct());
            }
            var categories = picked.Select(r => r.Category).Distinct().ToList();
            CategoryBox.Text = categories.Count == 1 ? categories[0] ?? "" : "";
            // a mod's file has nothing to change: its tags go, rather than sitting there doing nothing
            bool any = picked.Any(r => r.CanSuggest);
            CategoryBox.IsEnabled = any;
            TagSection.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            OpenOnSiteButton.Visibility = picked is [{ Asset: not null }] ? Visibility.Visible : Visibility.Collapsed;
            ShowAdding(picked);
            FillTags(picked);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>The add box: shown for files EAB doesn't list, ticked when they're all suggested for adding, filled when some are.</summary>
    private void ShowAdding(List<EabRow> picked)
    {
        bool filling = _filling;
        _filling = true;
        try
        {
            var unlisted = picked.Where(r => r.Asset is null && r.CanSuggest).ToList();
            AddCheck.Visibility = unlisted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AddCheck.Content = unlisted.Count switch
            {
                1 when picked.Count == 1 => "Suggest adding it to EasyAssetBrowser",
                1 => "Suggest adding the 1 file it doesn't list to EasyAssetBrowser",
                var n => $"Suggest adding the {n:N0} files it doesn't list to EasyAssetBrowser",
            };
            int adding = unlisted.Count(r => r.Adding);
            AddCheck.IsChecked = _addWas = adding == 0 ? false : adding == unlisted.Count ? true : null;
        }
        finally
        {
            _filling = filling;
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
        foreach (string tag in tags)
        {
            int with = picked.Count(r => r.Tags.Contains(tag));
            var box = new CheckBox
            {
                Content = tag,
                Margin = new Thickness(0, 2, 14, 2),
                MinWidth = 0,
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
                var rows = Picked;
                foreach (var row in rows.Where(r => r.CanSuggest)) row.SetTag(name, on);
                // a tag on a file EAB doesn't list suggests adding it, which the add box says
                ShowAdding(rows);
            }
            box.Checked += (_, _) => Changed();
            box.Unchecked += (_, _) => Changed();
            TagPanel.Children.Add(box);
        }
    }

    /// <summary>Suggests adding the files highlighted that EAB doesn't list (from some, to all of them), or stops.</summary>
    private void AddCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        // a click on a filled box (some of them) unticks it; it means all of them
        bool on = AddCheck.IsChecked == true || (_addWas is null && AddCheck.IsChecked == false);
        foreach (var row in Picked) row.SetAdding(on);
        ShowPicked();
    }

    // the add box's state as last shown, to tell a click from some to all
    private bool? _addWas;

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || CategoryBox.SelectedItem is not string category) return;
        SetCategory(category);
    }

    // a category typed in (one EAB doesn't have yet), taken when Enter's pressed or the box is left
    private void CategoryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SetCategory(CategoryBox.Text);
        e.Handled = true;
    }

    private void CategoryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_filling && !CategoryBox.IsKeyboardFocusWithin) SetCategory(CategoryBox.Text);
    }

    /// <summary>Gives the files highlighted a category, spelled as EAB spells it when it has it; nothing for an empty one, or the one they have.</summary>
    private void SetCategory(string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return;
        string category = _eab.Categories.FirstOrDefault(c => c.Name.Equals(typed, StringComparison.OrdinalIgnoreCase))?.Name ?? typed;
        var rows = Picked.Where(r => r.Category != category).ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows) row.SetCategory(category);
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
        foreach (var row in Picked) row.SetTag(tag, true);
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

    // never a mod's file, however it got here
    private List<EabRow> ChangedRows => [.. _rows.Where(r => r.Changed && r.CanSuggest)];

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

    // GitHub keeps at most 65,536 characters of an issue's text
    private const int MostIssueText = 64_000;

    /// <summary>
    /// Opens a new issue on EasyAssetBrowser's repository with the changes: a table of what changed, then the files as
    /// its lists would hold them; a big batch without the table, a file to a line. When that's too long for a link, it
    /// goes on the clipboard to paste into the issue, and when it's too long for an issue, it says so.
    /// </summary>
    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        var rows = ChangedRows;
        if (rows.Count == 0) return;
        bool allNew = rows.All(r => r.Asset is null);
        string kind = allNew ? "Assets to add" : rows.Any(r => r.Asset is null) ? "Asset and tag suggestions" : "Tag suggestions";
        string title = rows.Count == 1 ? $"{(allNew ? "Asset to add" : "Tag suggestion")}: {rows[0].Name}" : $"{kind}: {rows[0].Name} and {rows.Count - 1} more";
        string intro = $"Suggested from W3 Mod Workbench's asset browser ({rows.Count} file{(rows.Count == 1 ? "" : "s")}).";

        string text = Full();
        if (text.Length > MostIssueText) text = Compact();
        if (text.Length > MostIssueText)
        {
            int fit = rows.Count * MostIssueText / text.Length;
            MessageBox.Show(this, $"{rows.Count:N0} files are more than one GitHub issue can hold (about {fit:N0} fit). Highlight fewer and suggest them in parts, or use Copy as JSON and send the file another way.",
                "Suggest on GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string Link(string body) => $"{EasyAssetBrowser.Repository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        string link = Link(text);
        // browsers and GitHub cut links off at about 8,000 characters
        if (link.Length > 7500)
        {
            Clipboard.SetText(text);
            link = Link("The suggestion is on the clipboard: paste it here (Ctrl+V), replacing this line.");
            MessageBox.Show(this, "The suggestion is too long to put in the link, so it's on the clipboard. Paste it into the issue that opens (Ctrl+V).",
                "Suggest on GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        AssetBrowserWindow.OpenLink(link);

        // a table of what changed, then the files as the lists hold them
        string Full()
        {
            var body = new StringBuilder();
            body.AppendLine(intro);
            body.AppendLine();
            body.AppendLine("| Asset | Category | Change |");
            body.AppendLine("| --- | --- | --- |");
            foreach (var row in rows)
                body.AppendLine($"| `{row.EabPath}` | {row.Category} | {row.ChangeText.Replace("|", @"\|")} |");
            body.AppendLine();
            body.AppendLine("As the lists hold them:");
            body.AppendLine();
            body.AppendLine("```json");
            body.AppendLine(Json(rows));
            body.AppendLine("```");
            return body.ToString();
        }

        // only the files, a line each, for a batch the table would make too long
        string Compact()
        {
            var body = new StringBuilder();
            body.AppendLine(intro);
            body.AppendLine();
            body.AppendLine("As the lists hold them, a file to a line:");
            body.AppendLine();
            body.AppendLine("```json");
            body.AppendLine("[");
            for (int i = 0; i < rows.Count; i++)
                body.AppendLine("  " + JsonSerializer.Serialize(rows[i].ToEab(), Inline) + (i < rows.Count - 1 ? "," : ""));
            body.AppendLine("]");
            body.AppendLine("```");
            return body.ToString();
        }
    }

    private static readonly JsonSerializerOptions Inline = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
