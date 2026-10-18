using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>One definition a mod's XML sets: with on_conflict (the Remastered's XML overrides) or as a plain definition.</summary>
/// <param name="Tag">The kind of definition: item, ability, set…</param>
/// <param name="Id">The definition's name.</param>
/// <param name="Mode">extend, replace, or define when there is no on_conflict.</param>
/// <param name="Fields">What the definition sets: its attributes, child tags and tags.</param>
public sealed record XmlOverride(ModEntry Mod, string Tag, string Id, string Mode, string File, string FullPath, int Line, IReadOnlyList<string> Fields, string? Bundle = null)
{
    /// <summary>Each field's value, normalised the way Script Merger compares them: "@attribute", child tag, or "tags:name" (add or remove).</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>When this is Script Merger's merged mod: the mods whose versions of the file were merged into it.</summary>
    public IReadOnlyList<string>? MergedFrom { get; init; }

    /// <summary>When a merged mod already contains this mod's version of the file: the merged mod.</summary>
    public string? CoveredBy { get; init; }

    /// <summary>Where it was found: the file, or the file inside the bundle.</summary>
    public string Where => Bundle is null ? File : $"{System.IO.Path.GetFileName(Bundle)}: {File}";

    /// <summary>
    /// The name the game orders this definition by. XML mounted by a DLC loads by the DLC's name (the dlc\NAME in the
    /// file's path), whatever mod folder it sits in; a later name overrides an earlier one.
    /// </summary>
    public string RuntimeName => !Mod.IsModio && File.Split('\\', '/') is ["dlc", { Length: > 0 } dlc, ..] ? dlc : Mod.Name;

    /// <summary>New Game+ definitions live in folders ending _plus (items_plus, abilities_plus) and are separate from the normal ones.</summary>
    public string Scope => File.Split('\\', '/').Any(p => p.EndsWith("_plus", StringComparison.OrdinalIgnoreCase)) ? "New Game+" : "Normal";

    public string Key => $"{Scope} · {Tag} {Id}";
    public bool Replaces => Mode.Equals("replace", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything that sets one definition, winner first, and what the game ends up with.</summary>
public sealed class OverrideGroup
{
    public required string Scope { get; init; }
    public required string Tag { get; init; }
    public required string Id { get; init; }

    /// <summary>The winner first.</summary>
    public required IReadOnlyList<XmlOverride> Overrides { get; init; }

    /// <summary>Fields two mods give different values, with the mod that wins each and the ones it beats.</summary>
    public required IReadOnlyList<(string Field, XmlOverride Winner, IReadOnlyList<XmlOverride> Losers)> FieldClashes { get; init; }

    public string Key => $"{Scope} · {Tag} {Id}";
    public bool IsShared => Overrides.Where(o => o.CoveredBy is null).Select(o => o.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
    public bool IsClash => FieldClashes.Count > 0;
}

/// <summary>
/// Finds the definitions mods set in XML, loose or inside bundles, and where two mods disagree. The rules follow
/// Script Merger Remastered: only definitions of the kinds the game lets mods override count, a field only clashes
/// when the values differ, and the winner is decided by runtime order.
/// </summary>
public static class XmlOverrideScanner
{
    private static readonly HashSet<string> Definitions = new(["item", "ability", "item_extension", "set", "usable_item", "reward", "res", "card", "attitude"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] IdAttributes = ["name", "id", "index"];

    public static List<OverrideGroup> Scan(IReadOnlyList<ModEntry> mods, IReadOnlyList<ScriptMerger.MergeRecord> merges, IProgress<string>? progress, CancellationToken token)
    {
        var found = new List<XmlOverride>();
        var lockObj = new object();
        int done = 0;
        Parallel.ForEach(mods.Where(m => m.Enabled), new ParallelOptions { CancellationToken = token }, mod =>
        {
            var local = new List<XmlOverride>();
            foreach (string file in Files(mod.Folder))
            {
                token.ThrowIfCancellationRequested();
                ReadFile(mod, file, local);
            }
            foreach (string bundle in Bundles(mod.Folder))
            {
                token.ThrowIfCancellationRequested();
                ReadBundle(mod, bundle, local);
            }
            lock (lockObj) found.AddRange(local);
            progress?.Report($"Read {mod.Name} ({Interlocked.Increment(ref done)} of {mods.Count})");
        });

        // Script Merger's merged mod holds the result of merging several mods' copies of a file: those mods' own copies are covered by it
        static string Norm(string path) => path.Replace('/', '\\').ToLowerInvariant();
        var merged = merges.GroupBy(m => Norm(m.RelativePath)).ToDictionary(g => g.Key, g => g.ToList());
        for (int i = 0; i < found.Count; i++)
        {
            var o = found[i];
            if (!merged.TryGetValue(Norm(o.File), out var records)) continue;
            if (records.FirstOrDefault(r => r.MergedMod.Equals(o.Mod.Name, StringComparison.OrdinalIgnoreCase)) is { } own)
                found[i] = o with { MergedFrom = own.Included };
            else if (records.FirstOrDefault(r => r.Included.Contains(o.Mod.Name, StringComparer.OrdinalIgnoreCase)) is { } covering)
                found[i] = o with { CoveredBy = covering.MergedMod };
        }

        var groups = new List<OverrideGroup>();
        var order = Comparer<XmlOverride>.Create(RuntimeOrder);
        foreach (var g in found.GroupBy(o => (o.Scope, o.Tag.ToLowerInvariant(), o.Id.ToLowerInvariant())))
        {
            var ordered = g.OrderBy(o => o, order).ThenBy(o => o.File, StringComparer.OrdinalIgnoreCase).ToList();
            groups.Add(new OverrideGroup
            {
                Scope = ordered[0].Scope, Tag = ordered[0].Tag, Id = ordered[0].Id, Overrides = ordered,
                FieldClashes = Clashes([.. ordered.Where(o => o.CoveredBy is null)]),
            });
        }
        return [.. groups.OrderByDescending(g => g.IsClash).ThenByDescending(g => g.IsShared)
            .ThenBy(g => g.Tag, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The winner sorts first. mod.io mods beat everything (a lower priority number first, then by name); otherwise
    /// the name that sorts LAST wins, because later mounts override earlier ones.
    /// </summary>
    private static int RuntimeOrder(XmlOverride x, XmlOverride y)
    {
        if (x.Mod.IsModio != y.Mod.IsModio) return x.Mod.IsModio ? -1 : 1;
        if (x.Mod.IsModio)
        {
            int byPriority = (x.Mod.Priority ?? 9999).CompareTo(y.Mod.Priority ?? 9999);
            return byPriority != 0 ? byPriority : StringComparer.OrdinalIgnoreCase.Compare(x.Mod.Name, y.Mod.Name);
        }
        int byRuntime = StringComparer.OrdinalIgnoreCase.Compare(y.RuntimeName, x.RuntimeName);
        return byRuntime != 0 ? byRuntime : StringComparer.OrdinalIgnoreCase.Compare(x.Mod.Name, y.Mod.Name);
    }

    /// <summary>
    /// A field clashes when two mods give it different values. A replace, or a card (which can only be replaced),
    /// clashes as a whole definition when more than one mod has it.
    /// </summary>
    private static List<(string, XmlOverride, IReadOnlyList<XmlOverride>)> Clashes(List<XmlOverride> ordered)
    {
        var result = new List<(string, XmlOverride, IReadOnlyList<XmlOverride>)>();
        var perMod = ordered.GroupBy(o => o.Mod.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (perMod.Count < 2) return result;
        if (ordered.Any(o => o.Replaces) || ordered[0].Tag.Equals("card", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(("[whole definition]", perMod[0], perMod.Skip(1).ToList()));
            return result;
        }
        foreach (string field in perMod.SelectMany(o => o.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var setters = perMod.Where(o => o.Values.ContainsKey(field)).ToList();
            if (setters.Count > 1 && setters.Select(o => o.Values[field]).Distinct(StringComparer.Ordinal).Count() > 1)
                result.Add((field, setters[0], setters.Skip(1).ToList()));
        }
        return result;
    }

    private static IEnumerable<string> Files(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories)
                .Where(f => new FileInfo(f).Length < 64 * 1024 * 1024).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Bundles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.bundle", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The game's own XML, and mods that ship definitions, come inside bundles: read the .xml entries of each.</summary>
    private static void ReadBundle(ModEntry mod, string path, List<XmlOverride> into)
    {
        try
        {
            var bundle = Bundle.Open(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var entry in bundle.Entries.Where(e => e.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument doc;
                try { doc = Parse(TextDecoding.Decode(entry.Read(fs))); }
                catch (XmlException) { continue; }
                Collect(mod, doc, entry.Path.Replace('/', '\\'), path, path, into);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private static XDocument Parse(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static void ReadFile(ModEntry mod, string path, List<XmlOverride> into)
    {
        XDocument doc;
        try { doc = Parse(TextDecoding.Decode(File.ReadAllBytes(path))); }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return; }
        Collect(mod, doc, Path.GetRelativePath(mod.Folder, path), path, null, into);
    }

    /// <summary>Definitions sit two levels under &lt;definitions&gt;: &lt;definitions&gt;&lt;items&gt;&lt;item&gt;.</summary>
    private static void Collect(ModEntry mod, XDocument doc, string relative, string fullPath, string? bundle, List<XmlOverride> into)
    {
        var root = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("definitions", StringComparison.OrdinalIgnoreCase));
        if (root is null) return;
        foreach (var e in root.Elements().SelectMany(container => container.Elements()))
        {
            if (!Definitions.Contains(e.Name.LocalName)) continue;
            var idAttr = IdAttributes.Select(n => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(a => a is not null);
            if (idAttr is null || idAttr.Value.Trim().Length == 0) continue;
            var conflict = e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("on_conflict", StringComparison.OrdinalIgnoreCase)
                || a.Name.LocalName.Equals("onConflict", StringComparison.OrdinalIgnoreCase));
            var values = Values(e, idAttr, conflict);
            int line = e is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;
            into.Add(new XmlOverride(mod, e.Name.LocalName, idAttr.Value.Trim(), conflict is null ? "define" : Normalise(conflict.Value).ToLowerInvariant(),
                relative, fullPath, line, [.. values.Keys], bundle) { Values = values });
        }
    }

    private static Dictionary<string, string> Values(XElement definition, XAttribute identity, XAttribute? conflict)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in definition.Attributes().Where(a => a != identity && a != conflict))
            values["@" + a.Name.LocalName.ToLowerInvariant()] = Normalise(a.Value);
        foreach (var child in definition.Elements().GroupBy(c => c.Name.LocalName, StringComparer.OrdinalIgnoreCase))
        {
            if (child.Key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string tag in child.SelectMany(t => t.HasElements ? t.DescendantsAndSelf().Select(x => x.Attribute("name")?.Value ?? "") : t.Value.Split(',')))
                {
                    string text = Normalise(tag.Trim('"', '\'', ' '));
                    bool remove = text.StartsWith('-');
                    string name = Normalise(remove ? text[1..] : text);
                    if (name.Length > 0) values["tags:" + name.ToLowerInvariant()] = remove ? "remove" : "add";
                }
                continue;
            }
            values[child.Key.ToLowerInvariant()] = string.Join("|", child.Select(Canonical));
        }
        return values;
    }

    private static string Canonical(XElement e)
    {
        var sb = new StringBuilder("<").Append(e.Name.LocalName.ToLowerInvariant());
        foreach (var a in e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.OrdinalIgnoreCase))
            sb.Append(' ').Append(a.Name.LocalName.ToLowerInvariant()).Append('=').Append(Normalise(a.Value));
        sb.Append('>');
        if (e.HasElements) foreach (var c in e.Elements()) sb.Append(Canonical(c));
        else sb.Append(Normalise(e.Value));
        return sb.Append("</").Append(e.Name.LocalName.ToLowerInvariant()).Append('>').ToString();
    }

    private static string Normalise(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
