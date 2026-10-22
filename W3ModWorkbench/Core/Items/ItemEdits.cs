using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Items;

/// <summary>One value a project changes, against the game's: shown beside the field, and listed before saving.</summary>
public sealed record ItemChange(DefKind Kind, string Name, string Field, string? Game, string? Now);

/// <summary>
/// A project's changes to the game's item definitions, and how they're saved, as the Remastered's XML overrides have
/// them: items and abilities in a DLC of the project's own (<c>dlc\dlcNAME</c> in the workspace, which Build makes into
/// a DLC with a .reddlc mounting it), an item's changed attributes as <c>on_conflict="extend"</c> and anything else as
/// <c>on_conflict="replace"</c>; crafting schematics, alchemy recipes and loot tables, which can't be overridden, as
/// copies of the game's files with only the changed definition written anew. New Game+ gets the same changes, made to
/// its own copies, so its own values (its stronger stats) stay.
/// </summary>
public sealed class ItemEdits
{
    private readonly ItemDatabase _db;
    private readonly WorkbenchProject _project;

    /// <summary>The definitions the project changes: its working copy of each, by kind and name (normal game).</summary>
    private readonly Dictionary<(DefKind, string), XElement> _working = [];

    public ItemEdits(ItemDatabase db, WorkbenchProject project)
    {
        _db = db;
        _project = project;
        Read();
    }

    /// <summary>The project's DLC folder name: dlc and the mod's name, as the game's DLC folders are named.</summary>
    public static string DlcName(WorkbenchProject project)
    {
        string mod = project.ModFolderName;
        string bare = mod.StartsWith("mod", StringComparison.OrdinalIgnoreCase) ? mod[3..] : mod;
        return "dlc" + new string([.. bare.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
    }

    /// <summary>The override file's game path, for the normal game or New Game+.</summary>
    public static string OverridePath(WorkbenchProject project, bool newGamePlus)
    {
        string dlc = DlcName(project);
        return $@"dlc\{dlc}\data\gameplay\{(newGamePlus ? "items_plus" : "items")}\{dlc}.xml";
    }

    /// <summary>Whether a kind of definition is saved as an override (true) or in a copy of its file (false).</summary>
    public static bool Overridable(DefKind kind) => kind is DefKind.Item or DefKind.Ability;

    // ---------------------------------------------------------------- reading what the project has

    private void Read()
    {
        // overrides: items and abilities with on_conflict, in the project's DLC
        string file = _project.GameFilePath(OverridePath(_project, false));
        if (File.Exists(file))
        {
            var doc = ItemDatabase.Parse(ItemDatabase.Decode(File.ReadAllBytes(file)));
            foreach (var element in doc.Descendants().Where(e => e.Name.LocalName is "item" or "ability"))
            {
                var kind = element.Name.LocalName == "item" ? DefKind.Item : DefKind.Ability;
                if (element.Attribute("name")?.Value is not { Length: > 0 } name || _db.Find(kind, name) is not { } game) continue;
                string mode = element.Attribute("on_conflict")?.Value ?? "";
                var copy = new XElement(element);
                copy.Attribute("on_conflict")?.Remove();
                if (mode.Equals("extend", StringComparison.OrdinalIgnoreCase))
                {
                    // the game's definition with the attributes the project sets
                    var merged = new XElement(game.Element);
                    foreach (var a in copy.Attributes()) merged.SetAttributeValue(a.Name, a.Value);
                    copy = merged;
                }
                _working[(kind, game.Name)] = copy;
            }
        }
        // copies of the game's files holding changed schematics, recipes and loot tables
        foreach (var path in _db.Files.Keys.Where(p => !ItemDatabase.IsNewGamePlus(p)))
        {
            string copy = _project.GameFilePath(path);
            if (!File.Exists(copy)) continue;
            var mine = new ItemDatabase();
            try { mine.Add(path, ItemDatabase.Decode(File.ReadAllBytes(copy))); }
            catch (XmlException) { continue; }
            // only the definition the editor shows for a name (the first, in the first file that has it) is one it edits;
            // a second of the same name, or one in another file, keeps whatever the copy has
            var defs = mine.All.Where(d => !Overridable(d.Kind)).ToList();
            foreach (var def in defs)
            {
                bool first = defs.TakeWhile(d => d != def).All(d => d.Kind != def.Kind || !d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase));
                if (first && _db.Find(def.Kind, def.Name) is { } game && game.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase)
                    && !XNode.DeepEquals(Normal(game.Element), Normal(def.Element)))
                    _working[(def.Kind, game.Name)] = new XElement(def.Element);
            }
        }
    }

    /// <summary>An element without its whitespace and comments, to compare two copies by what they say.</summary>
    private static XElement Normal(XElement e)
    {
        var copy = new XElement(e.Name, e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => new XAttribute(a)));
        foreach (var node in e.Nodes())
            if (node is XElement child) copy.Add(Normal(child));
            else if (node is XText text && text.Value.Trim().Length > 0) copy.Add(new XText(text.Value.Trim()));
        return copy;
    }

    // ---------------------------------------------------------------- working copies

    /// <summary>The definition as the project has it: its working copy when it changes it, else the game's.</summary>
    public XElement Current(XmlDef def) => _working.TryGetValue((def.Kind, def.Name), out var mine) ? mine : def.Element;

    /// <summary>A working copy to change, made from the game's on first use.</summary>
    public XElement Edit(XmlDef def)
    {
        if (!_working.TryGetValue((def.Kind, def.Name), out var mine))
            _working[(def.Kind, def.Name)] = mine = new XElement(def.Element);
        return mine;
    }

    /// <summary>Puts a definition back as the game has it.</summary>
    public void Revert(XmlDef def) => _working.Remove((def.Kind, def.Name));

    public bool IsChanged(XmlDef def) => _working.TryGetValue((def.Kind, def.Name), out var mine) && !XNode.DeepEquals(Normal(def.Element), Normal(mine));

    /// <summary>Every value the project changes, definition by definition.</summary>
    public List<ItemChange> Changes()
    {
        var changes = new List<ItemChange>();
        foreach (var ((kind, name), mine) in _working)
        {
            if (_db.Find(kind, name) is not { } game) continue;
            foreach (var (field, was, now) in Diff(game.Element, mine)) changes.Add(new ItemChange(kind, name, field, was, now));
        }
        return changes;
    }

    /// <summary>The fields two copies of a definition differ in: its attributes, and each child's (by its tag and place).</summary>
    public static IEnumerable<(string Field, string? Was, string? Now)> Diff(XElement game, XElement mine, string prefix = "")
    {
        foreach (var name in game.Attributes().Select(a => a.Name).Union(mine.Attributes().Select(a => a.Name)))
        {
            string? was = game.Attribute(name)?.Value, now = mine.Attribute(name)?.Value;
            if (was != now) yield return (prefix + name.LocalName, was, now);
        }
        var gameChildren = Keyed(game);
        var mineChildren = Keyed(mine);
        foreach (var key in gameChildren.Keys.Union(mineChildren.Keys))
        {
            gameChildren.TryGetValue(key, out var g);
            mineChildren.TryGetValue(key, out var m);
            string field = prefix + key;
            if (g is null || m is null) yield return (field, g is null ? null : Text(g), m is null ? null : Text(m));
            else if (!g.HasElements && !m.HasElements && g.Value.Trim() != m.Value.Trim()) yield return (field, g.Value.Trim(), m.Value.Trim());
            else foreach (var d in Diff(g, m, field + ".")) yield return d;
        }
    }

    /// <summary>A definition's children by their tag and their place among those of that tag: weight, a[1], ingredient[2].</summary>
    private static Dictionary<string, XElement> Keyed(XElement e)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var group in e.Elements().GroupBy(c => c.Name.LocalName))
        {
            int i = 0;
            foreach (var child in group) result[group.Count() == 1 ? group.Key : $"{group.Key}[{++i}]"] = child;
        }
        return result;
    }

    private static string Text(XElement e) => e.HasElements || e.HasAttributes ? e.ToString(SaveOptions.DisableFormatting) : e.Value.Trim();

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Writes the project's changes: the override files (normal and New Game+) and the copies of the game's files that
    /// hold changed schematics, recipes and loot. A file left with no change is removed. Returns the files written.
    /// </summary>
    public List<string> Save(bool newGamePlus = true)
    {
        var written = new List<string>();
        foreach (var (key, mine) in _working.ToList())
            if (_db.Find(key.Item1, key.Item2) is { } game && XNode.DeepEquals(Normal(game.Element), Normal(mine))) _working.Remove(key);

        foreach (bool plus in newGamePlus ? new[] { false, true } : [false])
        {
            string path = OverridePath(_project, plus);
            var definitions = new List<XElement>();
            foreach (var ((kind, name), mine) in _working.Where(w => Overridable(w.Key.Item1)).OrderBy(w => w.Key.Item1).ThenBy(w => w.Key.Item2, StringComparer.OrdinalIgnoreCase))
            {
                var game = _db.Find(kind, name)!;
                var target = plus ? _db.Find(kind, name, true) : game;
                if (target is null) continue;
                var element = plus ? Apply(game.Element, mine, target.Element) : mine;
                definitions.Add(Override(kind, plus ? target.Element : game.Element, element));
            }
            written.AddRange(WriteOrRemove(path, definitions.Count == 0 ? null : OverrideDocument(definitions)));
        }

        // the files whose schematics, recipes or loot the project changes: the project's copy of each (or the game's
        // file, the first time), with those definitions written anew and everything else kept as it is
        var files = _working.Keys.Where(k => !Overridable(k.Item1))
            .SelectMany(k => _db.Named(k.Item1, k.Item2).Concat(newGamePlus ? _db.Named(k.Item1, k.Item2, true) : []))
            .Select(d => d.GamePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(_db.Files.Keys.Where(f => File.Exists(_project.GameFilePath(f))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string file in files)
        {
            bool plus = ItemDatabase.IsNewGamePlus(file);
            string copy = _project.GameFilePath(file);
            string text = File.Exists(copy) ? ItemDatabase.Decode(File.ReadAllBytes(copy)) : _db.Files[file];
            var here = new ItemDatabase();
            try { here.Add(file, text); }
            catch (XmlException) { continue; }
            bool changed = false;
            // each definition here paired with the game's in this file by name and place (a file can name two loot tables alike)
            var defs = here.All.Where(d => !Overridable(d.Kind)).ToList();
            var gameDefs = _db.All.Where(d => d.GamePath.Equals(file, StringComparison.OrdinalIgnoreCase) && !Overridable(d.Kind)).ToList();
            int Place(List<XmlDef> list, XmlDef def) => list.TakeWhile(d => d != def).Count(d => d.Kind == def.Kind && d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var def in defs.OrderByDescending(d => ((IXmlLineInfo)d.Element).LineNumber))
            {
                int place = Place(defs, def);
                var gameHere = gameDefs.Where(d => d.Kind == def.Kind && d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)).Skip(place).FirstOrDefault();
                if (gameHere is null) continue;
                // what the project wants here: for the definition it edits (and New Game+'s copy of it), its working copy;
                // for everything else, the game's own
                var game = _db.Find(def.Kind, def.Name);
                bool edited = game is not null && place == 0 && _working.ContainsKey((def.Kind, game.Name))
                    && file.Equals(plus ? PlusPath(game.GamePath) : game.GamePath, StringComparison.OrdinalIgnoreCase);
                var element = edited ? (plus ? Apply(game!.Element, _working[(def.Kind, game.Name)], gameHere.Element) : _working[(def.Kind, game!.Name)]) : gameHere.Element;
                if (XNode.DeepEquals(Normal(def.Element), Normal(element))) continue;
                text = Patch(text, def.Element, element);
                changed = true;
            }
            // a copy that's become the game's file again has nothing to give the mod
            if (Same(text, _db.Files[file]))
            {
                if (File.Exists(copy))
                {
                    FileSafety.CheckWrite(copy);
                    File.Delete(copy);
                }
            }
            else if (changed || !File.Exists(copy)) written.AddRange(WriteOrRemove(file, text));
        }
        return written;
    }

    /// <summary>
    /// An item whose attributes alone changed extends the game's with just those; anything else replaces it whole.
    /// An ability always replaces, as the stats in it aren't attributes of its own.
    /// </summary>
    private static XElement Override(DefKind kind, XElement game, XElement mine)
    {
        bool childrenSame = XNode.DeepEquals(new XElement("c", Normal(game).Elements()), new XElement("c", Normal(mine).Elements()));
        if (kind == DefKind.Item && childrenSame)
        {
            var extend = new XElement(mine.Name, new XAttribute("name", mine.Attribute("name")!.Value), new XAttribute("on_conflict", "extend"));
            foreach (var a in mine.Attributes().Where(a => a.Name.LocalName != "name" && game.Attribute(a.Name)?.Value != a.Value))
                extend.SetAttributeValue(a.Name, a.Value);
            return extend;
        }
        var replace = new XElement(mine);
        replace.Attribute("on_conflict")?.Remove();
        var attributes = replace.Attributes().ToList();
        replace.RemoveAttributes();
        replace.Add(attributes.Take(1), new XAttribute("on_conflict", "replace"), attributes.Skip(1));
        return replace;
    }

    private static XDocument OverrideDocument(List<XElement> definitions) =>
        new(new XDeclaration("1.0", "UTF-8", null),
            new XElement("redxml", new XElement("definitions",
                new XElement("items", definitions.Where(d => d.Name.LocalName == "item")),
                new XElement("abilities", definitions.Where(d => d.Name.LocalName == "ability")))));

    /// <summary>
    /// The change from <paramref name="game"/> to <paramref name="mine"/>, made to <paramref name="target"/> (New Game+'s
    /// copy): its attributes and children's attributes and text changed as they were, children added and taken away.
    /// </summary>
    public static XElement Apply(XElement game, XElement mine, XElement target)
    {
        var result = new XElement(target);
        foreach (var name in game.Attributes().Select(a => a.Name).Union(mine.Attributes().Select(a => a.Name)))
            if (game.Attribute(name)?.Value != mine.Attribute(name)?.Value) result.SetAttributeValue(name, mine.Attribute(name)?.Value);
        var g = Keyed(game);
        var m = Keyed(mine);
        var t = Keyed(result);
        foreach (var key in g.Keys.Union(m.Keys))
        {
            g.TryGetValue(key, out var gc);
            m.TryGetValue(key, out var mc);
            t.TryGetValue(key, out var tc);
            if (gc is not null && mc is null) tc?.Remove();
            else if (gc is null && mc is not null) result.Add(new XElement(mc));
            else if (gc is not null && mc is not null && !XNode.DeepEquals(Normal(gc), Normal(mc)))
            {
                if (tc is null) result.Add(new XElement(mc));
                else if (!gc.HasElements && !mc.HasElements && gc.Value.Trim() != mc.Value.Trim() && !mc.HasAttributes && !gc.HasAttributes) tc.Value = mc.Value;
                else tc.ReplaceWith(Apply(gc, mc, tc));
            }
        }
        return result;
    }

    /// <summary>
    /// The file's text with one definition written anew, everything around it kept as it was: the element is found by
    /// the line it starts on, and ends at its matching end tag.
    /// </summary>
    public static string Splice(string text, XElement old, XElement now)
    {
        var info = (IXmlLineInfo)old;
        int lineStart = 0;
        for (int line = 1; line < info.LineNumber && lineStart >= 0; line++) lineStart = text.IndexOf('\n', lineStart) + 1;
        string tag = old.Name.LocalName;
        int start = text.IndexOf("<" + tag, lineStart, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException(L.T("ItemEdits.NotFoundAt", tag, info.LineNumber));
        int end = ElementEnd(text, start, tag);
        // the new element indented as the old one was
        int indentFrom = text.LastIndexOf('\n', start) + 1;
        string indent = text[indentFrom..start];
        string written = now.ToString(SaveOptions.None).Replace("\r\n", "\n").Replace("\n", "\n" + indent).Replace("\n", text.Contains("\r\n") ? "\r\n" : "\n");
        return text[..start] + written + text[end..];
    }

    /// <summary>
    /// The file's text with one definition changed as little as it can be: when only values changed (attributes, or
    /// the text of an element such as a tag list), just those are written, and the game's layout, comments and blank
    /// lines stay; when entries were added or taken away, the definition is written anew (<see cref="Splice"/>).
    /// </summary>
    public static string Patch(string text, XElement old, XElement now)
    {
        var olds = old.DescendantsAndSelf().ToList();
        var nows = now.DescendantsAndSelf().ToList();
        if (olds.Count != nows.Count || olds.Zip(nows).Any(p => p.First.Name != p.Second.Name || p.First.HasElements != p.Second.HasElements))
            return Splice(text, old, now);
        var info = (IXmlLineInfo)old;
        int lineStart = 0;
        for (int line = 1; line < info.LineNumber && lineStart >= 0; line++) lineStart = text.IndexOf('\n', lineStart) + 1;
        int start = text.IndexOf("<" + old.Name.LocalName, lineStart, StringComparison.Ordinal);
        int end = ElementEnd(text, start, old.Name.LocalName);
        var tags = StartTags(text, start, end);
        if (tags.Count != olds.Count) return Splice(text, old, now);
        // last first, so what's written doesn't move what's still to be found
        for (int i = olds.Count - 1; i >= 0; i--)
        {
            var (o, n) = (olds[i], nows[i]);
            var (tagStart, tagEnd) = tags[i];
            if (!o.HasElements && o.Value != n.Value && !text[(tagEnd - 2)..tagEnd].Equals("/>", StringComparison.Ordinal))
            {
                int close = text.IndexOf("</" + o.Name.LocalName, tagEnd, StringComparison.Ordinal);
                if (close >= 0) text = text[..tagEnd] + System.Security.SecurityElement.Escape(n.Value) + text[close..];
            }
            string tag = text[tagStart..tagEnd];
            foreach (var name in o.Attributes().Select(a => a.Name).Union(n.Attributes().Select(a => a.Name)).Reverse())
                if (o.Attribute(name)?.Value != n.Attribute(name)?.Value) tag = SetAttribute(tag, name.LocalName, n.Attribute(name)?.Value);
            text = text[..tagStart] + tag + text[tagEnd..];
        }
        return text;
    }

    /// <summary>The start tags between two places in the text, in order, comments left out: where each starts and ends.</summary>
    private static List<(int Start, int End)> StartTags(string text, int from, int to)
    {
        var tags = new List<(int, int)>();
        for (int i = from; i < to; i++)
        {
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0) { i = text.IndexOf("-->", i + 4, StringComparison.Ordinal) + 2; continue; }
            if (text[i] != '<' || i + 1 >= to || text[i + 1] is '/' or '!' or '?') continue;
            int close = text.IndexOf('>', i);
            tags.Add((i, close + 1));
            i = close;
        }
        return tags;
    }

    /// <summary>A start tag with one attribute's value set (added before the tag's end when it has none), or taken away for null.</summary>
    private static string SetAttribute(string tag, string name, string? value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, $@"(\s+){System.Text.RegularExpressions.Regex.Escape(name)}(\s*=\s*)(""[^""]*""|'[^']*')");
        string escaped = value is null ? "" : System.Security.SecurityElement.Escape(value);
        if (m.Success)
            return value is null ? tag[..m.Index] + tag[(m.Index + m.Length)..]
                : tag[..m.Groups[3].Index] + "\"" + escaped + "\"" + tag[(m.Groups[3].Index + m.Groups[3].Length)..];
        if (value is null) return tag;
        int at = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
        while (at > 0 && char.IsWhiteSpace(tag[at - 1])) at--;
        return tag[..at] + $" {name}=\"{escaped}\"" + tag[at..];
    }

    /// <summary>Where an element that starts at <paramref name="start"/> ends: after its end tag, or its own /&gt;.</summary>
    private static int ElementEnd(string text, int start, string tag)
    {
        int depth = 0, i = start;
        while (i < text.Length)
        {
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0) { i = text.IndexOf("-->", i + 4, StringComparison.Ordinal) + 3; continue; }
            if (text[i] == '<')
            {
                int close = text.IndexOf('>', i);
                string inner = text[(i + 1)..close];
                string name = inner.TrimStart('/').Split([' ', '\t', '\r', '\n', '/'], 2)[0];
                if (name == tag)
                {
                    if (inner.StartsWith('/')) depth--;
                    else if (!inner.EndsWith('/')) depth++;
                    else if (depth == 0) return close + 1;
                    if (depth == 0) return close + 1;
                }
                i = close + 1;
                continue;
            }
            i++;
        }
        throw new InvalidDataException(L.T("ItemEdits.NotClosed", tag));
    }

    /// <summary>A normal game file's New Game+ copy: items\x.xml is items_plus\x.xml.</summary>
    private static string PlusPath(string path) =>
        path.Replace(@"\items\", @"\items_plus\", StringComparison.OrdinalIgnoreCase).Replace(@"\abilities\", @"\abilities_plus\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether two copies of a definitions file say the same, whatever their line endings.</summary>
    private static bool Same(string a, string b) => a.Replace("\r\n", "\n").TrimEnd() == b.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Writes a file into the workspace, or removes the project's copy when there's nothing left to change.</summary>
    private IEnumerable<string> WriteOrRemove(string gamePath, object? content)
    {
        string path = _project.GameFilePath(gamePath);
        if (content is null)
        {
            if (File.Exists(path))
            {
                FileSafety.CheckWrite(path);
                File.Delete(path);
            }
            yield break;
        }
        FileSafety.CheckWrite(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // UTF-8 with its mark, as the Remastered's text files are to be
        if (content is XDocument doc)
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(true), Indent = true, IndentChars = "\t" };
            using var writer = XmlWriter.Create(path, settings);
            doc.Save(writer);
        }
        else File.WriteAllText(path, (string)content, new UTF8Encoding(true));
        yield return path;
    }
}
