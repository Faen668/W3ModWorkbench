using W3ModWorkbench.Localisation;
namespace W3ModWorkbench.Core.LoadOrder;

public enum Severity { Error, Warning, Info }

/// <summary>One thing the analysis found: a conflict, a breakage, a dependency problem or just something worth knowing.</summary>
/// <param name="Category">The group it belongs to, such as "Script conflict" or "Dependency".</param>
/// <param name="Mods">The mods involved, highest priority first.</param>
/// <param name="Items">The evidence: files, functions, IDs. Can be long.</param>
/// <param name="Advice">What to do about it.</param>
/// <param name="Winner">The mod whose version the game ends up using, when the clash has a winner.</param>
public sealed record Finding(
    Severity Severity, string Category, string Title, string Summary,
    IReadOnlyList<string> Mods, IReadOnlyList<string> Items, string Advice, string? Winner = null)
{
    public string SeverityText => L.T(Severity switch { Severity.Error => "LoadOrderTypes.Problem", Severity.Warning => "LoadOrderTypes.Warning", _ => "LoadOrderTypes.Note" });
}

/// <summary>An installed mod or DLC folder, in the order the game loads it.</summary>
public sealed class ModEntry
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required bool IsDlc { get; init; }

    /// <summary>Installed by the in-game mod.io hub, outside the game folder. The game loads these before everything else.</summary>
    public bool IsModio { get; init; }

    /// <summary>Not installed: a mod being checked against the installed ones before it's installed.</summary>
    public bool IsCandidate { get; init; }

    /// <summary>DLC and mod.io mods have a fixed place, so mods.settings priorities don't apply to them.</summary>
    public bool HasFixedPlace => IsDlc || IsModio;

    /// <summary>The mod's own name from its info.json, when it has one.</summary>
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>The priority mods.settings gives it. Null when the file doesn't list the mod.</summary>
    public int? Priority { get; set; }

    /// <summary>Position in the load order, 1 first. The mod at 1 has the highest priority.</summary>
    public int Order { get; set; }

    public List<string> DeclaredDependencies { get; } = [];
    public List<string> DependsOn { get; } = [];
    public List<string> UsedBy { get; } = [];

    public int CompiledScripts { get; set; }
    public int LooseScripts { get; set; }
    public int Bundles { get; set; }
    public int BundleFiles { get; set; }
    public int StringsFiles { get; set; }
    public int Annotations { get; set; }

    public int Problems { get; set; }
    public int Warnings { get; set; }
    public int Notes { get; set; }

    public string Contents
    {
        get
        {
            var parts = new List<string>();
            if (CompiledScripts > 0) parts.Add(CompiledScripts == 1 ? L.T("LoadOrderTypes.CompiledScripts") : L.T("LoadOrderTypes.CompiledScriptBlobs", CompiledScripts));
            if (LooseScripts > 0) parts.Add(L.T("Count.LooseScript", LooseScripts));
            if (Bundles > 0) parts.Add(L.T("Count.BundledFile", BundleFiles));
            if (StringsFiles > 0) parts.Add(L.T("LoadOrderTypes.Strings"));
            return parts.Count == 0 ? L.T("LoadOrderTypes.NoScriptsBundles") : string.Join(", ", parts);
        }
    }
}

public sealed class LoadOrderReport
{
    public required string ModsFolder { get; init; }
    public string? SettingsFile { get; init; }
    public string? GameScriptsFolder { get; init; }
    public List<ModEntry> Mods { get; } = [];
    public List<Finding> Findings { get; } = [];
    public List<string> Warnings { get; } = [];
    public TimeSpan Elapsed { get; set; }
    public int VanillaTypes { get; set; }

    public int Count(Severity s) => Findings.Count(f => f.Severity == s);
}

/// <param name="Candidates">Mod folders to check before they're installed, each a mods\modName or dlc\dlcName folder.</param>
/// <param name="CandidateMenus">The menu XML files those mods put in bin\config\r4game\user_config_matrix\pc.</param>
/// <param name="ScriptMergerPath">Script Merger Remastered, whose list of merged files says which XML a merged mod covers.</param>
public sealed record LoadOrderOptions(string ModsFolder, bool IncludeDlc, string? SettingsFile, string? GameFolder,
    IReadOnlyList<string>? Candidates = null, IReadOnlyList<string>? CandidateMenus = null, string? ScriptMergerPath = null);
