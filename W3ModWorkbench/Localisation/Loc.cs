using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace W3ModWorkbench.Localisation;

/// <summary>A language the app's own text can be shown in: its tag (de, pt-BR), and its name as its speakers write it.</summary>
public sealed record AppLanguage(string Tag, string Name, string EnglishName);

/// <summary>
/// The app's own text in the language chosen, from the string tables in Localisation\Strings (en.json is the source,
/// every other language a file of its own, LOCALISATION.md says how). XAML binds to it with <c>{l:T Key}</c>, which
/// changes the moment the language does; code asks <see cref="L.T(string, object?[])"/>.
/// <para>
/// A string is looked up in the language shown, then in English, then the key itself is shown, so a missing string is
/// seen rather than blank. A value can name its placeholders (<c>{count:N0} files in {folder}</c>): the arguments
/// fill them in the order the English string names them, so a translation can put them in any order. A value can also
/// be plural forms (<c>{ "one": "…", "other": "…" }</c>), picked by the {count} argument (else the first number given, which
/// needn't be shown) and the language's rules.
/// </para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>The source language, which every key has.</summary>
    public const string English = "en";

    /// <summary>The pseudo-locale: English, accented and lengthened, to find text that isn't localised and layouts that clip.</summary>
    public const string Pseudo = "qps-ploc";

    private const string ResourcePrefix = "Strings/";

    private readonly Dictionary<string, Entry> _english;
    private Dictionary<string, Entry> _current;
    private readonly Dictionary<string, Template> _templates = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes, for code that fills text in itself to fill it again.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The language shown, by its tag.</summary>
    public string Language { get; private set; } = English;

    private Loc()
    {
        _english = Read(English) ?? [];
        _current = _english;
    }

    /// <summary>The string for a key in the language shown, as XAML's bindings ask for it.</summary>
    public string this[string key] => L.T(key);

    // ---------------------------------------------------------------- languages

    /// <summary>
    /// The languages there are tables for: the ones built in, and any in a Languages folder beside the app (a translation
    /// being tried out before it's built in). The pseudo-locale is listed in Debug builds.
    /// </summary>
    public static IReadOnlyList<AppLanguage> Available => _available ??= FindLanguages();
    private static IReadOnlyList<AppLanguage>? _available;

    private static List<AppLanguage> FindLanguages()
    {
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                tags.Add(name[ResourcePrefix.Length..^5]);
        if (Directory.Exists(LooseFolder))
            foreach (string file in Directory.EnumerateFiles(LooseFolder, "*.json"))
                tags.Add(Path.GetFileNameWithoutExtension(file));
        var list = tags.Where(t => t != English).Select(Describe).OrderBy(l => l.EnglishName, StringComparer.OrdinalIgnoreCase).ToList();
        list.Insert(0, Describe(English));
#if DEBUG
        list.Add(Describe(Pseudo));
#endif
        return list;
    }

    private static AppLanguage Describe(string tag)
    {
        if (tag == Pseudo) return new AppLanguage(tag, "[Ƥšéûðö ~~]", L.T("Settings.Language.Pseudo"));
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            string native = culture.NativeName;
            return new AppLanguage(tag, native.Length > 0 ? char.ToUpper(native[0], culture) + native[1..] : tag, culture.EnglishName);
        }
        catch (CultureNotFoundException)
        {
            return new AppLanguage(tag, tag, tag);
        }
    }

    /// <summary>A Languages folder beside the app: tables put there are listed without building them in.</summary>
    public static string LooseFolder => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>
    /// The language a setting means: the one it names when there's a table for it, else for an empty setting the Windows
    /// display language (or its parent, de for de-AT) when the app has it, else English.
    /// </summary>
    public static string Resolve(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
            return Available.FirstOrDefault(l => l.Tag.Equals(setting, StringComparison.OrdinalIgnoreCase))?.Tag
                ?? (setting.Equals(Pseudo, StringComparison.OrdinalIgnoreCase) ? Pseudo : English);
        for (var culture = CultureInfo.InstalledUICulture; culture != CultureInfo.InvariantCulture; culture = culture.Parent)
            if (Available.FirstOrDefault(l => l.Tag.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)) is { } match && match.Tag != Pseudo)
                return match.Tag;
        return English;
    }

    /// <summary>Shows the app's text in a language, by its tag. Bound text changes at once.</summary>
    public void Apply(string tag)
    {
        if (tag == Language) return;
        _current = tag == English || tag == Pseudo ? _english : Read(tag) ?? _english;
        Language = tag;
        lock (_templates) _templates.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        LanguageChanged?.Invoke();
    }

    // ---------------------------------------------------------------- looking strings up

    /// <summary>Whether the English table has the key.</summary>
    public bool Has(string key) => _english.ContainsKey(key);

    /// <summary>The string for a key, with its placeholders filled in.</summary>
    internal string Get(string key, object?[] args)
    {
        if (!_english.TryGetValue(key, out var source)) return key;
        var entry = _current.TryGetValue(key, out var found) ? found : source;
        string text;
        if (entry.Forms is null && source.Forms is null)
            text = entry.Text!;
        else
        {
            // the {count} argument, else the first that's a number (which needn't be shown: it can come after the rest)
            int at = SourceNames(source).IndexOf("count");
            if (at < 0 || at >= args.Length) at = Array.FindIndex(args, a => a is int or long or short or byte or uint or ulong or double or float or decimal);
            long n = at >= 0 ? Number(args[at]) : 0;
            string category = PluralCategory(entry == source ? English : Language, n);
            text = Pick(entry, category) ?? Pick(source, PluralCategory(English, n)) ?? source.Text ?? key;
        }
        if (Language == Pseudo) text = PseudoText(text);
        return args.Length == 0 && text.IndexOf('{') < 0 ? text : Fill(key, source, text, args);
    }

    private static string? Pick(Entry entry, string category) =>
        entry.Forms is { } forms ? forms.GetValueOrDefault(category) ?? forms.GetValueOrDefault("other") : entry.Text;

    private static long Number(object? value) => value switch
    {
        int i => i, long l => l, short s => s, byte b => b, uint u => u, ulong ul => (long)ul,
        double d => (long)Math.Abs(d), float f => (long)Math.Abs(f), decimal m => (long)Math.Abs(m),
        _ => 0,
    };

    /// <summary>
    /// Fills in a string's placeholders. The names in the English string give each its argument by order; the
    /// translation's names point at the same ones. {{ and }} are a plain brace.
    /// </summary>
    private string Fill(string key, Entry source, string text, object?[] args)
    {
        Template template;
        string cacheKey = key + "\u0001" + text;
        lock (_templates)
        {
            if (!_templates.TryGetValue(cacheKey, out template!))
            {
                var names = SourceNames(source);
                string format = Placeholder.Replace(text, m =>
                {
                    int index = names.IndexOf(m.Groups[1].Value);
                    return index < 0 ? m.Value.Replace("{", "{{").Replace("}", "}}") : "{" + index + m.Groups[2].Value + "}";
                });
                _templates[cacheKey] = template = new Template(format);
            }
        }
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template.Format, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    private sealed record Template(string Format);

    /// <summary>An English string's placeholder names, in the order the arguments fill them: the order they first come.</summary>
    private static List<string> SourceNames(Entry source)
    {
        var names = PlaceholderNames(source.Text ?? source.Forms?.GetValueOrDefault("other") ?? "");
        if (source.Forms is { } forms)
            foreach (string form in forms.Values)
                foreach (string name in PlaceholderNames(form))
                    if (!names.Contains(name)) names.Add(name);
        return names;
    }

    /// <summary>{name}, {name:N0} or {name,-8}, not {{ or }}.</summary>
    private static readonly Regex Placeholder = new(@"(?<!\{)\{([A-Za-z_][A-Za-z0-9_]*)((?:,-?\d+)?(?::[^{}]*)?)\}(?!\})", RegexOptions.Compiled);

    public static List<string> PlaceholderNames(string text)
    {
        var names = new List<string>();
        foreach (Match m in Placeholder.Matches(text))
            if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
        return names;
    }

    // ---------------------------------------------------------------- the tables

    /// <summary>A key's value: one string, or its plural forms by CLDR category (zero, one, two, few, many, other).</summary>
    private sealed class Entry
    {
        public string? Text;
        public Dictionary<string, string>? Forms;
    }

    /// <summary>A language's table, from the Languages folder beside the app first (to try a translation out), else the one built in.</summary>
    private static Dictionary<string, Entry>? Read(string tag)
    {
        try
        {
            string loose = Path.Combine(LooseFolder, tag + ".json");
            if (File.Exists(loose)) return Parse(File.ReadAllBytes(loose));
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + tag + ".json");
            if (stream is null) return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Parse(memory.ToArray());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A table: keys to strings or plural forms. Keys starting with $ are about the table, not strings.</summary>
    private static Dictionary<string, Entry> Parse(byte[] json)
    {
        var table = new Dictionary<string, Entry>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('$')) continue;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                if (property.Value.GetString() is { Length: > 0 } text) table[property.Name] = new Entry { Text = text };
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var forms = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var form in property.Value.EnumerateObject())
                    if (form.Value.ValueKind == JsonValueKind.String && form.Value.GetString() is { Length: > 0 } text) forms[form.Name] = text;
                if (forms.Count > 0) table[property.Name] = new Entry { Forms = forms };
            }
        }
        return table;
    }

    // ---------------------------------------------------------------- plurals

    /// <summary>
    /// The CLDR plural category of a whole number in a language: one, few, many, two, zero or other. Each table gives
    /// the forms its language has; a language not listed here has one and other, as English does.
    /// </summary>
    public static string PluralCategory(string tag, long n)
    {
        n = Math.Abs(n);
        long mod10 = n % 10, mod100 = n % 100;
        string language = tag.Split('-')[0].ToLowerInvariant();
        switch (language)
        {
            case "ja" or "zh" or "ko" or "th" or "vi" or "id" or "ms":
                return "other";
            case "fr" or "hy" or "kab":
                return n is 0 or 1 ? "one" : n != 0 && n % 1_000_000 == 0 ? "many" : "other";
            case "pt" when tag.Equals("pt-PT", StringComparison.OrdinalIgnoreCase):
                return n == 1 ? "one" : "other";
            case "pt":
                return n is 0 or 1 ? "one" : n != 0 && n % 1_000_000 == 0 ? "many" : "other";
            case "es" or "it" or "ca":
                return n == 1 ? "one" : n != 0 && n % 1_000_000 == 0 ? "many" : "other";
            case "cs" or "sk":
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other";
            case "pl":
                return n == 1 ? "one" : mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "many";
            case "ru" or "uk" or "be":
                return mod10 == 1 && mod100 != 11 ? "one"
                    : mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "many";
            case "hr" or "sr" or "bs":
                return mod10 == 1 && mod100 != 11 ? "one"
                    : mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "other";
            case "lt":
                return mod10 == 1 && mod100 is not (>= 11 and <= 19) ? "one"
                    : mod10 is >= 2 and <= 9 && mod100 is not (>= 11 and <= 19) ? "few" : "other";
            case "lv":
                return mod10 == 0 || mod100 is >= 11 and <= 19 ? "zero" : mod10 == 1 && mod100 != 11 ? "one" : "other";
            case "ro":
                return n == 1 ? "one" : n == 0 || mod100 is >= 2 and <= 19 ? "few" : "other";
            case "sl":
                return mod100 == 1 ? "one" : mod100 == 2 ? "two" : mod100 is 3 or 4 ? "few" : "other";
            case "ar":
                return n == 0 ? "zero" : n == 1 ? "one" : n == 2 ? "two"
                    : mod100 is >= 3 and <= 10 ? "few" : mod100 is >= 11 and <= 99 ? "many" : "other";
            case "he":
                return n == 1 ? "one" : n == 2 ? "two" : "other";
            case "ga":
                return n == 1 ? "one" : n == 2 ? "two" : n is >= 3 and <= 6 ? "few" : n is >= 7 and <= 10 ? "many" : "other";
            case "cy":
                return n switch { 0 => "zero", 1 => "one", 2 => "two", 3 => "few", 6 => "many", _ => "other" };
            default:
                return n == 1 ? "one" : "other";
        }
    }

    // ---------------------------------------------------------------- the pseudo-locale

    private const string PseudoFrom = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string PseudoTo = "àƀçðéƒĝĥîĵķļɱñöþǫŕšţûṽŵẋýžÀƁÇÐÉƑĜĤÎĴĶĻṀÑÖÞǪŔŠŢÛṼŴẊÝŽ";

    /// <summary>
    /// "[Šéţţîñĝš ~~~]": the letters accented, about a third longer, in brackets, so unlocalised text stands out and
    /// clipped text shows a missing bracket. Placeholders, {{ }} and \n stay as they are.
    /// </summary>
    public static string PseudoText(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        sb.Append('[');
        int letters = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                // a placeholder, or a doubled brace, goes through as it is
                if (i + 1 < text.Length && text[i + 1] == '{') { sb.Append("{{"); i++; continue; }
                int end = text.IndexOf('}', i);
                if (end < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append("}}"); i++; continue; }
            int at = PseudoFrom.IndexOf(c);
            sb.Append(at >= 0 ? PseudoTo[at] : c);
            if (char.IsLetter(c)) letters++;
        }
        int pad = Math.Max(1, (letters + 2) / 3);
        sb.Append(' ').Append('~', pad).Append(']');
        return sb.ToString();
    }
}

/// <summary>The app's own text in code: <c>L.T("Settings.Title")</c>, <c>L.T("Settings.Recent.Count", count)</c>.</summary>
public static class L
{
    /// <summary>
    /// The string for <paramref name="key"/> in the language shown, its placeholders filled from
    /// <paramref name="args"/> in the order the English string names them. For a plural string the {count} argument picks
    /// the form, or else the first number given (which can come after the rest without being shown).
    /// </summary>
    public static string T(string key, params object?[] args) => Loc.Instance.Get(key, args);
}
