using System.IO;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench.Core.Strings;

/// <summary>
/// REDkit's strings database (<c>r4data\LocalEditorStringDataBaseW3_UTF8.db</c>), an SQLite file with every string of
/// the game and its expansions in every language. Unlike the game's .w3strings it keeps each key's name, the
/// voiceover file a line is spoken in, and the scene, journal entry or item list each string belongs to.
/// <para>
/// Tables: LANGUAGES (ID, LANG), STRING_INFO (STRING_ID, RESOURCE, PROPERTY_NAME, VOICEOVER_NAME, STRING_KEY) and
/// STRINGS (STRING_ID, LANG, VERSION, TEXT), where a string's text in a language is the row with the highest VERSION.
/// </para>
/// </summary>
public static class RedkitStrings
{
    public const string FileName = "LocalEditorStringDataBaseW3_UTF8.db";

    /// <summary>The database in the REDkit that wcc_lite is from: r4data, two folders up from bin\x64_RedKit.</summary>
    public static string? ForWccLite(string? wccLite)
    {
        if (string.IsNullOrEmpty(wccLite) || Path.GetDirectoryName(wccLite) is not { } bin) return null;
        string path = Path.GetFullPath(Path.Combine(bin, "..", "..", "r4data", FileName));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The database of the REDkit set in Settings, or of one installed in the usual places.</summary>
    public static string? Find(string? wccLite) => ForWccLite(wccLite) ?? ForWccLite(ScriptCompiler.Find());

    /// <summary>Every string of one language in the database, with what the database knows about it.</summary>
    public static List<GameString> Read(string path, W3Language language, string source = "REDkit database")
    {
        using var db = SqliteFile.Open(path);
        if (!db.HasTable("LANGUAGES") || !db.HasTable("STRINGS") || !db.HasTable("STRING_INFO"))
            throw new InvalidDataException("This isn't a REDkit strings database: it has no LANGUAGES, STRINGS and STRING_INFO tables.");

        long lang = -1;
        db.Scan("LANGUAGES", row =>
        {
            if (string.Equals(row.Text(1), language.Handle, StringComparison.OrdinalIgnoreCase)) lang = row.RowId;
        });
        if (lang < 0) throw new InvalidDataException($"The database has no {language.Handle} strings.");

        var texts = new Dictionary<long, (long Version, string Text)>();
        db.Scan("STRINGS", row =>
        {
            if (row.Integer(1) != lang) return;
            long id = row.Integer(0), version = row.Integer(2);
            if (texts.TryGetValue(id, out var have) && have.Version >= version) return;
            texts[id] = (version, row.Text(3) ?? "");
        });

        string file = Path.GetFileName(path);
        var all = new List<GameString>(texts.Count);
        db.Scan("STRING_INFO", row =>
        {
            if (!texts.TryGetValue(row.RowId, out var text) || row.RowId is < 0 or > uint.MaxValue) return;
            string? key = Blank(row.Text(4));
            all.Add(new GameString((uint)row.RowId, key is null ? 0 : W3Strings.HashKey(key), text.Text, source, file)
            {
                KeyName = key,
                Voiceover = Blank(row.Text(3)),
                Property = Blank(row.Text(2)),
                Resource = Resource(row.Text(1)),
            });
        });
        all.Sort((a, b) => a.Id.CompareTo(b.Id));
        return all;
    }

    /// <summary>The columns are CHAR(n), padded with spaces.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// The resource a string belongs to, as the path when the database names one (<c>CStoryScene "quests\…\x.w2scene"</c>),
    /// otherwise as written ("CUSTOM" for strings made in the strings editor).
    /// </summary>
    private static string? Resource(string? text)
    {
        if (Blank(text) is not { } resource) return null;
        int quote = resource.IndexOf('"');
        if (quote >= 0 && resource.LastIndexOf('"') is int end && end > quote) return resource[(quote + 1)..end];
        return resource;
    }
}
