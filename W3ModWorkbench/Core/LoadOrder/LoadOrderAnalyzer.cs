using System.IO;
using System.Text;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>
/// Reads every installed mod (compiled scripts decompiled to their declarations, loose scripts parsed, bundle
/// tables, strings), puts them in the order the game loads them and looks for what breaks or collides.
/// </summary>
public sealed class LoadOrderAnalyzer
{
    private const int MaxItems = 400;

    private readonly LoadOrderReport _report;
    private readonly List<ScannedMod> _all;
    private readonly List<ScannedMod> _active;
    private readonly GameIndex _game;

    private readonly HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _conflictedFiles = new(StringComparer.OrdinalIgnoreCase);

    private LoadOrderAnalyzer(LoadOrderReport report, List<ScannedMod> all, GameIndex game)
    {
        _report = report;
        _all = all;
        _active = [.. all.Where(m => m.Entry.Enabled)];
        _game = game;
        foreach (string m in game.Members) _members.Add(m);
        foreach (var (k, v) in game.Bases) _bases[k] = v;
        foreach (var mod in _active)
        {
            foreach (string m in mod.Scripts.Members) _members.Add(m);
            foreach (var a in mod.Scripts.Annotations.Where(a => a.Kind is "addMethod" or "addField")) _members.Add(a.Key);
            foreach (var (k, v) in mod.Scripts.Bases) _bases[k] = v;
        }
    }

    /// <summary>Every mod.io mod, DLC and mod, in the order the game loads them (first = highest priority).</summary>
    public static List<ModEntry> DiscoverMods(LoadOrderOptions options, Dictionary<string, (bool Enabled, int? Priority)>? settings = null)
    {
        settings ??= ModScanner.ReadSettings(options.SettingsFile);
        // every folder under mods\ (and dlc\), each one a mod
        var entries = new List<ModEntry>();
        void Discover(string root, bool dlc)
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                var entry = new ModEntry { Name = name, Folder = dir, IsDlc = dlc };
                if (settings.TryGetValue(name, out var s))
                {
                    entry.Enabled = s.Enabled;
                    entry.Priority = s.Priority;
                }
                entries.Add(entry);
            }
        }
        if (options.IncludeDlc && Path.GetDirectoryName(options.ModsFolder.TrimEnd('\\', '/')) is { } parent)
            Discover(Path.Combine(parent, "dlc"), true);
        Discover(options.ModsFolder, false);
        foreach (var m in ModioLocator.InstalledMods())
            entries.Add(new ModEntry { Name = m.Name, Folder = m.Folder, IsDlc = false, IsModio = true, Enabled = m.Enabled ?? true, Priority = m.Priority });

        // mod.io mods first, then DLC, then mods; within each the lowest priority number first, then the mods mods.settings doesn't list, by name
        var ordered = entries
            .OrderBy(e => e.IsModio ? 0 : e.IsDlc ? 1 : 2)
            .ThenBy(e => e.Priority is null ? 1 : 0)
            .ThenBy(e => e.Priority ?? 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i + 1;
        return ordered;
    }

    public static async Task<LoadOrderReport> RunAsync(LoadOrderOptions options, IProgress<string>? progress, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        string? gameScripts = null;
        string? gameFolder = options.GameFolder ?? Path.GetDirectoryName(options.ModsFolder.TrimEnd('\\', '/'));
        if (gameFolder is not null && Directory.Exists(Path.Combine(gameFolder, "content", "content0", "scripts")))
            gameScripts = Path.Combine(gameFolder, "content", "content0", "scripts");

        var report = new LoadOrderReport { ModsFolder = options.ModsFolder, SettingsFile = options.SettingsFile, GameScriptsFolder = gameScripts };
        var settings = ModScanner.ReadSettings(options.SettingsFile);

        var ordered = DiscoverMods(options, settings);

        progress?.Report($"Reading {Format.Count(ordered.Count, "mod")}…");
        var scanned = new ScannedMod[ordered.Count];
        int done = 0;
        var gameTask = gameScripts is null ? Task.FromResult(GameIndex.Empty) : GameIndex.LoadAsync(gameScripts);
        await Task.Run(() => Parallel.For(0, ordered.Count,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) },
            i =>
            {
                scanned[i] = ModScanner.Scan(ordered[i], token);
                progress?.Report($"Read {ordered[i].Name} ({Interlocked.Increment(ref done)} of {ordered.Count})");
            }), token);

        progress?.Report("Reading the game's scripts…");
        var game = await gameTask;
        report.VanillaTypes = game.Types.Count;
        token.ThrowIfCancellationRequested();

        progress?.Report("Checking for conflicts…");
        foreach (var m in scanned) report.Mods.Add(m.Entry);
        var analyzer = new LoadOrderAnalyzer(report, [.. scanned], game);
        await Task.Run(() => analyzer.Analyze(settings, gameScripts is not null), token);

        report.Elapsed = DateTime.UtcNow - started;
        return report;
    }

    // ---------------------------------------------------------------- helpers

    private void Add(Severity severity, string category, string title, string summary, IEnumerable<ScannedMod> mods, IEnumerable<string> items, string advice, ScannedMod? winner = null)
    {
        var ordered = mods.DistinctBy(m => m.Entry.Name).OrderBy(m => m.Entry.Order).Select(m => m.Entry.Name).ToList();
        var list = items.ToList();
        if (list.Count > MaxItems)
        {
            int more = list.Count - MaxItems;
            list = [.. list.Take(MaxItems), $"… and {more:N0} more"];
        }
        _report.Findings.Add(new Finding(severity, category, title, summary, ordered, list, advice, winner?.Entry.Name));
    }

    private static string Who(ScannedMod m) => m.Entry.Name;

    /// <summary>mod0000_MergedFiles and friends: the output of a merge tool, which is meant to win.</summary>
    private static bool IsMerged(ScannedMod m) => m.Entry.Name.Contains("merged", StringComparison.OrdinalIgnoreCase);

    /// <summary>"modA (1), modB (4)": the mods with their place in the load order.</summary>
    private static string Ranked(IEnumerable<ScannedMod> mods) =>
        string.Join(", ", mods.OrderBy(m => m.Entry.Order).Select(m => $"{m.Entry.Name} (#{m.Entry.Order})"));

    private bool TypeExists(string name) =>
        _game.Types.Contains(name) || _active.Any(m => m.Scripts.Types.ContainsKey(name));

    private bool MemberExists(string owner, string name)
    {
        string? cls = owner;
        for (int depth = 0; cls is { Length: > 0 } && depth < 40; depth++)
        {
            if (_members.Contains($"{cls}.{name}")) return true;
            cls = _bases.TryGetValue(cls, out var b) ? b : null;
        }
        return false;
    }

    // ---------------------------------------------------------------- analysis

    private void Analyze(Dictionary<string, (bool Enabled, int? Priority)> settings, bool haveGame)
    {
        ReadProblems();
        LooseScriptFiles();
        TypeDefinitions();
        GlobalFunctions();
        AnnotationConflicts();
        if (haveGame && !_game.IsEmpty) TargetsExist();
        else _report.Warnings.Add("The game's scripts weren't found, so replaced and wrapped functions couldn't be checked against the game.");
        LooseReplacements();
        Dependencies();
        BundleFiles();
        StringIds();
        Platforms();
        SettingsNotes(settings);

        // tally per mod, then put the worst first
        var byName = _report.Mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var f in _report.Findings)
            foreach (string name in f.Mods)
                if (byName.TryGetValue(name, out var m))
                {
                    if (f.Severity == Severity.Error) m.Problems++;
                    else if (f.Severity == Severity.Warning) m.Warnings++;
                    else m.Notes++;
                }
        var sorted = _report.Findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _report.Findings.Clear();
        _report.Findings.AddRange(sorted);
    }

    private void ReadProblems()
    {
        foreach (var mod in _all)
            foreach (var (message, fatal) in mod.Problems)
                Add(fatal ? Severity.Error : Severity.Warning, "Unreadable", $"{mod.Entry.Name}: {message}", message, [mod], [],
                    fatal ? "A file the game would load couldn't be read. Reinstall the mod; if it's your own project, recompile it." : "Check the file for damage.");
    }

    // ---- loose scripts: the same path in two mods, and replacements of the game's files

    private void LooseScriptFiles()
    {
        var groups = _active
            .SelectMany(m => m.Loose.Select(l => (Mod: m, File: l)))
            .GroupBy(x => x.File.Relative, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in groups)
        {
            var mods = g.Select(x => x.Mod).Distinct().OrderBy(m => m.Entry.Order).ToList();
            bool identical = g.Select(x => x.File.Hash).Distinct().Count() == 1;
            bool replacesGame = _game.Files.ContainsKey(g.Key);
            _conflictedFiles.Add(g.Key);
            if (identical)
            {
                Add(Severity.Info, "Script file", $"Identical copies of scripts\\{g.Key}", $"{Ranked(mods)} ship the same file.", mods, [],
                    "Harmless while they stay identical. If one of them is updated they will start to conflict.");
            }
            else if (IsMerged(mods[0]))
            {
                Add(Severity.Info, "Script file", $"scripts\\{g.Key} is covered by {mods[0].Entry.Name}",
                    $"{Ranked(mods)} each ship this file. {mods[0].Entry.Name} loads first and is a merge, so it should already contain the others' changes.",
                    mods, [], "Re-run the merger whenever one of these mods is updated.", mods[0]);
            }
            else
            {
                string first = mods[0].Entry.Name;
                Add(Severity.Error, "Script file", $"Two mods replace scripts\\{g.Key}",
                    $"{Ranked(mods)} each ship a different version of {(replacesGame ? "a game script" : "this file")}. Only one is used: {first}, the highest priority. "
                    + "The others' changes in that file are lost.",
                    mods, g.OrderBy(x => x.Mod.Entry.Order).Select((x, i) => $"{(i == 0 ? "\u2605 " : "   ")}{x.Mod.Entry.Name}: {x.File.FullPath}"),
                    replacesGame
                        ? "Merge the files with Script Merger Remastered (Open Script Merger, below), or ask the authors for a version that uses @wrapMethod / @replaceMethod annotations, which can coexist."
                        : "Remove one of the mods, or rename one of the files if they are unrelated.", mods[0]);
            }
        }

        foreach (var mod in _active)
        {
            var over = mod.Loose.Where(l => _game.Files.ContainsKey(l.Relative)).Select(l => l.Relative).ToList();
            if (over.Count == 0) continue;
            Add(Severity.Info, "Script file", $"{mod.Entry.Name} replaces {Format.Count(over.Count, "game script")} whole",
                "A whole-file replacement hides every other mod's change to those files, and it is out of date as soon as the game's file changes.",
                [mod], over.Select(o => "scripts\\" + o),
                "Prefer annotations (@wrapMethod and friends) where the mod can be rewritten to use them.");
        }
    }

    // ---- types defined twice

    private void TypeDefinitions()
    {
        var defs = _active
            .SelectMany(m => m.Scripts.Types.Select(t => (Mod: m, Name: t.Key, Kind: t.Value)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var g in defs)
        {
            var mods = g.Select(x => x.Mod).Distinct().ToList();
            bool isEnum = g.Any(x => x.Kind == "enum");

            if (!isEnum && mods.Count > 1)
            {
                // two copies of one file are already reported as a file conflict
                var files = g.Select(x => x.Mod.Scripts.TypeFiles.GetValueOrDefault(x.Name)).ToList();
                if (!(files.All(f => f is not null) && files.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1))
                    Add(Severity.Error, "Type defined twice", $"{g.Key} is defined by {mods.Count} mods",
                        $"{Ranked(mods)} each declare the {g.First().Kind} {g.Key}. Scripts that use it get whichever the game loads, and the other mod's code expects a different shape.",
                        mods, [], "Only one of these mods can be installed, unless one is changed to use a different name.");
            }

            // a compiled project that declares a game class replaces it whole, as a loose file does
            if (!isEnum && _game.Types.Contains(g.Key))
                foreach (var x in g.Where(x => !x.Mod.Scripts.TypeFiles.ContainsKey(x.Name)))
                    Add(Severity.Info, "Script file", $"{x.Mod.Entry.Name} replaces the game's {g.Key} whole",
                        $"The mod's compiled scripts declare {x.Kind} {g.Key}, which the game already has, so its version takes the game's place and hides other mods' additions to it.",
                        [x.Mod], [], "Prefer annotations (@wrapMethod and friends) where the mod can be rewritten to use them.");

            if (isEnum && mods.Count > 1) EnumCollisions(g.Key, g.Where(x => x.Kind == "enum").Select(x => x.Mod).Distinct().ToList());
        }
    }

    private void EnumCollisions(string enumName, List<ScannedMod> mods)
    {
        if (mods.Count < 2) return;
        var byName = mods.SelectMany(m => m.Scripts.EnumMembers.GetValueOrDefault(enumName)?.Select(kv => (Mod: m, Name: kv.Key, Value: kv.Value)) ?? [])
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in byName)
        {
            var who = g.Select(x => x.Mod).Distinct().ToList();
            Add(Severity.Warning, "Enum value", $"{enumName}.{g.Key} is added by {who.Count} mods",
                $"{Ranked(who)} both add this value to {enumName}.", who,
                g.Select(x => $"{x.Mod.Entry.Name}: {(x.Value is { } v ? v.ToString() : "value not recorded")}"),
                "The values may differ, which breaks code compiled against the other mod's number.");
        }
        var byValue = mods.SelectMany(m => m.Scripts.EnumMembers.GetValueOrDefault(enumName)?.Where(kv => kv.Value is not null).Select(kv => (Mod: m, Name: kv.Key, Value: kv.Value!.Value)) ?? [])
            .GroupBy(x => x.Value).Where(g => g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                                              && g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in byValue)
        {
            var who = g.Select(x => x.Mod).Distinct().ToList();
            Add(Severity.Warning, "Enum value", $"{enumName} value {g.Key} means two things",
                $"{Ranked(who)} give different names the number {g.Key} in {enumName}.", who,
                g.Select(x => $"{x.Mod.Entry.Name}: {x.Name} = {x.Value}"),
                "Saved games and code comparing the number will read the wrong value.");
        }
    }

    private void GlobalFunctions()
    {
        var groups = _active.SelectMany(m => m.Scripts.Globals.Select(g => (Mod: m, Name: g)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in groups)
        {
            var mods = g.Select(x => x.Mod).Distinct().ToList();
            var files = g.Select(x => x.Mod.Scripts.GlobalFiles.GetValueOrDefault(x.Name)).ToList();
            if (files.All(f => f is not null) && files.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) continue;
            Add(Severity.Error, "Function defined twice", $"Global function {g.Key}() is defined by {mods.Count} mods",
                $"{Ranked(mods)} declare it. Only one definition can exist.", mods, [], "Rename one of them or remove one of the mods.");
        }
    }

    // ---- the same function changed by several mods

    private void AnnotationConflicts()
    {
        var groups = _active.SelectMany(m => m.Scripts.Annotations.Select(a => (Mod: m, Note: a)))
            .GroupBy(x => (Field: x.Note.IsField, Key: x.Note.Key.ToLowerInvariant()));
        foreach (var g in groups)
        {
            var mods = g.Select(x => x.Mod).Distinct().ToList();
            if (mods.Count < 2) continue;
            string target = g.First().Note.Key;
            string kind = g.Key.Field ? "field" : "function";
            var replacers = g.Where(x => x.Note.Kind == "replace").Select(x => x.Mod).Distinct().ToList();
            var wrappers = g.Where(x => x.Note.Kind == "wrap").Select(x => x.Mod).Distinct().ToList();
            var adders = g.Where(x => x.Note.Kind is "addMethod" or "addField").Select(x => x.Mod).Distinct().ToList();
            var replaceWinner = g.Where(x => x.Note.Kind == "replace").Select(x => x.Mod).OrderBy(m => m.Entry.Order).FirstOrDefault();
            var lines = g.GroupBy(x => (x.Mod, x.Note.Kind)).OrderBy(x => x.Key.Mod.Entry.Order)
                .Select(x => $"{(x.Key.Kind == "replace" && x.Key.Mod == replaceWinner ? "\u2605 " : "")}{x.Key.Mod.Entry.Name}: @{x.Key.Kind}  ({string.Join(", ", x.Select(n => n.Note.Source).Distinct())})").ToList();

            if (adders.Count > 1)
                Add(Severity.Error, "Same function", $"{target} is added by {adders.Count} mods",
                    $"{Ranked(adders)} each add the {kind} {target}. A class can't have it twice, so one of them fails.", adders, lines,
                    "Rename the member in one of the mods, or remove one.");
            if (replacers.Count > 1)
                Add(Severity.Error, "Same function", $"{target} is replaced by {replacers.Count} mods",
                    $"{Ranked(replacers)} each use @replaceMethod. Only one replacement runs; the other mod's version of {target} is thrown away.", replacers, lines,
                    "Keep one. If both are wanted, Script Merger Remastered may be able to merge them, or one mod has to be rewritten with @wrapMethod.",
                    replacers.OrderBy(m => m.Entry.Order).First());
            else if (replacers.Count == 1 && wrappers.Any(w => w != replacers[0]))
            {
                var wrappingOthers = wrappers.Where(w => w != replacers[0]).ToList();
                var later = wrappingOthers.Where(w => w.Entry.Order > replacers[0].Entry.Order).ToList();
                Add(Severity.Warning, "Same function", $"{target} is replaced by {replacers[0].Entry.Name} and wrapped by {Format.Count(wrappingOthers.Count, "other mod")}",
                    $"@replaceMethod swaps out the whole body; whether the wraps around it still run depends on the order the mods are applied. "
                    + $"{replacers[0].Entry.Name} is #{replacers[0].Entry.Order}{(later.Count > 0 ? $", {Ranked(later)} come after it" : "")}.",
                    g.Select(x => x.Mod).Distinct(), lines,
                    "Check in game that both mods still work. A replace that loads after a wrap usually discards it; Script Merger Remastered can combine such changes.");
            }
            else if (wrappers.Count > 1 && replacers.Count == 0)
                Add(Severity.Info, "Same function", $"{target} is wrapped by {wrappers.Count} mods",
                    $"{Ranked(wrappers)} each wrap it. Wraps stack, each calling the one below, so this normally works. "
                    + "It breaks only if one of them forgets to call wrappedMethod() or changes the result the other expects.",
                    wrappers, lines, "No action unless one of the mods misbehaves; then try changing their order.");
        }
    }

    // ---- annotations that point at nothing

    private void TargetsExist()
    {
        foreach (var mod in _active)
        {
            var missingTypes = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var missingMembers = new List<string>();
            var missingGlobals = new List<string>();
            foreach (var a in mod.Scripts.Annotations)
            {
                if (a.Target.Length == 0)
                {
                    if (a.Kind is "replace" or "wrap" && !_game.Globals.Contains(a.Name) && !_active.Any(m => m != mod && m.Scripts.Globals.Contains(a.Name)))
                        missingGlobals.Add($"@{a.Kind} {a.Name}()  ({a.Source})");
                    continue;
                }
                if (!TypeExists(a.Target))
                {
                    if (!missingTypes.TryGetValue(a.Target, out var list)) missingTypes[a.Target] = list = [];
                    list.Add($"@{a.Kind} {a.Key}");
                    continue;
                }
                if (a.Kind is "replace" or "wrap" && !MemberExists(a.Target, a.Name))
                    missingMembers.Add($"@{a.Kind} {a.Key}  ({a.Source})");
            }

            if (missingTypes.Count > 0)
            {
                var disabledOwner = _all.Where(m => !m.Entry.Enabled && missingTypes.Keys.Any(t => m.Scripts.Types.ContainsKey(t))).ToList();
                Add(Severity.Error, "Missing target", $"{mod.Entry.Name} changes {Format.Count(missingTypes.Count, "class", "classes")} that {(missingTypes.Count == 1 ? "doesn't" : "don't")} exist",
                    "Its annotations name classes that neither the game nor any enabled mod declares, so they have nothing to attach to."
                    + (disabledOwner.Count > 0 ? $" {string.Join(", ", disabledOwner.Select(Who))} declare some, but {(disabledOwner.Count == 1 ? "is" : "are")} disabled." : ""),
                    [mod, .. disabledOwner], missingTypes.SelectMany(kv => kv.Value.Select(v => $"{v}   (class {kv.Key} not found)")),
                    "The mod was probably written for another game version, or needs a mod that isn't installed or enabled.");
            }
            if (missingMembers.Count > 0)
                Add(Severity.Error, "Missing target", $"{mod.Entry.Name} wraps or replaces {Format.Count(missingMembers.Count, "function")} the game doesn't have",
                    "The classes exist, but these functions are not declared in them (or their base classes) by the game or any enabled mod. "
                    + "The mod was most likely written for a different version of the game, and these will fail to compile or never run.",
                    [mod], missingMembers, "Get a version of the mod made for this game version, or remove it.");
            if (missingGlobals.Count > 0)
                Add(Severity.Error, "Missing target", $"{mod.Entry.Name} changes {Format.Count(missingGlobals.Count, "global function")} the game doesn't have",
                    "No global function of these names exists in the game's scripts or an enabled mod.", [mod], missingGlobals,
                    "Get a version of the mod made for this game version, or remove it.");
        }
    }

    /// <summary>A mod that replaces a whole game file, while another mod annotates a function that file's copy lacks.</summary>
    private void LooseReplacements()
    {
        foreach (var owner in _active.Where(m => m.Scripts.Types.Keys.Any(_game.Types.Contains)))
            foreach (var other in _active.Where(m => m != owner))
            {
                var lost = other.Scripts.Annotations
                    .Where(a => a.Kind is "wrap" or "replace" && a.Target.Length > 0
                                && owner.Scripts.Types.ContainsKey(a.Target) && _game.Types.Contains(a.Target)
                                && !owner.Scripts.Members.Contains(a.Key) && !MemberExistsIn(owner, a.Target, a.Name))
                    .Select(a => $"@{a.Kind} {a.Key}  ({other.Entry.Name})")
                    .ToList();
                if (lost.Count == 0) continue;
                Add(Severity.Warning, "Missing target", $"{owner.Entry.Name}'s copy of a game class lacks what {other.Entry.Name} changes",
                    $"{owner.Entry.Name} replaces the game's class whole, and its version has no such function. {other.Entry.Name}'s annotations have nothing to attach to.",
                    [owner, other], lost, "Update the replacement from the game's current script, or use annotations instead of replacing the file.");
            }
    }

    private bool MemberExistsIn(ScannedMod owner, string cls, string name)
    {
        for (int depth = 0; cls.Length > 0 && depth < 40; depth++)
        {
            if (owner.Scripts.Members.Contains($"{cls}.{name}")) return true;
            if (!owner.Scripts.Bases.TryGetValue(cls, out var b) && !_game.Bases.TryGetValue(cls, out b)) return false;
            cls = b;
        }
        return false;
    }

    // ---- dependencies

    private void Dependencies()
    {
        var typeProviders = Providers(m => m.Scripts.Types.Keys);
        var globalProviders = Providers(m => m.Scripts.Globals);
        var memberProviders = Providers(m => m.Scripts.Members.Concat(m.Scripts.Annotations.Where(a => a.Kind is "addMethod" or "addField").Select(a => a.Key)));

        foreach (var user in _active)
        {
            var uses = new Dictionary<ScannedMod, SortedSet<string>>();
            void Use(ScannedMod provider, string what)
            {
                if (provider == user) return;
                if (!uses.TryGetValue(provider, out var set)) uses[provider] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(what);
            }

            foreach (string t in user.Scripts.NeedTypes.Concat(user.Scripts.NeedWords.Where(w => char.IsUpper(w[0]))))
            {
                if (user.Scripts.Types.ContainsKey(t) || _game.Types.Contains(t)) continue;
                if (typeProviders.TryGetValue(t, out var ps))
                    foreach (var p in ps) Use(p, $"{p.Scripts.Types.GetValueOrDefault(t)} {t}");
            }
            foreach (string g in user.Scripts.NeedGlobals.Concat(user.Scripts.NeedWords))
            {
                if (user.Scripts.Globals.Contains(g) || _game.Globals.Contains(g) || user.Scripts.Types.ContainsKey(g) || _game.Types.Contains(g)) continue;
                if (globalProviders.TryGetValue(g, out var ps))
                    foreach (var p in ps) Use(p, $"function {g}()");
            }
            foreach (string key in user.Scripts.NeedMembers)
            {
                int dot = key.IndexOf('.');
                string cls = key[..dot], name = key[(dot + 1)..];
                if (user.Scripts.Provides(key) || _game.Members.Contains(key)) continue;
                string? at = cls;
                for (int depth = 0; at is { Length: > 0 } && depth < 40; depth++)
                {
                    string k = $"{at}.{name}";
                    if (_game.Members.Contains(k)) break;
                    if (memberProviders.TryGetValue(k, out var ps))
                    {
                        foreach (var p in ps) Use(p, $"member {k}");
                        break;
                    }
                    at = _bases.TryGetValue(at, out var b) ? b : null;
                }
            }

            foreach (var (provider, what) in uses)
            {
                user.Entry.DependsOn.Add(provider.Entry.Name);
                provider.Entry.UsedBy.Add(user.Entry.Name);
                var examples = what.Take(MaxItems).ToList();
                if (!provider.Entry.Enabled)
                {
                    Add(Severity.Error, "Dependency", $"{user.Entry.Name} needs {provider.Entry.Name}, which is disabled",
                        $"{user.Entry.Name} uses {Format.Count(what.Count, "thing")} that only {provider.Entry.Name} provides, and mods.settings has {provider.Entry.Name} switched off.",
                        [user, provider], examples, $"Enable {provider.Entry.Name}, or disable {user.Entry.Name} too.");
                }
                else if (provider.Entry.Order < user.Entry.Order && !provider.Entry.HasFixedPlace)
                {
                    // scripts compile in reverse load order, so what a mod builds on has to load after it
                    Add(Severity.Error, "Load order", $"{provider.Entry.Name} loads before {user.Entry.Name}, which builds on it",
                        $"{user.Entry.Name} (#{user.Entry.Order}) uses {Format.Count(what.Count, "thing")} from {provider.Entry.Name} (#{provider.Entry.Order}). "
                        + "The game compiles scripts in reverse load order, so the mod that provides them has to load last. As it is this tends to end in "
                        + "\"Unknown base class\" or \"different number of parameters\" errors when the game starts.",
                        [user, provider], examples, FixOrder(provider, user));
                }
                else if (provider.Entry.Priority is null != user.Entry.Priority is null && !provider.Entry.HasFixedPlace)
                {
                    var set = provider.Entry.Priority is null ? user : provider;
                    Add(Severity.Warning, "Load order", $"Only one of {user.Entry.Name} and {provider.Entry.Name} has a priority",
                        $"{set.Entry.Name} has priority {set.Entry.Priority} in mods.settings and the other has none. Mixing the two makes the compile order depend on how the tool treats unlisted mods, "
                        + $"and {user.Entry.Name} uses {Format.Count(what.Count, "thing")} from {provider.Entry.Name}.",
                        [user, provider], examples,
                        "Give both a priority, with the mod that provides the scripts the higher number (200 or 1000 is fine), or remove priorities from both and let the folder names decide.");
                }
                else
                {
                    Add(Severity.Info, "Dependency", $"{user.Entry.Name} needs {provider.Entry.Name}",
                        $"{user.Entry.Name} uses {Format.Count(what.Count, "thing")} from {provider.Entry.Name}, which is installed, enabled and in the right place in the order (it loads after, so it compiles first).",
                        [user, provider], examples, "Nothing to do. Keep both installed.");
                }
            }
        }

        DeclaredDependencies();
        UnresolvedReferences();
    }

    /// <summary>Compiled scripts link by name; a type nobody declares means a mod they need isn't installed.</summary>
    private void UnresolvedReferences()
    {
        foreach (var user in _active.Where(m => m.Entry.CompiledScripts > 0))
        {
            // the engine declares many types in code, not in scripts (enums such as EAIAttitude), so only names a mod would
            // have written are trusted: base classes of its own classes, and names with an underscore prefix like SU_Oneliner
            var bases = user.Scripts.Bases.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = user.Scripts.NeedTypes
                .Where(t => (bases.Contains(t) || t.Contains('_'))
                            && !user.Scripts.Types.ContainsKey(t) && !TypeExists(t) && !IsBuiltIn(t)
                            && !_all.Any(m => !m.Entry.Enabled && m.Scripts.Types.ContainsKey(t)))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            var unknownGlobals = user.Scripts.NeedGlobals
                .Where(g => !user.Scripts.Globals.Contains(g) && !_game.Globals.Contains(g) && !_active.Any(m => m.Scripts.Globals.Contains(g)))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count == 0 && unknownGlobals.Count == 0) continue;
            Add(Severity.Error, "Missing dependency", $"{user.Entry.Name} uses {Format.Count(unknown.Count + unknownGlobals.Count, "thing")} that nothing installed provides",
                "Its compiled scripts link to these classes or functions, but neither the game nor any enabled mod declares them. A mod it depends on is probably not installed, "
                + "or it was written for a different game version.",
                [user], unknown.Select(t => $"type {t}").Concat(unknownGlobals.Select(g => $"function {g}()")),
                "Install the mod it needs (the mod's page lists its requirements), or remove this one.");
        }
    }

    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "array", "bool", "int", "float", "string", "name", "byte", "void", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64",
        "cname", "double", "handle", "ptr", "soft", "static", "enum", "struct", "class", "state", "import", "native", "unknown", "null",
    };

    private static bool IsBuiltIn(string type) => BuiltIns.Contains(type);

    private static string FixOrder(ScannedMod provider, ScannedMod user) =>
        $"Give {provider.Entry.Name} a higher priority number than {user.Entry.Name} (200 or 1000 is fine; both need one), or remove priorities from both and let folder names decide: a prefix such as modzzz sorts last, so it compiles first.";

    private Dictionary<string, List<ScannedMod>> Providers(Func<ScannedMod, IEnumerable<string>> names)
    {
        var map = new Dictionary<string, List<ScannedMod>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _all)
            foreach (string n in names(m))
            {
                if (!map.TryGetValue(n, out var list)) map[n] = list = [];
                if (!list.Contains(m)) list.Add(m);
            }
        return map;
    }

    private void DeclaredDependencies()
    {
        foreach (var mod in _active)
            foreach (string declared in mod.Entry.DeclaredDependencies)
            {
                var found = _all.FirstOrDefault(m => m != mod && (m.Entry.Name.Equals(declared, StringComparison.OrdinalIgnoreCase)
                                                                  || m.Entry.Name.Equals("mod" + declared, StringComparison.OrdinalIgnoreCase)
                                                                  || m.Entry.Name.Equals("dlc" + declared, StringComparison.OrdinalIgnoreCase)
                                                                  || m.Entry.Title.Equals(declared, StringComparison.OrdinalIgnoreCase)))
                    ?? _all.FirstOrDefault(m => m != mod && m.Entry.Name.Contains(declared, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                    Add(Severity.Error, "Dependency", $"{mod.Entry.Name} needs {declared}, which isn't installed",
                        $"The mod's info.json lists {declared} as a dependency, but no installed mod or DLC matches it.", [mod], [],
                        $"Install {declared}.");
                else if (!found.Entry.Enabled)
                    Add(Severity.Error, "Dependency", $"{mod.Entry.Name} needs {found.Entry.Name}, which is disabled",
                        "The mod's info.json lists it as a dependency.", [mod, found], [], $"Enable {found.Entry.Name} in the mod manager.");
                else if (found.Entry.Order < mod.Entry.Order && !found.Entry.HasFixedPlace)
                    Add(Severity.Error, "Load order", $"{found.Entry.Name} loads before {mod.Entry.Name}, which declares it as a dependency",
                        $"{mod.Entry.Name} is #{mod.Entry.Order} and {found.Entry.Name} #{found.Entry.Order}. Scripts compile in reverse load order, so a dependency has to load after the mods that use it.",
                        [mod, found], [], FixOrder(found, mod));
            }
    }

    // ---- bundles

    private void BundleFiles()
    {
        var byPath = new Dictionary<string, List<(ScannedMod Mod, BundledFile File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _active)
            foreach (var f in mod.Files)
            {
                // every bundle carries its own list of the strings it holds
                if (f.Path.Equals("strings.list", StringComparison.OrdinalIgnoreCase)) continue;
                if (!byPath.TryGetValue(f.Path, out var list)) byPath[f.Path] = list = [];
                list.Add((mod, f));
            }

        // one finding per set of mods that clash, listing every file they clash over
        var sets = new Dictionary<string, (List<ScannedMod> Mods, List<string> Different, List<string> Same)>();
        foreach (var (path, list) in byPath)
        {
            var mods = list.Select(x => x.Mod).Distinct().OrderBy(m => m.Entry.Order).ToList();
            if (mods.Count < 2) continue;
            string key = string.Join("|", mods.Select(m => m.Entry.Name));
            if (!sets.TryGetValue(key, out var set)) sets[key] = set = (mods, [], []);
            bool same = list.Select(x => (x.File.Crc, x.File.Size)).Distinct().Count() == 1;
            string line = same ? path : $"{path}   ({string.Join(" / ", list.OrderBy(x => x.Mod.Entry.Order).Select((x, i) => $"{(i == 0 ? "\u2605 " : "")}{x.Mod.Entry.Name}: {Format.Bytes(x.File.Size)}"))})";
            (same ? set.Same : set.Different).Add(line);
        }
        foreach (var (mods, different, same) in sets.Values)
        {
            if (different.Count > 0 && IsMerged(mods[0]))
                Add(Severity.Info, "Bundle file", $"{Format.Count(different.Count, "file")} also in the bundles of {Format.Count(mods.Count - 1, "other mod")}, covered by {mods[0].Entry.Name}",
                    $"{Ranked(mods)} each contain these files with different content. {mods[0].Entry.Name} loads first and is a merge, so it should already combine them.",
                    mods, different.Order(StringComparer.OrdinalIgnoreCase), "Re-run the merger whenever one of these mods is updated.", mods[0]);
            else if (different.Count > 0)
            {
                string first = mods[0].Entry.Name;
                Add(Severity.Warning, "Bundle file", $"{Format.Count(different.Count, "file")} in {mods.Count} mods' bundles differ",
                    $"{Ranked(mods)} each contain these files with different content. The game uses one copy per file: {first}'s, the highest priority, in each case. The others' changes to them are lost.",
                    mods, different.Order(StringComparer.OrdinalIgnoreCase), "Decide which mod's version you want, or let Script Merger Remastered combine them where it can (Open Script Merger, below).", mods[0]);
            }
            if (same.Count > 0)
                Add(Severity.Info, "Bundle file", $"{Format.Count(same.Count, "identical file")} shared by {mods.Count} mods' bundles",
                    $"{Ranked(mods)} ship byte-identical copies of these files, so it doesn't matter which one is used.", mods, same.Order(StringComparer.OrdinalIgnoreCase),
                    "No action needed.");
        }
    }

    // ---- strings

    private void StringIds()
    {
        var byId = new Dictionary<uint, List<(ScannedMod Mod, string Text)>>();
        foreach (var mod in _active)
            foreach (var (id, text) in mod.Strings)
            {
                if (!byId.TryGetValue(id, out var list)) byId[id] = list = [];
                list.Add((mod, text));
            }
        var sets = new Dictionary<string, (List<ScannedMod> Mods, List<string> Lines)>();
        foreach (var (id, list) in byId)
        {
            if (list.Count < 2 || list.Select(x => x.Text).Distinct().Count() < 2) continue;
            var mods = list.Select(x => x.Mod).OrderBy(m => m.Entry.Order).ToList();
            string key = string.Join("|", mods.Select(m => m.Entry.Name));
            if (!sets.TryGetValue(key, out var set)) sets[key] = set = (mods, []);
            string Cut(string t) => t.Length > 50 ? t[..50] + "…" : t;
            set.Lines.Add($"{id}   " + string.Join("  vs  ", list.OrderBy(x => x.Mod.Entry.Order).Select((x, i) => $"{(i == 0 ? "\u2605 " : "")}{x.Mod.Entry.Name}: \"{Cut(x.Text)}\"")));
        }
        foreach (var (mods, lines) in sets.Values)
            Add(Severity.Warning, "Strings", $"{Format.Count(lines.Count, "string ID")} used by {mods.Count} mods with different text",
                $"{Ranked(mods)} each define strings with the same IDs. The game shows one text for an ID, so one mod's text appears where the other's belongs.",
                mods, lines, "Move one mod's strings to a free ID block (the strings editor's Check IDs suggests one).", mods[0]);
    }

    // ---- compiled for another platform

    private void Platforms()
    {
        var compiled = _active.Where(m => m.Platform is { Length: > 0 }).ToList();
        var groups = compiled.GroupBy(m => m.Platform!, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count < 2) return;
        var common = groups.OrderByDescending(g => g.Count()).First();
        foreach (var g in groups.Where(g => g != common))
            Add(Severity.Warning, "Build", $"{Format.Count(g.Count(), "mod")} compiled for {g.Key}, the rest for {common.Key}",
                $"Most compiled scripts say they were built for {common.Key}.", g, g.Select(m => $"{m.Entry.Name}: {m.Platform}, build {m.Build}"),
                "Scripts built for another platform may not load. Get the version made for this game.");
    }

    // ---- mods.settings

    private void SettingsNotes(Dictionary<string, (bool Enabled, int? Priority)> settings)
    {
        if (_report.SettingsFile is null)
            _report.Warnings.Add("mods.settings wasn't found (Documents\\The Witcher 3), so priorities are mod folder order. Run the game once, or set the file in the box above.");
        else
        {
            var unlisted = _all.Where(m => !m.Entry.HasFixedPlace && m.Entry.Priority is null).ToList();
            if (unlisted.Count > 0)
                Add(Severity.Info, "Priority", $"{Format.Count(unlisted.Count, "mod")} not listed in mods.settings",
                    "mods.settings gives each mod a priority. These have none, so they load after the listed mods, in folder name order. For script mods leaving priorities out is the recommended way.",
                    unlisted, unlisted.Select(m => m.Entry.Name), "Nothing, unless one of them also needs a priority: a mod with a priority and a dependency without one is flagged separately.");

            var ties = _all.Where(m => m.Entry.Priority is not null && !m.Entry.HasFixedPlace).GroupBy(m => m.Entry.Priority!.Value).Where(g => g.Count() > 1);
            foreach (var g in ties)
                Add(Severity.Info, "Priority", $"{g.Count()} mods share priority {g.Key}",
                    "Equal priorities fall back to folder name order.", g, g.Select(m => m.Entry.Name), "Give each a different number if it matters which one wins.");

            string modsRoot = _report.ModsFolder;
            var stale = settings.Keys.Where(k => !Directory.Exists(Path.Combine(modsRoot, k)) && !k.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)).ToList();
            if (stale.Count > 0)
                Add(Severity.Warning, "Priority", $"{Format.Count(stale.Count, "entry", "entries")} in mods.settings for mods that aren't installed",
                    "Priorities of mods you removed stay behind and still shape the compile order, which has caused \"Unknown base class\" errors for other players.",
                    [], stale, "Delete these sections from mods.settings (the mod manager's Mod Settings tab shows the same file).");
        }

        var disabled = _all.Where(m => !m.Entry.Enabled).ToList();
        if (disabled.Count > 0)
            Add(Severity.Info, "Priority", $"{Format.Count(disabled.Count, "mod")} disabled in mods.settings",
                "These aren't loaded, so they are left out of every check here except dependencies.", disabled, disabled.Select(m => m.Entry.Name), "None needed.");
    }

    // ---------------------------------------------------------------- export

    public static string ToMarkdown(LoadOrderReport r)
    {
        var sb = new StringBuilder();
        sb.Append("# Load order report\n\n");
        sb.Append($"Mods folder: `{r.ModsFolder}`  \n");
        sb.Append($"{Format.Count(r.Mods.Count, "mod")}: {r.Count(Severity.Error)} problems, {r.Count(Severity.Warning)} warnings, {r.Count(Severity.Info)} notes.\n\n");
        sb.Append("## Load order\n\n| # | Mod | Priority | State | Contents |\n|---|---|---|---|---|\n");
        foreach (var m in r.Mods)
            sb.Append($"| {m.Order} | {m.Name}{(m.IsDlc ? " (DLC)" : m.IsModio ? " (mod.io)" : "")} | {(m.Priority?.ToString() ?? "–")} | {(m.Enabled ? "on" : "off")} | {m.Contents} |\n");
        foreach (var sev in new[] { Severity.Error, Severity.Warning, Severity.Info })
        {
            var list = r.Findings.Where(f => f.Severity == sev).ToList();
            if (list.Count == 0) continue;
            sb.Append($"\n## {(sev == Severity.Error ? "Problems" : sev == Severity.Warning ? "Warnings" : "Notes")} ({list.Count})\n");
            foreach (var f in list)
            {
                sb.Append($"\n### {f.Title}\n\n{f.Summary}\n\n");
                if (f.Mods.Count > 0) sb.Append($"Mods: {string.Join(", ", f.Mods)}\n\n");
                if (f.Winner is not null) sb.Append($"**Winner: {f.Winner}**\n\n");
                foreach (string item in f.Items.Take(60)) sb.Append($"- `{item}`\n");
                if (f.Items.Count > 60) sb.Append($"- … and {f.Items.Count - 60} more\n");
                sb.Append($"\n**What to do:** {f.Advice}\n");
            }
        }
        return sb.ToString();
    }
}
