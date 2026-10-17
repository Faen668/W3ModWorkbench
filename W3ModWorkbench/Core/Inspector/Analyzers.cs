using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>Stable 64-bit hashing: string.GetHashCode changes from run to run, and these hashes are compared across files.</summary>
internal static class Hashing
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Of(string s)
    {
        ulong h = Offset;
        foreach (char c in s) h = (h ^ c) * Prime;
        return h;
    }

    public static ulong Combine(ulong h, ulong v) => (h ^ v) * Prime ^ (v >> 29);

    /// <summary>Hashes of every run of <paramref name="k"/> items, sorted and without repeats.</summary>
    public static ulong[] Shingles(List<ulong> items, int k)
    {
        int n = items.Count;
        if (n < 4) return [];
        if (n < k)
        {
            ulong whole = Offset;
            foreach (ulong v in items) whole = Combine(whole, v);
            return [whole];
        }
        var result = new ulong[n - k + 1];
        for (int i = 0; i < result.Length; i++)
        {
            ulong h = Offset;
            for (int j = 0; j < k; j++) h = Combine(h, items[i + j]);
            result[i] = h;
        }
        return Distinct(result);
    }

    public static ulong[] Distinct(ulong[] values)
    {
        Array.Sort(values);
        int w = 0;
        for (int r = 0; r < values.Length; r++)
            if (r == 0 || values[r] != values[r - 1]) values[w++] = values[r];
        return w == values.Length ? values : values[..w];
    }

    public static int Intersect(ulong[] a, ulong[] b)
    {
        int i = 0, j = 0, count = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { count++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return count;
    }

    /// <summary>Items of <paramref name="a"/> that <paramref name="remove"/> doesn't have; both sorted.</summary>
    public static ulong[] Except(ulong[] a, ulong[] remove)
    {
        if (remove.Length == 0 || a.Length == 0) return a;
        var result = new List<ulong>(a.Length);
        int j = 0;
        foreach (ulong v in a)
        {
            while (j < remove.Length && remove[j] < v) j++;
            if (j < remove.Length && remove[j] == v) continue;
            result.Add(v);
        }
        return [.. result];
    }
}

/// <summary>
/// Reads WitcherScript the way a plagiarism checker does: not the text, but the code. Comments and layout are set
/// aside, letter case is ignored, and the code is kept twice: as written, and as a shape with every name and value
/// replaced, so a copy with renamed variables still matches.
/// </summary>
public static class ScriptAnalyzer
{
    private const int RawRun = 8;
    private const int ShapeRun = 7;
    private const int MinFunctionTokens = 12;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "class", "struct", "enum", "state", "statemachine", "extends", "in", "function", "event", "var", "const", "default", "hint",
        "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "return", "new", "delete", "this", "super", "parent",
        "virtual_parent", "true", "false", "null", "public", "protected", "private", "import", "final", "abstract", "exec", "cleanup",
        "entry", "latent", "timer", "saved", "quest", "storyscene", "out", "optional", "editable", "inlined", "transient", "autobind",
        "single", "int", "float", "bool", "string", "name", "byte", "void", "array", "wrapmethod", "replacemethod", "addmethod",
        "addfield", "wrapped", "wrappedmethod",
    };

    private enum Kind : byte { Word, Number, Text, Mark }

    public static ScriptInfo Analyze(string source)
    {
        var raw = new List<string>(source.Length / 5);
        var kinds = new List<Kind>(raw.Capacity);
        var comments = new List<string>();
        var strings = new List<string>();
        Tokenize(source, raw, kinds, comments, strings);

        int n = raw.Count;
        var rawHashes = new List<ulong>(n);
        var shapeHashes = new List<ulong>(n);
        ulong whole = 0, wholeShape = 0;
        for (int i = 0; i < n; i++)
        {
            ulong r = Hashing.Of(raw[i]);
            ulong s = kinds[i] switch
            {
                Kind.Word when !Keywords.Contains(raw[i]) => 0x1111,
                Kind.Number => 0x1111, // a constant that compiled to its number is still the same value
                Kind.Text => 0x3333,
                _ => r,
            };
            rawHashes.Add(r);
            shapeHashes.Add(s);
            whole = Hashing.Combine(whole, r);
            wholeShape = Hashing.Combine(wholeShape, s);
        }

        var classes = new List<string>();
        var functions = new List<FunctionInfo>();
        FindDeclarations(raw, kinds, rawHashes, shapeHashes, classes, functions);

        return new ScriptInfo
        {
            TokenCount = n,
            RawHash = n == 0 ? 0 : whole,
            AbstractHash = n == 0 ? 0 : wholeShape,
            RawShingles = Hashing.Shingles(rawHashes, RawRun),
            AbstractShingles = Hashing.Shingles(shapeHashes, ShapeRun),
            Comments = comments,
            Strings = strings,
            Classes = classes,
            Functions = functions,
        };
    }

    private static void Tokenize(string text, List<string> tokens, List<Kind> kinds, List<string> comments, List<string> strings)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = n;
                AddComment(comments, text[(i + 2)..end]);
                i = end;
            }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) end = n;
                AddComment(comments, text[(i + 2)..end]);
                i = Math.Min(n, end + 2);
            }
            else if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < n && text[j] != c && text[j] != '\n')
                    j += text[j] == '\\' && j + 1 < n ? 2 : 1;
                string body = text[(i + 1)..Math.Min(j, n)];
                tokens.Add(c + body);
                kinds.Add(Kind.Text);
                if (c == '"' && body.Length >= 10 && body.Count(char.IsLetter) >= 4) strings.Add(body.Trim());
                i = Math.Min(n, j + 1);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                tokens.Add(text[i..j].ToLowerInvariant());
                kinds.Add(Kind.Word);
                i = j;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '.')) j++;
                tokens.Add(text[i..j].ToLowerInvariant());
                kinds.Add(Kind.Number);
                i = j;
            }
            else
            {
                tokens.Add(c.ToString());
                kinds.Add(Kind.Mark);
                i++;
            }
        }
    }

    private static void AddComment(List<string> comments, string raw)
    {
        string text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('*', '/', '-', '=', '#', ' ');
        // short or decorative comments are what everyone writes
        if (text.Length >= 24 && text.Count(char.IsLetter) >= 12) comments.Add(text);
    }

    private static void FindDeclarations(List<string> raw, List<Kind> kinds, List<ulong> rawHashes, List<ulong> shapeHashes,
        List<string> classes, List<FunctionInfo> functions)
    {
        int n = raw.Count, depth = 0;
        var owners = new Stack<(string Name, int Depth)>();
        string? pendingClass = null, annotated = null;

        for (int i = 0; i < n; i++)
        {
            string t = raw[i];
            if (kinds[i] == Kind.Mark)
            {
                if (t == "{")
                {
                    depth++;
                    if (pendingClass is not null) { owners.Push((pendingClass, depth)); pendingClass = null; }
                }
                else if (t == "}")
                {
                    if (owners.Count > 0 && owners.Peek().Depth == depth) owners.Pop();
                    depth--;
                }
                else if (t == "@" && i + 4 < n && kinds[i + 1] == Kind.Word && raw[i + 2] == "(" && kinds[i + 3] == Kind.Word)
                {
                    annotated = raw[i + 3];
                }
                continue;
            }
            if (kinds[i] != Kind.Word) continue;

            if (t is "class" or "struct" or "state" && i + 1 < n && kinds[i + 1] == Kind.Word)
            {
                pendingClass = raw[i + 1];
                classes.Add(raw[i + 1]);
            }
            else if (t == "function" && i + 1 < n && kinds[i + 1] == Kind.Word)
            {
                string owner = owners.Count > 0 ? owners.Peek().Name : annotated ?? "";
                string name = raw[i + 1];
                annotated = null;
                int open = i + 2;
                while (open < n && raw[open] != "{" && raw[open] != ";") open++;
                if (open >= n || raw[open] == ";") continue;

                int close = open, level = 0;
                for (; close < n; close++)
                {
                    if (raw[close] == "{") level++;
                    else if (raw[close] == "}" && --level == 0) break;
                }
                int length = Math.Min(close, n) - open - 1;
                if (length < MinFunctionTokens) continue;
                ulong r = 0, s = 0;
                for (int k = open + 1; k < close && k < n; k++)
                {
                    r = Hashing.Combine(r, rawHashes[k]);
                    s = Hashing.Combine(s, shapeHashes[k]);
                }
                functions.Add(new FunctionInfo(owner, name, r, s, length));
            }
        }
    }
}

/// <summary>Reads XML (mod menus especially) and other text as a set of lines to compare.</summary>
public static class LineAnalyzer
{
    public static LineSetInfo AnalyzeXml(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException)
        {
            return AnalyzeText(text);
        }

        var lines = new List<string>();
        var ids = new List<string>();
        var comments = new List<string>();
        if (doc.Root is not null) Walk(doc.Root, "", lines, ids, comments);
        return Build(lines, ids, comments);
    }

    public static LineSetInfo AnalyzeText(string text)
    {
        var lines = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length >= 3) lines.Add(trimmed);
        }
        return Build(lines, [], []);
    }

    private static LineSetInfo Build(List<string> lines, List<string> ids, List<string> comments)
    {
        ulong whole = 0;
        var hashes = new List<ulong>(lines.Count);
        foreach (string line in lines)
        {
            ulong h = Hashing.Of(line);
            hashes.Add(h);
            whole = Hashing.Combine(whole, h);
        }
        return new LineSetInfo
        {
            CanonicalHash = lines.Count == 0 ? 0 : whole,
            Lines = Hashing.Distinct([.. hashes]),
            Ids = ids,
            Comments = comments,
        };
    }

    private static void Walk(XElement element, string parent, List<string> lines, List<string> ids, List<string> comments)
    {
        string path = parent + "/" + element.Name.LocalName;
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).ToList();
        string text = string.Join(' ', element.Nodes().OfType<XText>().Select(t => t.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Aggregate("", (a, w) => a.Length == 0 ? w : a + " " + w);
        lines.Add(path + "|" + string.Join(';', attributes.Select(a => a.Name.LocalName + "=" + a.Value.Trim())) + "|" + text);

        foreach (var a in attributes)
        {
            string name = a.Name.LocalName;
            string value = a.Value.Trim();
            if ((name is "id" or "displayName" or "name") && value.Length >= 4 && value.Any(char.IsLetter)) ids.Add(value);
        }
        foreach (var node in element.Nodes())
        {
            if (node is XComment c)
            {
                string note = string.Join(' ', c.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (note.Length >= 24 && note.Count(char.IsLetter) >= 12) comments.Add(note);
            }
            else if (node is XElement child)
            {
                Walk(child, path, lines, ids, comments);
            }
        }
    }
}
