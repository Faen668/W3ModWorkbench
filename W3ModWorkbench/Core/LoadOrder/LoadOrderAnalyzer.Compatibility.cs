using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Input;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>
/// The checks of what mods put outside their scripts: texture caches, DLC IDs, the options menus and keys they add
/// to the game's own folders, their XML definitions, and what each changes in a file two of them ship.
/// </summary>
public sealed partial class LoadOrderAnalyzer
{
    /// <summary>The most clashing files looked into property by property, and the largest; past these the clash is only listed.</summary>
    private const int MaxDiffs = 200, MaxDiffSize = 8 << 20;

    /// <summary>A new line in a finding's item, indented under the file it's about.</summary>
    private const string Under = "\n          ";

    private string? _gameFolder;
    private GameConfig? _config;
    private IReadOnlyList<ScriptMerger.MergeRecord> _merges = [];
    private CancellationToken _token;
    private ResourceLocator? _vanilla;
    private int _diffs;
    private readonly Dictionary<string, Bundle?> _bundles = new(StringComparer.OrdinalIgnoreCase);

    private ScannedMod? ModNamed(string name) => _all.FirstOrDefault(m => m.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Line(IEnumerable<(ScannedMod Mod, string Text)> versions) =>
        string.Join(" / ", versions.OrderBy(v => v.Mod.Entry.Order).Select((v, i) => $"{(i == 0 ? "★ " : "")}{v.Mod.Entry.Name}: {v.Text}"));

    // ---- texture caches

    private void TextureCaches()
    {
        var byPath = new Dictionary<string, List<(ScannedMod Mod, CachedTexture Texture)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _active)
            foreach (var t in mod.Textures.DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!byPath.TryGetValue(t.Path, out var list)) byPath[t.Path] = list = [];
                list.Add((mod, t));
            }
        var xbms = _active.ToDictionary(m => m, m => m.Files.Where(f => f.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var sets = new Dictionary<string, (List<ScannedMod> Mods, List<string> Different, List<string> Same)>();
        var mismatched = new List<(ScannedMod Xbm, ScannedMod Cache, string Line)>();
        foreach (var (path, list) in byPath)
        {
            var ordered = list.OrderBy(x => x.Mod.Entry.Order).ToList();
            // the .xbm the game uses says how big the texture is; the pixels come from the first cache that has them
            var xbmWinner = _active.Where(m => xbms[m].Contains(path)).MinBy(m => m.Entry.Order);
            var cacheWinner = ordered[0];
            if (xbmWinner is not null && xbmWinner != cacheWinner.Mod && ordered.FirstOrDefault(x => x.Mod == xbmWinner) is { Mod: not null } own
                && own.Texture.Description != cacheWinner.Texture.Description)
                mismatched.Add((xbmWinner, cacheWinner.Mod, L.T("LoadOrderAnalyzer.TextureMismatchLine", path, xbmWinner.Entry.Name, own.Texture.Description, cacheWinner.Mod.Entry.Name, cacheWinner.Texture.Description)));

            if (ordered.Count < 2) continue;
            var mods = ordered.Select(x => x.Mod).ToList();
            string key = string.Join("|", mods.Select(m => m.Entry.Name));
            if (!sets.TryGetValue(key, out var set)) sets[key] = set = (mods, [], []);
            bool same = ordered.All(x => x.Texture.PackedSize == ordered[0].Texture.PackedSize && x.Texture.Description == ordered[0].Texture.Description)
                && ordered.Skip(1).All(x => SamePixels(ordered[0].Texture, x.Texture));
            if (same) set.Same.Add(path);
            else set.Different.Add($"{path}   ({Line(ordered.Select(x => (x.Mod, x.Texture.Description)))})");
        }

        string category = L.T("LoadOrderAnalyzer.Category.Textures");
        foreach (var (mods, different, same) in sets.Values)
        {
            if (different.Count > 0)
                Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.TexturesDiffer", L.T("Count.Texture", different.Count), mods.Count),
                    L.T("LoadOrderAnalyzer.TexturesDifferSummary", Ranked(mods), mods[0].Entry.Name),
                    mods, different.Order(StringComparer.OrdinalIgnoreCase), L.T("LoadOrderAnalyzer.TexturesDifferAdvice"), mods[0]);
            if (same.Count > 0)
                Add(Severity.Info, category, L.T("LoadOrderAnalyzer.TexturesSame", L.T("Count.Texture", same.Count), mods.Count),
                    L.T("LoadOrderAnalyzer.TexturesSameSummary", Ranked(mods)), mods, same.Order(StringComparer.OrdinalIgnoreCase), L.T("LoadOrderAnalyzer.NoActionNeeded"));
        }
        foreach (var g in mismatched.GroupBy(x => (x.Xbm, x.Cache)))
            Add(Severity.Error, category, L.T("LoadOrderAnalyzer.TextureMismatch", L.T("Count.Texture", g.Count()), g.Key.Xbm.Entry.Name, g.Key.Cache.Entry.Name),
                L.T("LoadOrderAnalyzer.TextureMismatchSummary", g.Key.Xbm.Entry.Name, g.Key.Cache.Entry.Name),
                [g.Key.Xbm, g.Key.Cache], g.Select(x => x.Line), L.T("LoadOrderAnalyzer.TextureMismatchAdvice", g.Key.Xbm.Entry.Name, g.Key.Cache.Entry.Name), g.Key.Cache);
    }

    /// <summary>Whether two textures' packed pixels are the same, byte for byte.</summary>
    private static bool SamePixels(CachedTexture a, CachedTexture b)
    {
        try
        {
            return ReadPacked(a).AsSpan().SequenceEqual(ReadPacked(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static byte[] ReadPacked(CachedTexture t)
        {
            using var stream = File.OpenRead(t.Cache);
            stream.Position = t.Offset;
            var data = new byte[t.PackedSize];
            stream.ReadExactly(data);
            return data;
        }
    }

    // ---- DLC IDs

    private void DlcIds()
    {
        var byId = _active.SelectMany(m => m.Dlcs.Select(d => (Mod: m, d.Id, d.File)))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in byId)
        {
            var mods = g.Select(x => x.Mod).Distinct().OrderBy(m => m.Entry.Order).ToList();
            Add(Severity.Error, L.T("LoadOrderAnalyzer.Category.Dlc"), L.T("LoadOrderAnalyzer.DlcIdShared", g.Key, mods.Count),
                L.T("LoadOrderAnalyzer.DlcIdSharedSummary", Ranked(mods), g.Key),
                mods, g.OrderBy(x => x.Mod.Entry.Order).Select(x => $"{x.Mod.Entry.Name}: {x.File}"), L.T("LoadOrderAnalyzer.DlcIdSharedAdvice"));
        }
    }

    // ---- options menus

    private void Menus()
    {
        if (_config is not { MenuFolder: not null } config) return;
        string category = L.T("LoadOrderAnalyzer.Category.Menus");
        var groups = config.Groups.ToLookup(g => g.Id, StringComparer.OrdinalIgnoreCase);
        // the mods that read a group's settings are the ones its menu belongs to
        List<ScannedMod> Readers(string group) => [.. _active.Where(m => m.MenuReads.Any(r => r.Group.Equals(group, StringComparison.OrdinalIgnoreCase)))];
        // a menu file that comes with a mod being checked belongs to that mod
        List<ScannedMod> Bringing(IEnumerable<string> files) => files.Any(config.CandidateMenus.Contains) ? [.. _active.Where(m => m.Entry.IsCandidate)] : [];

        foreach (var (file, error) in config.BrokenMenus)
            Add(Severity.Error, category, L.T("LoadOrderAnalyzer.MenuBroken", file), L.T("LoadOrderAnalyzer.MenuBrokenSummary", file, Path.Combine(config.MenuFolder, file)),
                Bringing([file]), [error], L.T("LoadOrderAnalyzer.MenuBrokenAdvice", file));

        foreach (var g in groups.Where(g => g.Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var files = g.Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.MenuGroupTwice", g.Key, files.Count),
                L.T("LoadOrderAnalyzer.MenuGroupTwiceSummary", g.Key, string.Join(", ", files)),
                Readers(g.Key).Concat(Bringing(files)), g.Select(x => L.T("LoadOrderAnalyzer.MenuGroupLine", x.File, x.Line, L.T("Count.Setting", x.Vars.Count))), L.T("LoadOrderAnalyzer.MenuGroupTwiceAdvice"));
        }

        // a mod whose settings menu isn't there reads every setting as empty, and often does nothing at all
        foreach (var mod in _active)
        {
            var reads = mod.MenuReads.DistinctBy(r => (r.Group.ToLowerInvariant(), r.Var.ToLowerInvariant())).ToList();
            var noGroup = reads.Where(r => !groups.Contains(r.Group)).ToList();
            var noVar = reads.Where(r => groups.Contains(r.Group) && !groups[r.Group].Any(g => g.Vars.Contains(r.Var, StringComparer.OrdinalIgnoreCase))).ToList();
            // a mod that reads another mod's menu is told apart by the other mod providing it
            if (noGroup.Count > 0)
                Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.MenuMissing", mod.Entry.Name),
                    L.T("LoadOrderAnalyzer.MenuMissingSummary", mod.Entry.Name, string.Join(", ", noGroup.Select(r => r.Group).Distinct(StringComparer.OrdinalIgnoreCase))),
                    [mod], noGroup.Select(r => $"{r.Group} › {r.Var}   ({r.Source})"), L.T("LoadOrderAnalyzer.MenuMissingAdvice", mod.Entry.Name));
            if (noVar.Count > 0)
                Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.MenuOld", mod.Entry.Name, L.T("Count.Setting", noVar.Count)),
                    L.T("LoadOrderAnalyzer.MenuOldSummary", mod.Entry.Name),
                    [mod], noVar.Select(r => $"{r.Group} › {r.Var}   ({string.Join(", ", groups[r.Group].Select(g => g.File).Distinct())})"), L.T("LoadOrderAnalyzer.MenuOldAdvice", mod.Entry.Name));
        }
    }

    // ---- keys

    private void Keys()
    {
        if (_config is null) return;
        string category = L.T("LoadOrderAnalyzer.Category.Keys");
        var listening = _active.Where(m => m.Listens.Count > 0).ToList();
        if (_config.Input is not { } input)
        {
            if (listening.Count > 0) _report.Warnings.Add(L.T("LoadOrderAnalyzer.NoInputSettings"));
            return;
        }
        // a mod's own action: one it listens for that the game's scripts don't name (a name alone can be anything)
        bool Mods(string action) => !_game.Names.Contains(action) && _active.Any(m => m.Listens.Any(l => l.Action.Equals(action, StringComparison.OrdinalIgnoreCase)));
        List<ScannedMod> Using(string action) => [.. _active.Where(m => m.Listens.Any(l => l.Action.Equals(action, StringComparison.OrdinalIgnoreCase)))];

        foreach (var mod in listening)
        {
            var actions = mod.Listens.Select(l => l.Action).Where(a => !_game.Names.Contains(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unbound = actions.Where(a => input.ForAction(a).Count == 0).ToList();
            var noKey = actions.Except(unbound, StringComparer.OrdinalIgnoreCase).Where(a => input.ForAction(a).All(b => b.Key.Equals("IK_None", StringComparison.OrdinalIgnoreCase))).ToList();
            if (unbound.Count > 0)
                Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.KeysMissing", mod.Entry.Name, L.T("Count.Action", unbound.Count)),
                    L.T("LoadOrderAnalyzer.KeysMissingSummary", mod.Entry.Name, input.Path),
                    [mod], unbound.Select(a => _config.MenuActions.Contains(a) ? L.T("LoadOrderAnalyzer.KeyInMenu", a) : a), L.T("LoadOrderAnalyzer.KeysMissingAdvice", mod.Entry.Name));
            if (noKey.Count > 0)
                Add(Severity.Info, category, L.T("LoadOrderAnalyzer.KeysUnset", mod.Entry.Name, L.T("Count.Action", noKey.Count)),
                    L.T("LoadOrderAnalyzer.KeysUnsetSummary", mod.Entry.Name),
                    [mod], noKey.Select(a => _config.MenuActions.Contains(a) ? L.T("LoadOrderAnalyzer.KeyInMenu", a) : a), L.T("LoadOrderAnalyzer.KeysUnsetAdvice"));
        }

        // a key that does a mod's action and something else in the same place
        var sets = new Dictionary<string, (List<ScannedMod> Mods, List<string> Lines)>();
        foreach (var g in input.Bindings.Where(b => !b.Key.Equals("IK_None", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(b => (Context: b.Context.ToLowerInvariant(), Key: b.Key.ToLowerInvariant())))
        {
            var actions = g.Select(b => b.Action).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (actions.Count < 2 || !actions.Any(Mods)) continue;
            var mods = actions.Where(Mods).SelectMany(Using).Distinct().OrderBy(m => m.Entry.Order).ToList();
            string key = string.Join("|", mods.Select(m => m.Entry.Name));
            if (!sets.TryGetValue(key, out var set)) sets[key] = set = (mods, []);
            var first = g.First();
            var others = actions.Where(a => !Mods(a)).ToList();
            string theirs = string.Join(", ", actions.Where(Mods).Select(a => $"{a} ({string.Join(", ", Using(a).Select(m => m.Entry.Name))})"));
            string game = others.Count == 0 ? "" : ", " + L.T("LoadOrderAnalyzer.GameActions", string.Join(", ", others.Take(3)) + (others.Count > 3 ? ", …" : ""));
            set.Lines.Add(L.T("LoadOrderAnalyzer.KeyShareLine", first.Key, first.Context, theirs + game));
        }
        foreach (var (mods, lines) in sets.Values)
            Add(Severity.Warning, category, L.T("LoadOrderAnalyzer.KeysShared", L.T("Count.Key", lines.Count), string.Join(", ", mods.Select(m => m.Entry.Name))),
                L.T("LoadOrderAnalyzer.KeysSharedSummary"), mods, lines, L.T("LoadOrderAnalyzer.KeysSharedAdvice"));
    }

    // ---- XML definitions

    private void XmlDefinitions()
    {
        List<OverrideGroup> groups;
        try { groups = XmlOverrideScanner.Scan([.. _active.Select(m => m.Entry)], _merges, null, _token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report.Warnings.Add(L.T("LoadOrderAnalyzer.XmlCouldntRead", ex.Message));
            return;
        }
        // XML loads in its own order (a DLC's by the DLC's name), so the winner is said by the scan, not the load order
        var sets = new Dictionary<string, (List<ScannedMod> Mods, ScannedMod? Winner, List<string> Lines)>();
        foreach (var g in groups.Where(g => g.IsClash))
            foreach (var (field, winner, losers) in g.FieldClashes)
            {
                var mods = losers.Prepend(winner).Select(o => ModNamed(o.Mod.Name)).OfType<ScannedMod>().Distinct().OrderBy(m => m.Entry.Order).ToList();
                if (mods.Count < 2) continue;
                string key = winner.Mod.Name + ">" + string.Join("|", mods.Select(m => m.Entry.Name));
                if (!sets.TryGetValue(key, out var set)) sets[key] = set = (mods, ModNamed(winner.Mod.Name), []);
                string Value(XmlOverride o) => o.Values.TryGetValue(field, out var v) ? v : "–";
                set.Lines.Add($"{g.Key} › {field}   (★ {winner.Mod.Name}: {Value(winner)} / {string.Join(" / ", losers.Select(l => $"{l.Mod.Name}: {Value(l)}"))})");
            }
        foreach (var (mods, winner, lines) in sets.Values)
            Add(Severity.Warning, L.T("LoadOrderAnalyzer.Category.Xml"), L.T("LoadOrderAnalyzer.XmlClash", L.T("Count.Setting", lines.Count), mods.Count),
                L.T("LoadOrderAnalyzer.XmlClashSummary", Ranked(mods), winner?.Entry.Name ?? mods[0].Entry.Name), mods, lines, L.T("LoadOrderAnalyzer.XmlClashAdvice"), winner);
    }

    // ---- what each mod changes in a file two of them ship

    /// <summary>
    /// Lines that say what each mod's copy of a file changes against the game's (or, for a file the game hasn't got,
    /// how the copies differ), and whether they change the same things. Empty when the file isn't one to look into.
    /// <paramref name="combinable"/> says whether the mods change different things, so one file could hold both.
    /// </summary>
    private List<string> FileChanges(string path, List<(ScannedMod Mod, BundledFile File)> copies, out bool? combinable)
    {
        combinable = null;
        if (_diffs >= MaxDiffs || copies.Any(c => c.File.Size > MaxDiffSize || c.File.Bundle.Length == 0)) return [];
        var data = copies.Select(c => (c.Mod, Data: ReadBundled(c.File))).ToList();
        if (data.Any(d => d.Data is not { Length: > 4 } bytes || bytes[0] != 'C' || bytes[1] != 'R' || bytes[2] != '2' || bytes[3] != 'W')) return [];
        _diffs++;
        _vanilla ??= new ResourceLocator([], null, _gameFolder, otherMods: false);
        byte[]? game = _gameFolder is null ? null : _vanilla.ReadFile(path);
        var lines = new List<string>();
        if (game is null)
        {
            // both add the file: how far apart their copies are
            if (Cr2wDiff.Changes(data[0].Data!, data[1].Data!) is { } apart)
                lines.Add("    " + L.T("LoadOrderAnalyzer.FileNewDiffers", data[0].Mod.Entry.Name, data[1].Mod.Entry.Name, L.T("Count.Property", apart.Count)));
            combinable = false;
            return lines;
        }

        var changes = new List<(ScannedMod Mod, List<Cr2wChange> Changes)>();
        foreach (var (mod, bytes) in data)
        {
            if (Cr2wDiff.Changes(game, bytes!) is not { } c) return [];
            changes.Add((mod, c));
            // each object changed on a line of its own, under the mod
            string shown = Under + Cr2wDiff.Summary(Cr2wDiff.Touched(c), 3, Under);
            lines.Add("    " + (c.Count == 0 ? L.T("LoadOrderAnalyzer.FileSameAsGame", mod.Entry.Name) : L.T("LoadOrderAnalyzer.FileChanges", mod.Entry.Name, L.T("Count.Change", Cr2wDiff.Touched(c).Count)) + shown));
        }
        // the same thing changed the same way by both isn't a clash
        var both = new List<string>();
        for (int i = 0; i < changes.Count; i++)
            for (int j = i + 1; j < changes.Count; j++)
                both.AddRange(Overlap(changes[i].Changes, changes[j].Changes));
        both = [.. both.Distinct(StringComparer.Ordinal)];
        combinable = both.Count == 0;
        lines.Add("    → " + (both.Count == 0 ? L.T("LoadOrderAnalyzer.FileCombinable")
            : L.T("LoadOrderAnalyzer.FileBothChange") + Under + Cr2wDiff.Summary(both, 3, Under)));
        return lines;
    }

    /// <summary>What two copies both change, to different values: a path one changes inside a list or structure the other changes counts.</summary>
    private static IEnumerable<string> Overlap(List<Cr2wChange> a, List<Cr2wChange> b)
    {
        var touchedA = Cr2wDiff.Touched(a);
        var touchedB = Cr2wDiff.Touched(b);
        var toA = a.ToDictionary(c => c.Path, c => c.To, StringComparer.Ordinal);
        var toB = b.ToDictionary(c => c.Path, c => c.To, StringComparer.Ordinal);
        static bool Within(string inner, string outer) => inner == outer || inner.StartsWith(outer + "[", StringComparison.Ordinal) || inner.StartsWith(outer + ".", StringComparison.Ordinal);
        foreach (string x in touchedA)
            foreach (string y in touchedB)
            {
                if (!Within(x, y) && !Within(y, x)) continue;
                if (x == y && toA.TryGetValue(x, out var va) && toB.TryGetValue(y, out var vb) && va == vb) continue;
                yield return x.Length <= y.Length ? x : y;
            }
    }

    private byte[]? ReadBundled(BundledFile file)
    {
        try
        {
            if (!_bundles.TryGetValue(file.Bundle, out var bundle)) _bundles[file.Bundle] = bundle = Bundle.Open(file.Bundle);
            var entry = bundle?.Entries.FirstOrDefault(e => e.Path.Replace('/', '\\').Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var stream = File.OpenRead(file.Bundle);
            return entry.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
