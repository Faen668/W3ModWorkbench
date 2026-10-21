using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>
/// Compares two sources and says how much of one is in the other. Scripts are compared by their code (as written, and
/// as a shape that ignores names), menus and text by their lines, and bundle contents and other files by SHA-256.
/// The game's own scripts can be set aside first, so a mod isn't blamed for starting from the code it wraps.
/// </summary>
public static class InspectorEngine
{
    private const double ShapeWeight = 0.9;
    private const int HotShingle = 40;
    private const int MaxEvidencePerKind = 300;

    // each kind's name is under its key in the string tables
    private static readonly (UnitKind Kind, string Key, double Weight)[] Categories =
    [
        (UnitKind.Script, "InspectorEngine.Category.Scripts", 40),
        (UnitKind.Xml, "InspectorEngine.Category.Xml", 20),
        (UnitKind.Text, "InspectorEngine.Category.Text", 10),
        (UnitKind.Asset, "InspectorEngine.Category.Assets", 30),
    ];

    public static InspectionReport Run(string originalPath, string suspectPath, InspectOptions options,
        IProgress<InspectProgress>? progress, CancellationToken token)
    {
        GameBaseline? baseline = null;
        if (options.IgnoreGameCode && options.GameFolder is { Length: > 0 } game && GameLocator.IsGameFolder(game))
        {
            progress?.Report(new InspectProgress(L.T("InspectorEngine.ReadingGamesOwnScripts"), 0, ""));
            baseline = GameBaseline.Load(game, progress, token);
        }

        var original = SourceScanner.Scan(originalPath, progress, L.T("InspectorEngine.ReadingOriginal"), token);
        var suspect = SourceScanner.Scan(suspectPath, progress, L.T("InspectorEngine.ReadingSuspect"), token);

        progress?.Report(new InspectProgress(L.T("InspectorEngine.Comparing"), 0.05, L.T("InspectorEngine.SettingAsideGamesOwn")));
        int gameFiles = 0;
        if (baseline is not null)
            foreach (var unit in original.Units.Concat(suspect.Units).Where(u => u.Kind == UnitKind.Script && u.Script is not null))
            {
                unit.Sig1 = Hashing.Except(unit.Script!.RawShingles, baseline.Raw);
                unit.Sig2 = Hashing.Except(unit.Script.AbstractShingles, baseline.Shape);
                unit.IsGameCode = unit.Script.RawShingles.Length > 0 && unit.Sig1.Length == 0;
                if (unit.IsGameCode) gameFiles++;
            }
        token.ThrowIfCancellationRequested();

        progress?.Report(new InspectProgress(L.T("InspectorEngine.Comparing"), 0.2, L.T("InspectorEngine.MatchingFiles")));
        var matches = FindMatches(original, suspect, baseline, token);

        progress?.Report(new InspectProgress(L.T("InspectorEngine.Comparing"), 0.6, L.T("InspectorEngine.Scoring")));
        var categories = new List<CategoryResult>();
        foreach (var (kind, key, weight) in Categories)
            categories.Add(Score(kind, L.T(key), weight, original, suspect, matches));

        double? Direction(Func<CategoryResult, double?> pick)
        {
            double total = 0, sum = 0;
            foreach (var c in categories)
                if (pick(c) is { } v)
                {
                    total += c.Weight;
                    sum += c.Weight * v;
                }
            return total == 0 ? null : sum / total;
        }

        progress?.Report(new InspectProgress(L.T("InspectorEngine.Comparing"), 0.75, L.T("InspectorEngine.CollectingSharedCommentsNames")));
        var evidence = CollectEvidence(original, suspect, baseline, token);

        progress?.Report(new InspectProgress(L.T("InspectorEngine.Comparing"), 0.9, L.T("InspectorEngine.ComparingCodingStyle")));
        var styleA = StyleProfile.Compute(original.Units.Where(u => u.Kind == UnitKind.Script && !u.IsGameCode).Select(u => u.Text!));
        var styleB = StyleProfile.Compute(suspect.Units.Where(u => u.Kind == UnitKind.Script && !u.IsGameCode).Select(u => u.Text!));
        List<StyleFeature> style = [];
        double? styleSimilarity = null;
        string styleNote;
        if (styleA is not null && styleB is not null)
        {
            style = StyleProfile.Compare(styleA, styleB);
            styleSimilarity = StyleProfile.Similarity(style);
            styleNote = styleSimilarity switch
            {
                >= 0.92 => L.T("InspectorEngine.TwoWrittenAlmostSame"),
                >= 0.85 => L.T("InspectorEngine.ManyHabitsMatchCommon"),
                >= 0.7 => L.T("InspectorEngine.SomeHabitsMatchSome"),
                _ => L.T("InspectorEngine.TwoWrittenDifferently"),
            };
        }
        else
        {
            styleNote = L.T("InspectorEngine.StyleNeedsLeastLines", StyleProfile.MinLines);
        }

        var report = new InspectionReport
        {
            Original = original,
            Suspect = suspect,
            GameCodeIgnored = baseline is not null,
            GameScriptsRead = baseline?.Files ?? 0,
            GameCodeFiles = gameFiles,
            Categories = categories,
            Matches = matches,
            Evidence = evidence,
            Style = style,
            StyleSimilarity = styleSimilarity,
            StyleNote = styleNote,
            OriginalInSuspect = Direction(c => c.OriginalInSuspect) ?? 0,
            SuspectInOriginal = Direction(c => c.SuspectInOriginal) ?? 0,
        };
        report.Findings.AddRange(Summarise(report));
        progress?.Report(new InspectProgress(L.T("InspectorEngine.Done"), 1, ""));
        return report;
    }

    // ---------------------------------------------------------------- matching

    private static List<MatchRow> FindMatches(SourceScan original, SourceScan suspect, GameBaseline? baseline, CancellationToken token)
    {
        var rows = new List<MatchRow>();
        var seen = new HashSet<(InspectUnit, InspectUnit)>();

        foreach (var kind in new[] { UnitKind.Script, UnitKind.Xml, UnitKind.Text })
        {
            token.ThrowIfCancellationRequested();
            var a = original.Units.Where(u => u.Kind == kind && Usable(u)).ToList();
            var b = suspect.Units.Where(u => u.Kind == kind && Usable(u)).ToList();
            var pairs = new HashSet<(int, int)>();

            foreach (var (x, y) in Candidates(a, b, u => u.Sig1)) pairs.Add((x, y));
            foreach (var (x, y) in Candidates(b, a, u => u.Sig1)) pairs.Add((y, x));
            if (kind == UnitKind.Script)
            {
                foreach (var (x, y) in Candidates(a, b, u => u.Sig2)) pairs.Add((x, y));
                foreach (var (x, y) in Candidates(b, a, u => u.Sig2)) pairs.Add((y, x));
            }

            // same path or same content, whatever the shingles say
            var byKey = b.Select((u, i) => (u, i)).ToLookup(t => t.u.Name);
            var byHash = b.Select((u, i) => (u, i)).ToLookup(t => t.u.Hash);
            for (int i = 0; i < a.Count; i++)
            {
                foreach (var (u, j) in byKey[a[i].Name]) if (u.SamePath(a[i])) pairs.Add((i, j));
                foreach (var (_, j) in byHash[a[i].Hash]) pairs.Add((i, j));
            }

            foreach (var (i, j) in pairs)
                if (Evaluate(a[i], b[j], baseline) is { } row && seen.Add((a[i], b[j]))) rows.Add(row);
        }

        AssetMatches(original, suspect, rows);
        rows.Sort((x, y) => y.Strength.CompareTo(x.Strength));
        return rows;
    }

    /// <summary>Tiny scripts and files that are nothing but the game's own code say nothing either way.</summary>
    private static bool Usable(InspectUnit u) => u.Kind switch
    {
        UnitKind.Script => !u.IsGameCode && u.Script is { TokenCount: >= 12 },
        _ => u.Lines is { Lines.Length: > 0 },
    };

    /// <summary>For each unit of <paramref name="a"/>, the units of <paramref name="b"/> that share the most with it.</summary>
    private static IEnumerable<(int A, int B)> Candidates(List<InspectUnit> a, List<InspectUnit> b, Func<InspectUnit, ulong[]> sig)
    {
        var index = new Dictionary<ulong, List<int>>();
        for (int j = 0; j < b.Count; j++)
            foreach (ulong s in sig(b[j]))
            {
                if (!index.TryGetValue(s, out var list)) index[s] = list = [];
                list.Add(j);
            }

        var counts = new Dictionary<int, int>();
        for (int i = 0; i < a.Count; i++)
        {
            counts.Clear();
            foreach (ulong s in sig(a[i]))
                if (index.TryGetValue(s, out var list) && list.Count <= HotShingle)
                    foreach (int j in list) counts[j] = counts.GetValueOrDefault(j) + 1;
            foreach (var (j, _) in counts.OrderByDescending(kv => kv.Value).Take(2))
                yield return (i, j);
        }
    }

    private static MatchRow? Evaluate(InspectUnit a, InspectUnit b, GameBaseline? baseline)
    {
        bool samePath = a.SamePath(b);
        if (a.Kind == UnitKind.Script) return EvaluateScripts(a, b, samePath, baseline);

        int shared = Hashing.Intersect(a.Sig1, b.Sig1);
        double ca = a.Sig1.Length == 0 ? 0 : (double)shared / a.Sig1.Length;
        double cb = b.Sig1.Length == 0 ? 0 : (double)shared / b.Sig1.Length;
        bool identical = a.Hash == b.Hash || (a.Lines!.CanonicalHash == b.Lines!.CanonicalHash && a.Lines.CanonicalHash != 0);
        double top = Math.Max(ca, cb);
        var level = identical ? MatchLevel.Identical : LevelOf(top);
        if (level == MatchLevel.None) return null;
        if (!identical && shared < 3) return null;

        string note = identical ? (a.Hash == b.Hash ? L.T("InspectorEngine.ByteByteSame") : L.T("InspectorEngine.SameContentLaidOut")) : L.T("InspectorEngine.LinesElementsFound", shared, a.Sig1.Length);
        int ids = a.Lines!.Ids.Intersect(b.Lines!.Ids, StringComparer.Ordinal).Count();
        if (ids > 0) note += $" · {L.T("Count.SharedMenuKey", ids)}";
        return new MatchRow
        {
            Kind = a.Kind, Original = a, Suspect = b, Level = level, SamePath = samePath,
            OriginalInSuspect = identical ? 1 : ca, SuspectInOriginal = identical ? 1 : cb, Note = note,
        };
    }

    private static MatchRow? EvaluateScripts(InspectUnit a, InspectUnit b, bool samePath, GameBaseline? baseline)
    {
        var sa = a.Script!;
        var sb = b.Script!;
        int raw = Hashing.Intersect(a.Sig1, b.Sig1);
        int shape = Hashing.Intersect(a.Sig2, b.Sig2);
        double r1 = a.Sig1.Length == 0 ? 0 : (double)raw / a.Sig1.Length, r2 = b.Sig1.Length == 0 ? 0 : (double)raw / b.Sig1.Length;
        double s1 = a.Sig2.Length == 0 ? 0 : (double)shape / a.Sig2.Length, s2 = b.Sig2.Length == 0 ? 0 : (double)shape / b.Sig2.Length;
        double ca = Math.Max(r1, s1 * ShapeWeight), cb = Math.Max(r2, s2 * ShapeWeight);
        double top = Math.Max(ca, cb);

        MatchLevel level;
        string kind;
        if (a.Hash == b.Hash) { level = MatchLevel.Identical; kind = L.T("InspectorEngine.ByteByteSame"); }
        else if (sa.RawHash == sb.RawHash) { level = MatchLevel.Identical; kind = L.T("InspectorEngine.SameCodeCommentsLayout"); }
        else if (sa.AbstractHash == sb.AbstractHash) { level = MatchLevel.Renamed; kind = L.T("InspectorEngine.SameCodeNamesValues"); }
        else
        {
            level = LevelOf(top);
            kind = "";
            if (level >= MatchLevel.Large && Math.Max(s1, s2) - Math.Max(r1, r2) >= 0.25)
            {
                level = MatchLevel.Renamed;
                kind = L.T("InspectorEngine.SameStructureNamesValues");
            }
        }
        if (level == MatchLevel.None) return null;

        // functions: copied as written, and copied with other names
        var bRaw = sb.Functions.Select(f => f.RawHash).ToHashSet();
        var bShape = sb.Functions.Select(f => f.AbstractHash).ToHashSet();
        var mine = sa.Functions.Where(f => baseline is null || !baseline.FunctionBodies.Contains(f.RawHash)).ToList();
        int verbatim = mine.Count(f => bRaw.Contains(f.RawHash));
        int renamed = mine.Count(f => !bRaw.Contains(f.RawHash) && bShape.Contains(f.AbstractHash));

        var parts = new List<string>();
        if (kind.Length > 0) parts.Add(kind);
        if (mine.Count > 0 && verbatim + renamed > 0)
            parts.Add(L.T("InspectorEngine.Reused", verbatim + renamed, L.T("Count.Function", mine.Count)) + (renamed > 0 ? L.T("InspectorEngine.ReusedRenamed", renamed) : ""));
        if (parts.Count == 0) parts.Add(L.T("InspectorEngine.CodeShared", Math.Round(top * 100)));

        return new MatchRow
        {
            Kind = UnitKind.Script, Original = a, Suspect = b, Level = level, SamePath = samePath,
            OriginalInSuspect = level == MatchLevel.Identical ? 1 : ca, SuspectInOriginal = level == MatchLevel.Identical ? 1 : cb,
            Note = string.Join(" · ", parts),
        };
    }

    private static MatchLevel LevelOf(double containment) => containment switch
    {
        >= 0.8 => MatchLevel.NearCopy,
        >= 0.5 => MatchLevel.Large,
        >= 0.2 => MatchLevel.Partial,
        _ => MatchLevel.None,
    };

    private static void AssetMatches(SourceScan original, SourceScan suspect, List<MatchRow> rows)
    {
        var byHash = suspect.Units.Where(IsAsset).ToLookup(u => u.Hash);
        foreach (var a in original.Units.Where(IsAsset))
        {
            var hits = byHash[a.Hash].ToList();
            if (hits.Count == 0) continue;
            var b = hits.FirstOrDefault(h => h.SamePath(a)) ?? hits[0];
            bool samePath = b.SamePath(a);
            rows.Add(new MatchRow
            {
                Kind = UnitKind.Asset, Original = a, Suspect = b, Level = MatchLevel.Identical, SamePath = samePath,
                OriginalInSuspect = 1, SuspectInOriginal = 1,
                Note = samePath ? L.T("InspectorEngine.ByteByteSameSame") : L.T("InspectorEngine.ByteByteSameUnder"),
            });
        }
    }

    private static bool IsAsset(InspectUnit u) => u.Kind == UnitKind.Asset && u.Size > 0 && !u.Hash.StartsWith("unreadable:", StringComparison.Ordinal);

    // ---------------------------------------------------------------- scoring

    private static CategoryResult Score(UnitKind kind, string name, double weight, SourceScan original, SourceScan suspect, List<MatchRow> matches)
    {
        var a = original.Units.Where(u => InCategory(u, kind)).ToList();
        var b = suspect.Units.Where(u => InCategory(u, kind)).ToList();
        int shared = matches.Where(m => m.Kind == kind && m.Level >= MatchLevel.Large).Select(m => m.Original).Distinct().Count();

        double? ab, ba;
        string detail;
        if (kind == UnitKind.Asset)
        {
            var hashA = a.Select(u => u.Hash).ToHashSet();
            var hashB = b.Select(u => u.Hash).ToHashSet();
            ab = a.Count == 0 ? null : (double)a.Count(u => hashB.Contains(u.Hash)) / a.Count;
            ba = b.Count == 0 ? null : (double)b.Count(u => hashA.Contains(u.Hash)) / b.Count;
            detail = L.T("InspectorEngine.FilesByteByteSame", a.Count(u => hashB.Contains(u.Hash)), a.Count);
            shared = a.Count(u => hashB.Contains(u.Hash));
        }
        else
        {
            var (rawAB, shapeAB) = Coverage(a, b);
            var (rawBA, shapeBA) = Coverage(b, a);
            ab = a.Count == 0 ? null : kind == UnitKind.Script ? Math.Max(rawAB, shapeAB * ShapeWeight) : rawAB;
            ba = b.Count == 0 ? null : kind == UnitKind.Script ? Math.Max(rawBA, shapeBA * ShapeWeight) : rawBA;
            detail = kind == UnitKind.Script
                ? L.T("InspectorEngine.WrittenIgnoringNamesValues", rawAB, shapeAB)
                : L.T("InspectorEngine.OriginalsLinesSuspect", rawAB);
        }
        return new CategoryResult
        {
            Kind = kind, Name = name, Weight = weight, OriginalFiles = a.Count, SuspectFiles = b.Count,
            OriginalInSuspect = ab, SuspectInOriginal = ba, SharedFiles = shared, Detail = detail,
        };
    }

    private static bool InCategory(InspectUnit u, UnitKind kind) =>
        u.Kind == kind && (kind == UnitKind.Asset ? IsAsset(u) : Usable(u));

    /// <summary>Share of the units' own content found anywhere in the other side, as written and as a shape.</summary>
    private static (double Raw, double Shape) Coverage(List<InspectUnit> from, List<InspectUnit> into)
    {
        var global1 = Hashing.Distinct([.. into.SelectMany(u => u.Sig1)]);
        var global2 = Hashing.Distinct([.. into.SelectMany(u => u.Sig2)]);
        long total1 = 0, found1 = 0, total2 = 0, found2 = 0;
        foreach (var u in from)
        {
            total1 += u.Sig1.Length;
            found1 += u.Sig1.Count(s => Array.BinarySearch(global1, s) >= 0);
            total2 += u.Sig2.Length;
            found2 += u.Sig2.Count(s => Array.BinarySearch(global2, s) >= 0);
        }
        return (total1 == 0 ? 0 : (double)found1 / total1, total2 == 0 ? 0 : (double)found2 / total2);
    }

    // ---------------------------------------------------------------- evidence

    private static List<EvidenceItem> CollectEvidence(SourceScan original, SourceScan suspect, GameBaseline? baseline, CancellationToken token)
    {
        var result = new List<EvidenceItem>();

        void Shared(string category, Func<InspectUnit, IEnumerable<string>> pick, HashSet<string>? ignore, int minLength, EvidenceKind kind = EvidenceKind.Other)
        {
            token.ThrowIfCancellationRequested();
            var a = Index(original, pick);
            var b = Index(suspect, pick);
            foreach (var key in a.Keys.Where(b.ContainsKey).Where(k => k.Length >= minLength && ignore?.Contains(k) != true)
                         .OrderByDescending(k => k.Length).Take(MaxEvidencePerKind))
                result.Add(new EvidenceItem(category, key, Where(a[key]), Where(b[key])) { Kind = kind });
        }

        Shared(L.T("InspectorEngine.SharedComment"), u => u.Script?.Comments ?? u.Lines?.Comments ?? [], baseline?.Comments, 24, EvidenceKind.SharedComment);
        Shared(L.T("InspectorEngine.SharedTextCode"), u => u.Script?.Strings ?? [], baseline?.Strings, 10);
        Shared(L.T("InspectorEngine.SharedClass"), u => u.Script?.Classes ?? [], baseline?.Classes, 3);
        Shared(L.T("InspectorEngine.SharedFunction"), u => u.Script?.Functions.Select(f => f.Title) ?? [], baseline?.FunctionNames, 6);
        Shared(L.T("InspectorEngine.SharedMenuKey"), u => u.Lines?.Ids ?? [], null, 6);

        // functions copied whole, with and without their names changed
        var shapes = new Dictionary<ulong, (FunctionInfo F, InspectUnit U)>();
        var raws = new Dictionary<ulong, (FunctionInfo F, InspectUnit U)>();
        foreach (var u in suspect.Units.Where(u => u.Script is not null && !u.IsGameCode))
            foreach (var f in u.Script!.Functions.Where(f => f.Tokens >= 20))
            {
                raws.TryAdd(f.RawHash, (f, u));
                shapes.TryAdd(f.AbstractHash, (f, u));
            }

        int copied = 0, renamed = 0;
        foreach (var u in original.Units.Where(u => u.Script is not null && !u.IsGameCode))
            foreach (var f in u.Script!.Functions.Where(f => f.Tokens >= 20))
            {
                token.ThrowIfCancellationRequested();
                if (baseline?.FunctionBodies.Contains(f.RawHash) == true || baseline?.FunctionShapes.Contains(f.AbstractHash) == true) continue;
                if (raws.TryGetValue(f.RawHash, out var same))
                {
                    if (copied++ < MaxEvidencePerKind)
                        result.Add(new EvidenceItem(L.T("InspectorEngine.CopiedFunction"), f.Title == same.F.Title ? f.Title : $"{f.Title}  →  {same.F.Title}",
                            L.T("InspectorEngine.Tokens", u.Origin, f.Tokens), same.U.Origin) { Kind = EvidenceKind.CopiedFunction });
                }
                else if (shapes.TryGetValue(f.AbstractHash, out var like))
                {
                    if (renamed++ < MaxEvidencePerKind)
                        result.Add(new EvidenceItem(L.T("InspectorEngine.RenamedFunction"), $"{f.Title}  →  {like.F.Title}",
                            L.T("InspectorEngine.Tokens", u.Origin, f.Tokens), like.U.Origin) { Kind = EvidenceKind.RenamedFunction });
                }
            }

        return result;
    }

    private static Dictionary<string, List<InspectUnit>> Index(SourceScan scan, Func<InspectUnit, IEnumerable<string>> pick)
    {
        var map = new Dictionary<string, List<InspectUnit>>(StringComparer.Ordinal);
        foreach (var u in scan.Units)
            foreach (string item in pick(u).Distinct())
            {
                if (!map.TryGetValue(item, out var list)) map[item] = list = [];
                list.Add(u);
            }
        return map;
    }

    private static string Where(List<InspectUnit> units) =>
        units.Count == 1 ? units[0].Origin : L.T("InspectorEngine.More", units[0].Origin, units.Count - 1);

    // ---------------------------------------------------------------- findings

    private static List<string> Summarise(InspectionReport r)
    {
        var f = new List<string>();
        foreach (var c in r.Categories.Where(c => c.OriginalFiles > 0 || c.SuspectFiles > 0))
        {
            if (c.OriginalInSuspect is >= 0.1 || c.SuspectInOriginal is >= 0.1)
                f.Add(L.T("InspectorEngine.OriginalSuspectSuspectOriginal", c.Name, Percent(c.OriginalInSuspect), Percent(c.SuspectInOriginal)));
        }

        int scripts = r.Matches.Where(m => m.Kind == UnitKind.Script && m.Level >= MatchLevel.Large).Select(m => m.Suspect).Distinct().Count();
        int scriptTotal = r.Categories.First(c => c.Kind == UnitKind.Script).SuspectFiles;
        if (scripts > 0)
            f.Add(L.T("InspectorEngine.ScriptsMostlyOriginal", scripts, L.T("Count.Script", scriptTotal)));

        int renamedFiles = r.Matches.Count(m => m.Kind == UnitKind.Script && m.Level == MatchLevel.Renamed);
        if (renamedFiles > 0)
            f.Add(L.T("InspectorEngine.ScriptsRewritten", L.T("Count.Script", renamedFiles), renamedFiles));

        int copiedFunctions = r.Evidence.Count(e => e.Kind == EvidenceKind.CopiedFunction);
        int renamedFunctions = r.Evidence.Count(e => e.Kind == EvidenceKind.RenamedFunction);
        string? copiedAsWritten = copiedFunctions > 0 ? L.T("InspectorEngine.CopiedWritten", L.T("Count.Function", copiedFunctions)) : null;
        string? copiedRenamed = renamedFunctions > 0 ? L.T("InspectorEngine.CopiedOtherNames", L.T("Count.Function", renamedFunctions)) : null;
        if (copiedAsWritten is not null && copiedRenamed is not null)
            f.Add(L.T("InspectorEngine.FunctionsCopiedBoth", copiedAsWritten, copiedRenamed));
        else if ((copiedAsWritten ?? copiedRenamed) is { } copiedOne)
            f.Add(L.T("InspectorEngine.FunctionsCopied", copiedOne));

        int assets = r.Matches.Count(m => m.Kind == UnitKind.Asset);
        if (assets > 0)
            f.Add(L.T("InspectorEngine.AssetsSame", assets));

        int menus = r.Matches.Count(m => m.Kind == UnitKind.Xml && m.Level >= MatchLevel.Large);
        if (menus > 0)
            f.Add(L.T("InspectorEngine.MenusSame", menus));

        int comments = r.Evidence.Count(e => e.Kind == EvidenceKind.SharedComment);
        if (comments > 0)
            f.Add(L.T("InspectorEngine.CommentsShared", comments));

        int samePaths = r.Matches.Count(m => m.SamePath && m.Level >= MatchLevel.Large);
        if (samePaths > 0)
            f.Add(L.T("InspectorEngine.SitVerySamePath", L.T("Count.MatchingFile", samePaths)));

        if (r.StyleSimilarity is >= 0.85)
            f.Add(L.T("InspectorEngine.CodingStyleAlikeAcross", r.StyleSimilarity, r.StyleNote));

        if (r.GameCodeIgnored && r.GameCodeFiles > 0)
            f.Add(L.T("InspectorEngine.OnlyGamesOwnCode", L.T("Count.Script", r.GameCodeFiles)));

        if ((r.Original.Blobs > 0) != (r.Suspect.Blobs > 0))
            f.Add(L.T("InspectorEngine.OneSourceCompiledDecompiled"));

        if (f.Count == 0 || r.Headline < 0.08)
            f.Add(L.T("InspectorEngine.NothingNoteSharedBeyond"));
        return f;
    }

    private static string Percent(double? v) => v is null ? L.T("ReportWriter.NotAvailable") : $"{v:P0}";
}
