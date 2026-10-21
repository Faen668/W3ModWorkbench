using System.IO;
using System.Text;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;

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

        progress?.Report(L.T("LoadOrderAnalyzer.Reading", L.T("Count.Mod", ordered.Count)));
        var scanned = new ScannedMod[ordered.Count];
        int done = 0;
        var gameTask = gameScripts is null ? Task.FromResult(GameIndex.Empty) : GameIndex.LoadAsync(gameScripts);
        await Task.Run(() => Parallel.For(0, ordered.Count,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) },
            i =>
            {
                scanned[i] = ModScanner.Scan(ordered[i], token);
                progress?.Report(L.T("LoadOrderAnalyzer.Read", ordered[i].Name, Interlocked.Increment(ref done), ordered.Count));
            }), token);

        progress?.Report(L.T("LoadOrderAnalyzer.ReadingGamesScripts"));
        var game = await gameTask;
        report.VanillaTypes = game.Types.Count;
        token.ThrowIfCancellationRequested();

        progress?.Report(L.T("LoadOrderAnalyzer.CheckingConflicts"));
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
            list = [.. list.Take(MaxItems), L.T("LoadOrderAnalyzer.More", more)];
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
        else _report.Warnings.Add(L.T("LoadOrderAnalyzer.GamesScriptsWerentFound"));
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
                    fatal ? L.T("LoadOrderAnalyzer.FileGameWouldLoad") : L.T("LoadOrderAnalyzer.CheckFileDamage"));
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
                Add(Severity.Info, L.T("LoadOrderAnalyzer.ScriptFile"), L.T("LoadOrderAnalyzer.IdenticalCopiesScripts", g.Key), L.T("LoadOrderAnalyzer.ShipSameFile", Ranked(mods)), mods, [],
                    L.T("LoadOrderAnalyzer.HarmlessWhileTheyStay"));
            }
            else if (IsMerged(mods[0]))
            {
                Add(Severity.Info, L.T("LoadOrderAnalyzer.ScriptFile"), L.T("LoadOrderAnalyzer.ScriptsCovered", g.Key, mods[0].Entry.Name),
                    L.T("LoadOrderAnalyzer.EachShipFileLoads", Ranked(mods), mods[0].Entry.Name),
                    mods, [], L.T("LoadOrderAnalyzer.ReRunMergerWhenever"), mods[0]);
            }
            else
            {
                string first = mods[0].Entry.Name;
                Add(Severity.Error, L.T("LoadOrderAnalyzer.ScriptFile"), L.T("LoadOrderAnalyzer.TwoModsReplaceScripts", g.Key),
                    L.T(replacesGame ? "LoadOrderAnalyzer.DifferentVersionsGame" : "LoadOrderAnalyzer.DifferentVersionsFile", Ranked(mods), first),
                    mods, g.OrderBy(x => x.Mod.Entry.Order).Select((x, i) => $"{(i == 0 ? "\u2605 " : "   ")}{x.Mod.Entry.Name}: {x.File.FullPath}"),
                    replacesGame
                        ? L.T("LoadOrderAnalyzer.MergeFilesScriptMerger")
                        : L.T("LoadOrderAnalyzer.RemoveOneModsRename"), mods[0]);
            }
        }

        foreach (var mod in _active)
        {
            var over = mod.Loose.Where(l => _game.Files.ContainsKey(l.Relative)).Select(l => l.Relative).ToList();
            if (over.Count == 0) continue;
            Add(Severity.Info, L.T("LoadOrderAnalyzer.ScriptFile"), L.T("LoadOrderAnalyzer.ReplacesWhole", mod.Entry.Name, L.T("Count.GameScript", over.Count)),
                L.T("LoadOrderAnalyzer.WholeFileReplacementHides"),
                [mod], over.Select(o => "scripts\\" + o),
                L.T("LoadOrderAnalyzer.PreferAnnotationsWrapMethodFriends"));
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
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.TypeDefinedTwice"), L.T("LoadOrderAnalyzer.DefinedMods", g.Key, mods.Count),
                        L.T("LoadOrderAnalyzer.EachDeclareScriptsUse", Ranked(mods), g.First().Kind, g.Key),
                        mods, [], L.T("LoadOrderAnalyzer.OnlyOneTheseMods"));
            }

            // a compiled project that declares a game class replaces it whole, as a loose file does
            if (!isEnum && _game.Types.Contains(g.Key))
                foreach (var x in g.Where(x => !x.Mod.Scripts.TypeFiles.ContainsKey(x.Name)))
                    Add(Severity.Info, L.T("LoadOrderAnalyzer.ScriptFile"), L.T("LoadOrderAnalyzer.ReplacesGamesWhole", x.Mod.Entry.Name, g.Key),
                        L.T("LoadOrderAnalyzer.ModsCompiledScriptsDeclare", x.Kind, g.Key),
                        [x.Mod], [], L.T("LoadOrderAnalyzer.PreferAnnotationsWrapMethodFriends"));

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
            Add(Severity.Warning, L.T("LoadOrderAnalyzer.EnumValue"), L.T("LoadOrderAnalyzer.AddedMods", enumName, g.Key, who.Count),
                L.T("LoadOrderAnalyzer.BothAddValue", Ranked(who), enumName), who,
                g.Select(x => $"{x.Mod.Entry.Name}: {(x.Value is { } v ? v.ToString() : L.T("LoadOrderAnalyzer.ValueNotRecorded"))}"),
                L.T("LoadOrderAnalyzer.ValuesMayDifferWhich"));
        }
        var byValue = mods.SelectMany(m => m.Scripts.EnumMembers.GetValueOrDefault(enumName)?.Where(kv => kv.Value is not null).Select(kv => (Mod: m, Name: kv.Key, Value: kv.Value!.Value)) ?? [])
            .GroupBy(x => x.Value).Where(g => g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                                              && g.Select(x => x.Mod).Distinct().Count() > 1);
        foreach (var g in byValue)
        {
            var who = g.Select(x => x.Mod).Distinct().ToList();
            Add(Severity.Warning, L.T("LoadOrderAnalyzer.EnumValue"), L.T("LoadOrderAnalyzer.ValueMeansTwoThings", enumName, g.Key),
                L.T("LoadOrderAnalyzer.GiveDifferentNamesNumber", Ranked(who), g.Key, enumName), who,
                g.Select(x => $"{x.Mod.Entry.Name}: {x.Name} = {x.Value}"),
                L.T("LoadOrderAnalyzer.SavedGamesCodeComparing"));
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
            Add(Severity.Error, L.T("LoadOrderAnalyzer.FunctionDefinedTwice"), L.T("LoadOrderAnalyzer.GlobalFunctionDefinedMods", g.Key, mods.Count),
                L.T("LoadOrderAnalyzer.DeclareOnlyOneDefinition", Ranked(mods)), mods, [], L.T("LoadOrderAnalyzer.RenameOneThemRemove"));
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
                Add(Severity.Error, L.T("LoadOrderAnalyzer.SameFunction"), L.T("LoadOrderAnalyzer.AddedMods2", target, adders.Count),
                    L.T("LoadOrderAnalyzer.EachAddClassCant", Ranked(adders), kind, target), adders, lines,
                    L.T("LoadOrderAnalyzer.RenameMemberOneMods"));
            if (replacers.Count > 1)
                Add(Severity.Error, L.T("LoadOrderAnalyzer.SameFunction"), L.T("LoadOrderAnalyzer.ReplacedMods", target, replacers.Count),
                    L.T("LoadOrderAnalyzer.EachUseReplaceMethodOnly", Ranked(replacers), target), replacers, lines,
                    L.T("LoadOrderAnalyzer.KeepOneIfBoth"),
                    replacers.OrderBy(m => m.Entry.Order).First());
            else if (replacers.Count == 1 && wrappers.Any(w => w != replacers[0]))
            {
                var wrappingOthers = wrappers.Where(w => w != replacers[0]).ToList();
                var later = wrappingOthers.Where(w => w.Entry.Order > replacers[0].Entry.Order).ToList();
                Add(Severity.Warning, L.T("LoadOrderAnalyzer.SameFunction"), L.T("LoadOrderAnalyzer.ReplacedWrapped", target, replacers[0].Entry.Name, L.T("Count.OtherMod", wrappingOthers.Count)),
                    later.Count > 0
                        ? L.T("LoadOrderAnalyzer.ReplaceAndWrapsLater", replacers[0].Entry.Name, replacers[0].Entry.Order, Ranked(later))
                        : L.T("LoadOrderAnalyzer.ReplaceAndWraps", replacers[0].Entry.Name, replacers[0].Entry.Order),
                    g.Select(x => x.Mod).Distinct(), lines,
                    L.T("LoadOrderAnalyzer.CheckGameBothMods"));
            }
            else if (wrappers.Count > 1 && replacers.Count == 0)
                Add(Severity.Info, L.T("LoadOrderAnalyzer.SameFunction"), L.T("LoadOrderAnalyzer.WrappedMods", target, wrappers.Count),
                    L.T("LoadOrderAnalyzer.EachWrapWrapsStack", Ranked(wrappers)),
                    wrappers, lines, L.T("LoadOrderAnalyzer.NoActionUnlessOne"));
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
                Add(Severity.Error, L.T("LoadOrderAnalyzer.MissingTarget"), L.T("LoadOrderAnalyzer.ChangesMissingClasses", mod.Entry.Name, missingTypes.Count),
                    disabledOwner.Count > 0
                        ? L.T("LoadOrderAnalyzer.MissingClassesDisabled", string.Join(", ", disabledOwner.Select(Who)), disabledOwner.Count)
                        : L.T("LoadOrderAnalyzer.MissingClasses"),
                    [mod, .. disabledOwner], missingTypes.SelectMany(kv => kv.Value.Select(v => L.T("LoadOrderAnalyzer.ClassNotFound", v, kv.Key))),
                    L.T("LoadOrderAnalyzer.ModProbablyWrittenAnother"));
            }
            if (missingMembers.Count > 0)
                Add(Severity.Error, L.T("LoadOrderAnalyzer.MissingTarget"), L.T("LoadOrderAnalyzer.WrapsReplacesGameDoesnt", mod.Entry.Name, L.T("Count.Function", missingMembers.Count)),
                    L.T("LoadOrderAnalyzer.ClassesExistButThese"),
                    [mod], missingMembers, L.T("LoadOrderAnalyzer.GetVersionModMade"));
            if (missingGlobals.Count > 0)
                Add(Severity.Error, L.T("LoadOrderAnalyzer.MissingTarget"), L.T("LoadOrderAnalyzer.ChangesGameDoesntHave", mod.Entry.Name, L.T("Count.GlobalFunction", missingGlobals.Count)),
                    L.T("LoadOrderAnalyzer.NoGlobalFunctionThese"), [mod], missingGlobals,
                    L.T("LoadOrderAnalyzer.GetVersionModMade"));
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
                Add(Severity.Warning, L.T("LoadOrderAnalyzer.MissingTarget"), L.T("LoadOrderAnalyzer.CopyGameClassLacks", owner.Entry.Name, other.Entry.Name),
                    L.T("LoadOrderAnalyzer.ReplacesGamesClassWhole", owner.Entry.Name, other.Entry.Name),
                    [owner, other], lost, L.T("LoadOrderAnalyzer.UpdateReplacementGamesCurrent"));
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
                    foreach (var p in ps) Use(p, L.T("LoadOrderAnalyzer.Function", g));
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
                        foreach (var p in ps) Use(p, L.T("LoadOrderAnalyzer.Member", k));
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
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.Category.Dependency"), L.T("LoadOrderAnalyzer.NeedsWhichDisabled", user.Entry.Name, provider.Entry.Name),
                        L.T("LoadOrderAnalyzer.UsesOnlyProvidesMods", user.Entry.Name, L.T("Count.Thing", what.Count), provider.Entry.Name),
                        [user, provider], examples, L.T("LoadOrderAnalyzer.EnableDisableToo", provider.Entry.Name, user.Entry.Name));
                }
                else if (provider.Entry.Order < user.Entry.Order && !provider.Entry.HasFixedPlace)
                {
                    // scripts compile in reverse load order, so what a mod builds on has to load after it
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.LoadOrder"), L.T("LoadOrderAnalyzer.LoadsBeforeWhichBuilds", provider.Entry.Name, user.Entry.Name),
                        L.T("LoadOrderAnalyzer.UsesGameCompilesScripts", user.Entry.Name, user.Entry.Order, L.T("Count.Thing", what.Count), provider.Entry.Name, provider.Entry.Order),
                        [user, provider], examples, FixOrder(provider, user));
                }
                else if (provider.Entry.Priority is null != user.Entry.Priority is null && !provider.Entry.HasFixedPlace)
                {
                    var set = provider.Entry.Priority is null ? user : provider;
                    Add(Severity.Warning, L.T("LoadOrderAnalyzer.LoadOrder"), L.T("LoadOrderAnalyzer.OnlyOneHasPriority", user.Entry.Name, provider.Entry.Name),
                        L.T("LoadOrderAnalyzer.HasPriorityModsSettings", set.Entry.Name, set.Entry.Priority, user.Entry.Name, L.T("Count.Thing", what.Count), provider.Entry.Name),
                        [user, provider], examples,
                        L.T("LoadOrderAnalyzer.GiveBothPriorityMod"));
                }
                else
                {
                    Add(Severity.Info, L.T("LoadOrderAnalyzer.Category.Dependency"), L.T("LoadOrderAnalyzer.Needs", user.Entry.Name, provider.Entry.Name),
                        L.T("LoadOrderAnalyzer.UsesWhichInstalledEnabled", user.Entry.Name, L.T("Count.Thing", what.Count), provider.Entry.Name),
                        [user, provider], examples, L.T("LoadOrderAnalyzer.NothingDoKeepBoth"));
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
            Add(Severity.Error, L.T("LoadOrderAnalyzer.MissingDependency"), L.T("LoadOrderAnalyzer.UsesNothingInstalledProvides", user.Entry.Name, L.T("Count.Thing", unknown.Count + unknownGlobals.Count)),
                L.T("LoadOrderAnalyzer.CompiledScriptsLinkThese"),
                [user], unknown.Select(t => L.T("LoadOrderAnalyzer.Type", t)).Concat(unknownGlobals.Select(g => L.T("LoadOrderAnalyzer.Function", g))),
                L.T("LoadOrderAnalyzer.InstallModNeedsMods"));
        }
    }

    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "array", "bool", "int", "float", "string", "name", "byte", "void", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64",
        "cname", "double", "handle", "ptr", "soft", "static", "enum", "struct", "class", "state", "import", "native", "unknown", "null",
    };

    private static bool IsBuiltIn(string type) => BuiltIns.Contains(type);

    private static string FixOrder(ScannedMod provider, ScannedMod user) =>
        L.T("LoadOrderAnalyzer.GiveHigherPriorityNumber", provider.Entry.Name, user.Entry.Name);

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
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.Category.Dependency"), L.T("LoadOrderAnalyzer.NeedsWhichIsntInstalled", mod.Entry.Name, declared),
                        L.T("LoadOrderAnalyzer.ModsInfoJsonLists", declared), [mod], [],
                        L.T("LoadOrderAnalyzer.Install", declared));
                else if (!found.Entry.Enabled)
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.Category.Dependency"), L.T("LoadOrderAnalyzer.NeedsWhichDisabled", mod.Entry.Name, found.Entry.Name),
                        L.T("LoadOrderAnalyzer.ModsInfoJsonLists2"), [mod, found], [], L.T("LoadOrderAnalyzer.EnableModManager", found.Entry.Name));
                else if (found.Entry.Order < mod.Entry.Order && !found.Entry.HasFixedPlace)
                    Add(Severity.Error, L.T("LoadOrderAnalyzer.LoadOrder"), L.T("LoadOrderAnalyzer.LoadsBeforeWhichDeclares", found.Entry.Name, mod.Entry.Name),
                        L.T("LoadOrderAnalyzer.ScriptsCompileReverseLoad", mod.Entry.Name, mod.Entry.Order, found.Entry.Name, found.Entry.Order),
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
                Add(Severity.Info, L.T("LoadOrderAnalyzer.BundleFile"), L.T("LoadOrderAnalyzer.AlsoBundlesCovered", L.T("Count.File", different.Count), L.T("Count.OtherMod", mods.Count - 1), mods[0].Entry.Name),
                    L.T("LoadOrderAnalyzer.EachContainTheseFiles", Ranked(mods), mods[0].Entry.Name),
                    mods, different.Order(StringComparer.OrdinalIgnoreCase), L.T("LoadOrderAnalyzer.ReRunMergerWhenever"), mods[0]);
            else if (different.Count > 0)
            {
                string first = mods[0].Entry.Name;
                Add(Severity.Warning, L.T("LoadOrderAnalyzer.BundleFile"), L.T("LoadOrderAnalyzer.ModsBundlesDiffer", L.T("Count.File", different.Count), mods.Count),
                    L.T("LoadOrderAnalyzer.EachContainTheseFiles2", Ranked(mods), first),
                    mods, different.Order(StringComparer.OrdinalIgnoreCase), L.T("LoadOrderAnalyzer.DecideWhichModsVersion"), mods[0]);
            }
            if (same.Count > 0)
                Add(Severity.Info, L.T("LoadOrderAnalyzer.BundleFile"), L.T("LoadOrderAnalyzer.SharedModsBundles", L.T("Count.IdenticalFile", same.Count), mods.Count),
                    L.T("LoadOrderAnalyzer.ShipByteIdenticalCopies", Ranked(mods)), mods, same.Order(StringComparer.OrdinalIgnoreCase),
                    L.T("LoadOrderAnalyzer.NoActionNeeded"));
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
            Add(Severity.Warning, L.T("LoadOrderAnalyzer.Category.Strings"), L.T("LoadOrderAnalyzer.UsedModsDifferentText", L.T("Count.StringID", lines.Count), mods.Count),
                L.T("LoadOrderAnalyzer.EachDefineStringsSame", Ranked(mods)),
                mods, lines, L.T("LoadOrderAnalyzer.MoveOneModsStrings"), mods[0]);
    }

    // ---- compiled for another platform

    private void Platforms()
    {
        var compiled = _active.Where(m => m.Platform is { Length: > 0 }).ToList();
        var groups = compiled.GroupBy(m => m.Platform!, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count < 2) return;
        var common = groups.OrderByDescending(g => g.Count()).First();
        foreach (var g in groups.Where(g => g != common))
            Add(Severity.Warning, L.T("LoadOrderAnalyzer.Category.Build"), L.T("LoadOrderAnalyzer.CompiledRest", L.T("Count.Mod", g.Count()), g.Key, common.Key),
                L.T("LoadOrderAnalyzer.MostCompiledScriptsSay", common.Key), g, g.Select(m => L.T("LoadOrderAnalyzer.Build", m.Entry.Name, m.Platform, m.Build)),
                L.T("LoadOrderAnalyzer.ScriptsBuiltAnotherPlatform"));
    }

    // ---- mods.settings

    private void SettingsNotes(Dictionary<string, (bool Enabled, int? Priority)> settings)
    {
        if (_report.SettingsFile is null)
            _report.Warnings.Add(L.T("LoadOrderAnalyzer.ModsSettingsWasntFound"));
        else
        {
            var unlisted = _all.Where(m => !m.Entry.HasFixedPlace && m.Entry.Priority is null).ToList();
            if (unlisted.Count > 0)
                Add(Severity.Info, L.T("LoadOrderAnalyzer.Category.Priority"), L.T("LoadOrderAnalyzer.NotListedModsSettings", L.T("Count.Mod", unlisted.Count)),
                    L.T("LoadOrderAnalyzer.ModsSettingsGivesEach"),
                    unlisted, unlisted.Select(m => m.Entry.Name), L.T("LoadOrderAnalyzer.NothingUnlessOneThem"));

            var ties = _all.Where(m => m.Entry.Priority is not null && !m.Entry.HasFixedPlace).GroupBy(m => m.Entry.Priority!.Value).Where(g => g.Count() > 1);
            foreach (var g in ties)
                Add(Severity.Info, L.T("LoadOrderAnalyzer.Category.Priority"), L.T("LoadOrderAnalyzer.ModsSharePriority", g.Count(), g.Key),
                    L.T("LoadOrderAnalyzer.EqualPrioritiesFallBack"), g, g.Select(m => m.Entry.Name), L.T("LoadOrderAnalyzer.GiveEachDifferentNumber"));

            string modsRoot = _report.ModsFolder;
            var stale = settings.Keys.Where(k => !Directory.Exists(Path.Combine(modsRoot, k)) && !k.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)).ToList();
            if (stale.Count > 0)
                Add(Severity.Warning, L.T("LoadOrderAnalyzer.Category.Priority"), L.T("LoadOrderAnalyzer.ModsSettingsModsArent", L.T("Count.Entry", stale.Count)),
                    L.T("LoadOrderAnalyzer.PrioritiesModsYouRemoved"),
                    [], stale, L.T("LoadOrderAnalyzer.DeleteTheseSectionsMods"));
        }

        var disabled = _all.Where(m => !m.Entry.Enabled).ToList();
        if (disabled.Count > 0)
            Add(Severity.Info, L.T("LoadOrderAnalyzer.Category.Priority"), L.T("LoadOrderAnalyzer.DisabledModsSettings", L.T("Count.Mod", disabled.Count)),
                L.T("LoadOrderAnalyzer.TheseArentLoadedSo"), disabled, disabled.Select(m => m.Entry.Name), L.T("LoadOrderAnalyzer.NoneNeeded"));
    }

    // ---------------------------------------------------------------- export

    public static string ToMarkdown(LoadOrderReport r)
    {
        var sb = new StringBuilder();
        sb.Append($"# {L.T("LoadOrderAnalyzer.Report.Title")}\n\n");
        sb.Append($"{L.T("LoadOrderAnalyzer.Report.ModsFolder", $"`{r.ModsFolder}`")}  \n");
        sb.Append($"{L.T("LoadOrderAnalyzer.Report.Counts", L.T("Count.Mod", r.Mods.Count), r.Count(Severity.Error), r.Count(Severity.Warning), r.Count(Severity.Info))}\n\n");
        sb.Append($"## {L.T("LoadOrderAnalyzer.Report.LoadOrder")}\n\n| # | {L.T("LoadOrderAnalyzer.Report.Mod")} | {L.T("LoadOrderAnalyzer.Report.Priority")} | {L.T("LoadOrderAnalyzer.Report.State")} | {L.T("LoadOrderAnalyzer.Report.Contents")} |\n|---|---|---|---|---|\n");
        foreach (var m in r.Mods)
            sb.Append($"| {m.Order} | {m.Name}{(m.IsDlc ? " (DLC)" : m.IsModio ? " (mod.io)" : "")} | {(m.Priority?.ToString() ?? "–")} | {(m.Enabled ? L.T("LoadOrderAnalyzer.Report.On") : L.T("LoadOrderAnalyzer.Report.Off"))} | {m.Contents} |\n");
        foreach (var sev in new[] { Severity.Error, Severity.Warning, Severity.Info })
        {
            var list = r.Findings.Where(f => f.Severity == sev).ToList();
            if (list.Count == 0) continue;
            sb.Append($"\n## {L.T(sev == Severity.Error ? "LoadOrderAnalyzer.Report.Problems" : sev == Severity.Warning ? "LoadOrderAnalyzer.Report.Warnings" : "LoadOrderAnalyzer.Report.Notes")} ({list.Count})\n");
            foreach (var f in list)
            {
                sb.Append($"\n### {f.Title}\n\n{f.Summary}\n\n");
                if (f.Mods.Count > 0) sb.Append($"{L.T("LoadOrderAnalyzer.Report.Mods", string.Join(", ", f.Mods))}\n\n");
                if (f.Winner is not null) sb.Append($"**{L.T("LoadOrderAnalyzer.Report.Winner", f.Winner)}**\n\n");
                foreach (string item in f.Items.Take(60)) sb.Append($"- `{item}`\n");
                if (f.Items.Count > 60) sb.Append($"- {L.T("LoadOrderAnalyzer.Report.More", f.Items.Count - 60)}\n");
                sb.Append($"\n**{L.T("LoadOrderAnalyzer.Report.WhatToDo")}** {f.Advice}\n");
            }
        }
        return sb.ToString();
    }
}
