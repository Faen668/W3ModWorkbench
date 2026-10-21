using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Help;

/// <summary>A group of help pages, shown as one collapsible heading in the help window.</summary>
public sealed class HelpCategory
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public string Summary { get; init; } = "";
    public int Order { get; init; }
    public List<HelpPage> Pages { get; } = [];
}

/// <summary>One help page: its header block (title, icon, summary, keywords) and its body in the help markup.</summary>
public sealed class HelpPage
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required HelpCategory Category { get; init; }
    public string Summary { get; init; } = "";
    public string Keywords { get; init; } = "";
    public int Order { get; init; }
    public required string Body { get; init; }

    /// <summary>The page's English title, which [[links]] in every language may use; the same as <see cref="Title"/> in English.</summary>
    public required string EnglishTitle { get; init; }

    /// <summary>The language the page is in: the app's when it's translated, else en.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Windows this page is about, by class name: F1 in one of them opens this page.</summary>
    public IReadOnlyList<string> Windows { get; init; } = [];

    /// <summary>The body's ## headings, in order: the page's "On this page" list.</summary>
    public IReadOnlyList<string> Sections => _sections ??= [.. Body.Replace("\r", "").Split('\n')
        .Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(l => l[3..].Trim())];
    private IReadOnlyList<string>? _sections;

    /// <summary>The body as plain words, for searching and for the snippet a search result shows.</summary>
    public string PlainText => _plain ??= HelpLibrary.Plain(Body);
    private string? _plain;
}

/// <summary>A page that matches a search, how well, and a line of it with the words in.</summary>
public sealed record HelpHit(HelpPage Page, int Score, int Count, string Snippet);

/// <summary>
/// The help pages, read from the Markdown files built into the app (Help\Pages\&lt;category folder&gt;\&lt;page&gt;.md), and
/// the pictures they show (Help\Images). A category folder is named "NN Title" for its place, and its _category.md has
/// its icon and summary; a page starts with a header block between --- lines. Help\Pages\README.md has the markup.
/// <para>
/// A translation is the same file under a folder named for its language (Help\Pages\de\05 Scripting\10 Script Bench.md).
/// The pages are those of the language the app is in, page by page, with English for any not translated yet; what a
/// translation's header leaves out (icon, order, windows) is the English page's.
/// </para>
/// </summary>
public static class HelpLibrary
{
    private const string PagePrefix = "HelpPages/", ImagePrefix = "HelpImages/";

    /// <summary>The pages read, by language.</summary>
    private static readonly Dictionary<string, IReadOnlyList<HelpCategory>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The categories and their pages in the language the app is in.</summary>
    public static IReadOnlyList<HelpCategory> Categories
    {
        get
        {
            string language = Localisation.Loc.Instance.Language;
            lock (Loaded)
            {
                if (!Loaded.TryGetValue(language, out var categories)) Loaded[language] = categories = Load(language);
                return categories;
            }
        }
    }

    public static IEnumerable<HelpPage> Pages => Categories.SelectMany(c => c.Pages);

    /// <summary>A page by its title in the language shown, or its English title (as links may name it).</summary>
    public static HelpPage? Find(string title) =>
        Pages.FirstOrDefault(p => p.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
        ?? Pages.FirstOrDefault(p => p.EnglishTitle.Equals(title, StringComparison.OrdinalIgnoreCase));

    /// <summary>The page about a window, by its class name (MeshWindow), for F1.</summary>
    public static HelpPage? ForWindow(string windowClass) =>
        Pages.FirstOrDefault(p => p.Windows.Contains(windowClass, StringComparer.Ordinal));

    /// <summary>
    /// The files under Help\Pages by their path there (05 Scripting/10 Script Bench.md): the English ones, in numbered
    /// category folders, or a language's, under the folder named for it.
    /// </summary>
    private static Dictionary<string, (Dictionary<string, string> Head, string Body)> Read(string? language)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, (Dictionary<string, string>, string)>(StringComparer.OrdinalIgnoreCase);
        string prefix = language is null ? PagePrefix : PagePrefix + language + "/";
        foreach (string name in assembly.GetManifestResourceNames())
        {
            string resource = name.Replace('\\', '/');
            if (!resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string path = resource[prefix.Length..];
            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            if (folder.Length == 0) continue; // README.md and the like, for whoever writes the pages
            if (language is null && !char.IsDigit(folder[0])) continue; // a language's folder: its translations
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[path] = Split(reader.ReadToEnd());
        }
        return files;
    }

    private static IReadOnlyList<HelpCategory> Load(string language)
    {
        var english = Read(null);
        var translated = language.Equals(Localisation.Loc.English, StringComparison.OrdinalIgnoreCase) ? [] : Read(language);
        var categories = new Dictionary<string, HelpCategory>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<(string Folder, Dictionary<string, string> Head, string Body, Dictionary<string, string> English, bool Translated)>();
        foreach (var (path, (englishHead, englishBody)) in english)
        {
            string folder = Path.GetDirectoryName(path)!.Replace('\\', '/');
            bool isTranslated = translated.TryGetValue(path, out var translation);
            var (head, body) = isTranslated ? translation : (englishHead, englishBody);
            if (Path.GetFileName(path).Equals("_category.md", StringComparison.OrdinalIgnoreCase))
            {
                var (order, title) = Numbered(folder);
                categories[folder] = new HelpCategory
                {
                    Title = head.GetValueOrDefault("title") ?? englishHead.GetValueOrDefault("title") ?? title,
                    Glyph = Glyph(head.GetValueOrDefault("icon") ?? englishHead.GetValueOrDefault("icon")),
                    Summary = head.GetValueOrDefault("summary") ?? body.Trim(),
                    Order = order,
                };
            }
            else pages.Add((folder, head, body, englishHead, isTranslated));
        }
        foreach (var (folder, head, body, englishHead, isTranslated) in pages)
        {
            if (!categories.TryGetValue(folder, out var category))
            {
                var (order, title) = Numbered(folder);
                categories[folder] = category = new HelpCategory { Title = title, Glyph = Glyph(null), Order = order };
            }
            // what a translation leaves out of its header is the English page's
            string? Field(string key) => head.GetValueOrDefault(key) ?? englishHead.GetValueOrDefault(key);
            string englishTitle = englishHead.GetValueOrDefault("title") ?? L.T("HelpLibrary.Untitled");
            category.Pages.Add(new HelpPage
            {
                Title = head.GetValueOrDefault("title") ?? englishTitle,
                EnglishTitle = englishTitle,
                Language = isTranslated ? language : Localisation.Loc.English,
                Glyph = Glyph(Field("icon")),
                Category = category,
                Summary = Field("summary") ?? "",
                Keywords = Field("keywords") ?? "",
                Order = int.TryParse(Field("order"), out int o) ? o : 999,
                Windows = (Field("windows") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Body = body,
            });
        }
        foreach (var category in categories.Values)
            category.Pages.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return [.. categories.Values.Where(c => c.Pages.Count > 0).OrderBy(c => c.Order).ThenBy(c => c.Title)];
    }

    /// <summary>"03 Looking at game files" → (3, "Looking at game files").</summary>
    private static (int Order, string Title) Numbered(string folder)
    {
        string name = folder.Split('/').Last();
        int space = name.IndexOf(' ');
        return space > 0 && int.TryParse(name[..space], out int order) ? (order, name[(space + 1)..]) : (999, name);
    }

    private static string Glyph(string? hex) =>
        hex is { Length: > 0 } && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code) ? char.ConvertFromUtf32(code) : "";

    /// <summary>The header block between --- lines (key: value), and the rest.</summary>
    private static (Dictionary<string, string> Head, string Body) Split(string text)
    {
        var head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (head, text);
        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (head, text);
        foreach (string line in text[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) head[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        int bodyStart = text.IndexOf('\n', end + 1);
        return (head, bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
    }

    /// <summary>
    /// A picture the pages show, by its file name, or null when the app hasn't got it. The app's language's own copy
    /// (settings.de.jpg for settings.jpg) comes first when there is one.
    /// </summary>
    public static BitmapImage? Image(string file)
    {
        string language = Localisation.Loc.Instance.Language;
        string own = Path.GetFileNameWithoutExtension(file) + "." + language + Path.GetExtension(file);
        return language != Localisation.Loc.English && LoadImage(own) is { } translated ? translated : LoadImage(file);
    }

    private static BitmapImage? LoadImage(string file)
    {
        lock (Images)
        {
            if (Images.TryGetValue(file, out var cached)) return cached;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ImagePrefix + file);
            BitmapImage? image = null;
            if (stream is not null)
            {
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
            }
            Images[file] = image;
            return image;
        }
    }

    private static readonly Dictionary<string, BitmapImage?> Images = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- searching

    /// <summary>
    /// The pages with every word of the query in them (title, summary, keywords, headings or text), best first: a word
    /// in the title counts most, then the keywords and summary, then the headings, then the text.
    /// </summary>
    public static List<HelpHit> Search(string query)
    {
        var words = Words(query);
        var hits = new List<HelpHit>();
        if (words.Count == 0) return hits;
        foreach (var page in Pages)
        {
            string head = $"{page.Title} {page.Summary} {page.Keywords} {page.Category.Title}";
            string text = page.PlainText;
            if (!words.All(w => head.Contains(w, StringComparison.OrdinalIgnoreCase) || text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int score = 0, count = 0;
            foreach (string w in words)
            {
                if (page.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 40;
                if (page.Keywords.Contains(w, StringComparison.OrdinalIgnoreCase) || page.Summary.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 15;
                if (page.Sections.Any(s => s.Contains(w, StringComparison.OrdinalIgnoreCase))) score += 8;
                int n = Count(text, w);
                count += n;
                score += Math.Min(n, 10);
            }
            hits.Add(new HelpHit(page, score, count, Snippet(text, words)));
        }
        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Page.Title)];
    }

    public static List<string> Words(string query) =>
        [.. query.Split((char[])[' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 || char.IsDigit(w[0])).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static int Count(string text, string word)
    {
        int n = 0;
        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>About a line of the text round the first place a word of the query is.</summary>
    private static string Snippet(string text, List<string> words)
    {
        int at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0) return text.Length > 140 ? text[..140].TrimEnd() + "…" : text;
        int start = Math.Max(0, at - 50);
        while (start > 0 && start < at && text[start] != ' ') start++;
        int end = Math.Min(text.Length, at + 110);
        while (end < text.Length && end > at && text[end - 1] != ' ') end--;
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }

    /// <summary>The markup's words without its marks: no headings' #, no **, no `, links as their text.</summary>
    public static string Plain(string body)
    {
        var sb = new StringBuilder();
        bool code = false, cards = false;
        foreach (string raw in body.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (line.StartsWith(":::", StringComparison.Ordinal)) { cards = line.StartsWith(":::cards", StringComparison.Ordinal); continue; }
            if (line.StartsWith("![", StringComparison.Ordinal) && !code) continue;
            // a card: its label and its text, not its icon
            if (cards && System.Text.RegularExpressions.Regex.Match(line, @"^- (\[\[[^\]]+\]\])\s*\|\s*\w+\s*\|\s*(.*)$") is { Success: true } card)
                line = card.Groups[1].Value + ": " + card.Groups[2].Value;
            line = line.TrimStart('#', '>', '-', '|', ' ');
            if (line.Length > 2 && char.IsDigit(line[0]) && line.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4) line = line[(line.IndexOf(". ", StringComparison.Ordinal) + 2)..];
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[\[([^\]|]+)\|([^\]]+)\]\]", "$2");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[\[([^\]]+)\]\]", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = line.Replace("**", "").Replace("`", "").Replace("{{", "").Replace("}}", "");
            if (line.Length > 0) sb.Append(line).Append(' ');
        }
        return sb.ToString();
    }
}
