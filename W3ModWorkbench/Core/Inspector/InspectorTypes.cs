using W3ModWorkbench.Localisation;
namespace W3ModWorkbench.Core.Inspector;

/// <summary>What a file is, which decides how it's compared.</summary>
public enum UnitKind
{
    /// <summary>WitcherScript: a loose .ws, or one decompiled from a .rsblob.</summary>
    Script,

    /// <summary>XML, such as a mod menu: compared element by element.</summary>
    Xml,

    /// <summary>Other text, such as a strings CSV, an ini or a readme: compared line by line.</summary>
    Text,

    /// <summary>Anything else, including everything binary inside a bundle: compared by its SHA-256.</summary>
    Asset,
}

/// <summary>One function body found in a script, for spotting copied functions.</summary>
public sealed record FunctionInfo(string Owner, string Name, ulong RawHash, ulong AbstractHash, int Tokens)
{
    public string Title => Owner.Length > 0 ? $"{Owner}::{Name}" : Name;
}

/// <summary>What was read out of a script's code.</summary>
public sealed class ScriptInfo
{
    public int TokenCount { get; init; }

    /// <summary>Hash of every token in order, ignoring comments, layout and letter case.</summary>
    public ulong RawHash { get; init; }

    /// <summary>The same with names and values replaced by placeholders: the shape of the code.</summary>
    public ulong AbstractHash { get; init; }

    /// <summary>Sorted, distinct hashes of every run of tokens (a "shingle"), as written.</summary>
    public ulong[] RawShingles { get; init; } = [];

    /// <summary>The same for the shape of the code, so renaming variables doesn't hide a copy.</summary>
    public ulong[] AbstractShingles { get; init; } = [];

    public IReadOnlyList<string> Comments { get; init; } = [];
    public IReadOnlyList<string> Strings { get; init; } = [];
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<FunctionInfo> Functions { get; init; } = [];
}

/// <summary>What was read out of an XML or text file: a set of hashed lines.</summary>
public sealed class LineSetInfo
{
    /// <summary>Hash of the whole file as canonical lines in order, so layout and attribute order don't matter.</summary>
    public ulong CanonicalHash { get; init; }

    /// <summary>Sorted, distinct hashes of the file's lines (an XML element with its path, attributes and text).</summary>
    public ulong[] Lines { get; init; } = [];

    /// <summary>The names an XML file defines: its id, displayName and name values.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    public IReadOnlyList<string> Comments { get; init; } = [];
}

/// <summary>A file, or a file inside a bundle or compiled script blob, read and ready to compare.</summary>
public sealed class InspectUnit
{
    public required UnitKind Kind { get; init; }

    /// <summary>What to show: the path, or "bundle ▸ path inside it".</summary>
    public required string Origin { get; init; }

    /// <summary>Where it sits, lower-case with backslashes: relative to the source, or the path inside its bundle.</summary>
    public required string Key { get; init; }

    public required long Size { get; init; }

    /// <summary>SHA-256 of its bytes, in hex.</summary>
    public required string Hash { get; init; }

    public DateTime? Modified { get; init; }

    /// <summary>The text of a script, XML or text file, to show side by side.</summary>
    public string? Text { get; init; }

    public ScriptInfo? Script { get; set; }
    public LineSetInfo? Lines { get; set; }

    /// <summary>The unit's own shingles or lines, less what the game's own code already has.</summary>
    public ulong[] Sig1 { get; set; } = [];

    /// <summary>Script shapes, less what the game's own code already has.</summary>
    public ulong[] Sig2 { get; set; } = [];

    /// <summary>True when the file has code but every bit of it is the game's own.</summary>
    public bool IsGameCode { get; set; }

    public string Name => Key[(Key.LastIndexOf('\\') + 1)..];

    /// <summary>Whether this and <paramref name="other"/> sit at the same path, or one's path ends with the other's.</summary>
    public bool SamePath(InspectUnit other) =>
        Key == other.Key
        || Key.EndsWith('\\' + other.Key, StringComparison.Ordinal)
        || other.Key.EndsWith('\\' + Key, StringComparison.Ordinal);
}

/// <summary>Everything read from one source folder.</summary>
public sealed class SourceScan
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public List<InspectUnit> Units { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public int Files { get; set; }
    public int Bundles { get; set; }
    public int Blobs { get; set; }
    public long Bytes { get; set; }
    public DateTime? Oldest { get; set; }
    public DateTime? Newest { get; set; }

    public int Count(UnitKind kind) => Units.Count(u => u.Kind == kind);

    /// <summary>"48 scripts · 3 bundles · 6 XML files", for under the path.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(int n, string countKey)
            {
                if (n > 0) parts.Add(L.T(countKey, n));
            }
            Add(Count(UnitKind.Script), "Count.Script");
            Add(Blobs, "Count.CompiledScriptFile");
            Add(Bundles, "Count.Bundle");
            Add(Count(UnitKind.Xml), "Count.XmlFile");
            Add(Count(UnitKind.Text), "Count.TextFile");
            Add(Count(UnitKind.Asset), "Count.OtherFile");
            return parts.Count == 0 ? L.T("InspectorTypes.NothingCompareFound") : string.Join(" · ", parts) + $" · {Format.Bytes(Bytes)}";
        }
    }
}

public enum MatchLevel
{
    /// <summary>Not worth flagging.</summary>
    None,

    /// <summary>Small overlap: could be coincidence or shared patterns.</summary>
    Partial,

    /// <summary>A large part of one is in the other.</summary>
    Large,

    /// <summary>Nearly all of one is in the other.</summary>
    NearCopy,

    /// <summary>The same code with other names or values.</summary>
    Renamed,

    /// <summary>The same content: reformatted or recommented, or byte for byte.</summary>
    Identical,
}

/// <summary>A file in one source and the file in the other it matches.</summary>
public sealed class MatchRow
{
    public required UnitKind Kind { get; init; }
    public required InspectUnit Original { get; init; }
    public required InspectUnit Suspect { get; init; }
    public required MatchLevel Level { get; init; }

    /// <summary>How much of the original file is in the suspect one, 0 to 1.</summary>
    public required double OriginalInSuspect { get; init; }

    /// <summary>How much of the suspect file is in the original one, 0 to 1.</summary>
    public required double SuspectInOriginal { get; init; }

    public bool SamePath { get; init; }
    public string Note { get; init; } = "";

    public string KindText => Kind switch
    {
        UnitKind.Script => L.T("InspectorTypes.Kind.Script"),
        UnitKind.Xml => L.T("InspectorTypes.Kind.Xml"),
        UnitKind.Text => L.T("InspectorTypes.Kind.Text"),
        _ => L.T("InspectorTypes.Kind.Asset"),
    };

    public string LevelText => Level switch
    {
        MatchLevel.Identical => L.T("InspectorTypes.Identical"),
        MatchLevel.Renamed => L.T("InspectorTypes.RenamedCopy"),
        MatchLevel.NearCopy => L.T("InspectorTypes.NearCopy"),
        MatchLevel.Large => L.T("InspectorTypes.LargeOverlap"),
        MatchLevel.Partial => L.T("InspectorTypes.PartialOverlap"),
        _ => "",
    };

    /// <summary>Used to sort the strongest matches first.</summary>
    public double Strength => (int)Level * 10 + Math.Max(OriginalInSuspect, SuspectInOriginal);
}

/// <summary>One thing the two sources share that isn't a whole file: a comment, a class, a menu key.</summary>
public sealed record EvidenceItem(string Category, string Text, string InOriginal, string InSuspect)
{
    /// <summary>What kind of evidence it is, for counting; <see cref="Category"/> is its name to show.</summary>
    public EvidenceKind Kind { get; init; }
}

public enum EvidenceKind { Other, SharedComment, CopiedFunction, RenamedFunction }

/// <summary>How the two sources compare in one kind of content.</summary>
public sealed class CategoryResult
{
    public UnitKind Kind { get; init; }
    public required string Name { get; init; }
    public required int OriginalFiles { get; init; }
    public required int SuspectFiles { get; init; }

    /// <summary>0 to 1, or null when the original has none of this to be copied.</summary>
    public double? OriginalInSuspect { get; init; }

    /// <summary>0 to 1, or null when the suspect has none of this.</summary>
    public double? SuspectInOriginal { get; init; }

    public int SharedFiles { get; init; }
    public double Weight { get; init; }
    public string Detail { get; init; } = "";
}

public enum VerdictLevel { None, Minor, Some, Significant, Extensive }

public sealed class InspectionReport
{
    public required SourceScan Original { get; init; }
    public required SourceScan Suspect { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    public bool GameCodeIgnored { get; init; }
    public int GameScriptsRead { get; init; }
    public int GameCodeFiles { get; init; }
    public List<CategoryResult> Categories { get; init; } = [];
    public List<MatchRow> Matches { get; init; } = [];
    public List<EvidenceItem> Evidence { get; init; } = [];
    public List<string> Findings { get; init; } = [];

    /// <summary>The coding habits of each source's scripts side by side; empty when a source has too little code.</summary>
    public List<StyleFeature> Style { get; init; } = [];

    /// <summary>How alike the two coders' habits are, 0 to 1, or null when there was too little code to tell.</summary>
    public double? StyleSimilarity { get; init; }

    public string StyleNote { get; init; } = "";

    /// <summary>How much of the original is in the suspect, 0 to 1.</summary>
    public double OriginalInSuspect { get; init; }

    /// <summary>How much of the suspect is in the original, 0 to 1.</summary>
    public double SuspectInOriginal { get; init; }

    public double Headline => Math.Max(OriginalInSuspect, SuspectInOriginal);

    public VerdictLevel Verdict => Headline switch
    {
        >= 0.75 => VerdictLevel.Extensive,
        >= 0.5 => VerdictLevel.Significant,
        >= 0.25 => VerdictLevel.Some,
        >= 0.08 => VerdictLevel.Minor,
        _ => VerdictLevel.None,
    };

    public string VerdictTitle => Verdict switch
    {
        VerdictLevel.Extensive => L.T("InspectorTypes.ExtensiveCopying"),
        VerdictLevel.Significant => L.T("InspectorTypes.SignificantOverlap"),
        VerdictLevel.Some => L.T("InspectorTypes.SomeOverlap"),
        VerdictLevel.Minor => L.T("InspectorTypes.MinorOverlap"),
        _ => L.T("InspectorTypes.NoMeaningfulSimilarity"),
    };

    public string VerdictText => Verdict switch
    {
        VerdictLevel.Extensive => L.T("InspectorTypes.MostOneSourceReproduced"),
        VerdictLevel.Significant => L.T("InspectorTypes.LargeShareOneSource"),
        VerdictLevel.Some => L.T("InspectorTypes.PartsSharedCanCommon"),
        VerdictLevel.Minor => L.T("InspectorTypes.LittleSharedWhichNormal"),
        _ => L.T("InspectorTypes.TwoSourcesHaveAlmost"),
    };
}

/// <summary>What the inspector is doing, for the progress card.</summary>
public sealed record InspectProgress(string Phase, double Fraction, string Detail);

public sealed record InspectOptions(bool IgnoreGameCode, string? GameFolder);
