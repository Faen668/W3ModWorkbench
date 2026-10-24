using System.Globalization;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Strings;

/// <summary>
/// Strings as REDkit's strings database exports and imports them (Export project database writes
/// <c>LocalEditorStringDataBaseW3_UTF8_mod_export.csv</c>): separated by semicolons, a header of
/// <c>ID;RESOURCE;PROPERTY;VOICEOVER;KEY</c> and a column for each language (EN, PL, DE…, as REDkit's database names
/// them), then a string a row in ID order with its text in each language's column. REDkit's import finds the columns
/// by their names and needs ID and at least one language, so only the languages there are text for are written: an
/// empty column would empty that language's text in REDkit. A field with a semicolon, a quote or a line break is
/// quoted, a quote in it doubled, as REDkit's CSV files are.
/// </summary>
public static class RedkitStringsCsv
{
    /// <summary>The columns before the languages', as REDkit writes them.</summary>
    private static readonly string[] InfoColumns = ["ID", "RESOURCE", "PROPERTY", "VOICEOVER", "KEY"];

    /// <summary>REDkit's own language that isn't the game's, passed over when reading.</summary>
    private const string DebugLanguage = "DEBUG";

    /// <summary>The languages in the order REDkit's database numbers them, which is the order it writes their columns in.</summary>
    private static readonly string[] Order = ["PL", "EN", "DE", "IT", "FR", "CZ", "ES", "ZH", "RU", "HU", "JP", "TR", "KR", "BR", "ESMX", "CN", "AR", "UA"];

    /// <summary>Whether a CSV is in REDkit's layout: its first line is a header starting with the ID column.</summary>
    public static bool Is(string csv)
    {
        var line = csv.AsSpan().TrimStart('﻿').TrimStart();
        return line.StartsWith("ID;", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One or more languages' strings as REDkit's CSV: a row for each string ID any of them has, with its key by name
    /// where one knows it. <paramref name="keysLeftOut"/> counts strings whose key is known only by its hash, which
    /// REDkit can't take (its KEY column is a name), so their KEY is left empty.
    /// </summary>
    public static string Write(IReadOnlyList<W3StringsFile> files, out int keysLeftOut)
    {
        var languages = files.Select(f => f.Language).DistinctBy(l => l.Handle).OrderBy(l => Rank(l.Handle)).ToList();
        var rows = new SortedDictionary<uint, (string? Key, bool HashOnly, Dictionary<string, string> Text)>();
        foreach (var file in files)
            foreach (var s in file.Strings)
            {
                if (!rows.TryGetValue(s.Id, out var row)) rows[s.Id] = row = (null, false, []);
                if (s.KeyName is not null) row = (s.KeyName, false, row.Text);
                else if (s.KeyHash != 0 && row.Key is null) row = (null, true, row.Text);
                row.Text[file.Language.Handle] = s.Text;
                rows[s.Id] = row;
            }
        var sb = new StringBuilder();
        sb.AppendJoin(';', InfoColumns.Concat(languages.Select(l => l.Handle.ToUpperInvariant()))).Append('\n');
        keysLeftOut = 0;
        foreach (var (id, row) in rows)
        {
            if (row.HashOnly && row.Key is null) keysLeftOut++;
            sb.Append(id.ToString(CultureInfo.InvariantCulture)).Append(";;;;").Append(Field(row.Key ?? ""));
            foreach (var language in languages)
                sb.Append(';').Append(Field(row.Text.GetValueOrDefault(language.Handle, "")));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads REDkit's CSV: each language column's strings, those with text in it. Rows and columns that can't be used
    /// are skipped and listed in the problems, by the line they start on.
    /// </summary>
    public static (Dictionary<W3Language, List<W3String>> Languages, List<W3Strings.CsvProblem> Problems) Read(string csv)
    {
        var problems = new List<W3Strings.CsvProblem>();
        var result = new Dictionary<W3Language, List<W3String>>();
        var rows = Rows(csv.TrimStart('﻿')).ToList();
        if (rows.Count == 0) return (result, problems);
        var header = rows[0].Fields;
        int idColumn = -1, keyColumn = -1;
        var languageColumns = new List<(int Column, W3Language Language)>();
        for (int c = 0; c < header.Count; c++)
        {
            string name = header[c].Trim();
            if (name.Equals("ID", StringComparison.OrdinalIgnoreCase)) idColumn = c;
            else if (name.Equals("KEY", StringComparison.OrdinalIgnoreCase)) keyColumn = c;
            else if (InfoColumns.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals(DebugLanguage, StringComparison.OrdinalIgnoreCase) || name.Length == 0) continue;
            else if (W3Strings.Language(name) is { } language) languageColumns.Add((c, language));
            else problems.Add(new(rows[0].Line, L.T("RedkitStringsCsv.UnknownLanguage", name)));
        }
        if (idColumn < 0)
        {
            problems.Add(new(rows[0].Line, L.T("RedkitStringsCsv.NoIdColumn")));
            return (result, problems);
        }
        foreach (var (_, language) in languageColumns) result.TryAdd(language, []);
        foreach (var (line, fields) in rows.Skip(1))
        {
            if (fields.All(f => f.Length == 0)) continue;
            string idText = idColumn < fields.Count ? fields[idColumn].Trim() : "";
            if (!uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                problems.Add(new(line, L.T("W3Strings.IsntStringID", idText)));
                continue;
            }
            string key = keyColumn >= 0 && keyColumn < fields.Count ? fields[keyColumn].Trim() : "";
            foreach (var (column, language) in languageColumns)
                if (column < fields.Count && fields[column].Length > 0)
                    result[language].Add(new W3String(id, key.Length > 0 ? W3Strings.HashKey(key) : 0, key.Length > 0 ? key : null, fields[column]));
        }
        return (result, problems);
    }

    private static int Rank(string handle)
    {
        int at = Array.IndexOf(Order, handle.ToUpperInvariant());
        return at < 0 ? Order.Length : at;
    }

    private static string Field(string text) =>
        text.AsSpan().IndexOfAny(';', '"', '\n') < 0 && !text.Contains('\r') ? text : "\"" + text.Replace("\"", "\"\"") + "\"";

    /// <summary>The CSV's rows, each with the line it starts on: fields split at semicolons, a quoted one taking in semicolons, line breaks and doubled quotes.</summary>
    private static IEnumerable<(int Line, List<string> Fields)> Rows(string csv)
    {
        int line = 1, at = 0;
        while (at < csv.Length)
        {
            int start = line;
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, endOfRow = false;
            while (at < csv.Length && !endOfRow)
            {
                char c = csv[at++];
                if (quoted)
                {
                    if (c == '"' && at < csv.Length && csv[at] == '"') { field.Append('"'); at++; }
                    else if (c == '"') quoted = false;
                    else if (c == '\r' && at < csv.Length && csv[at] == '\n') continue;
                    else
                    {
                        if (c == '\n') line++;
                        field.Append(c);
                    }
                }
                else if (c == '"' && field.Length == 0) quoted = true;
                else if (c == ';') { fields.Add(field.ToString()); field.Clear(); }
                else if (c == '\n') { line++; endOfRow = true; }
                else if (c != '\r') field.Append(c);
            }
            fields.Add(field.ToString());
            yield return (start, fields);
        }
    }
}
