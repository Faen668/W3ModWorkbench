using System.IO;
using System.Text.Json;

namespace W3ModWorkbench;

public sealed class Settings
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "settings.json");

    /// <summary>Where settings were kept while the tool was called W3 Bundle Explorer.</summary>
    private static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3BundleExplorer", "settings.json");

    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>The colour theme on top of the light or dark mode: Windows (its own accent), or one of <see cref="Themes.Presets"/>.</summary>
    public string ThemePreset { get; set; } = "Windows";

    public string OutputFolder { get; set; } = "";
    public string LastOpenFolder { get; set; } = "";
    public bool KeepStructure { get; set; } = true;
    public bool OpenWhenDone { get; set; } = true;

    /// <summary>The Witcher 3 install whose script sources name enum values in decompiled scripts.</summary>
    public string GameFolder { get; set; } = "";

    /// <summary>The source inspector's two folders, and whether it sets the game's own scripts aside.</summary>
    public string InspectorOriginal { get; set; } = "";
    public string InspectorSuspect { get; set; } = "";
    public bool InspectorIgnoreGame { get; set; } = true;

    /// <summary>The load order analyser's mods folder, and whether it counts the DLC folder.</summary>
    public string LoadOrderModsFolder { get; set; } = "";
    public bool LoadOrderIncludeDlc { get; set; } = true;

    /// <summary>Tabs: files open as tabs in one preview window. Windows: each file gets a window of its own.</summary>
    public string PreviewOpenIn { get; set; } = "Tabs";

    /// <summary>Right or Down: where the second group of tabs goes when a preview is split.</summary>
    public string PreviewSplit { get; set; } = "Right";

    /// <summary>Show a declaration's code in a popup when the mouse rests on a name in a script.</summary>
    public bool PeekDefinitions { get; set; } = true;

    /// <summary>Show the outline side bar beside scripts.</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>The outline side bar's width.</summary>
    public double OutlineWidth { get; set; } = 270;

    /// <summary>REDkit's wcc_lite.exe, which compiles a script project into a .rsblob. Compiling is off while it's empty.</summary>
    public string WccLitePath { get; set; } = "";

    /// <summary>Script Merger Remastered's exe, opened from the load order analyser to merge clashing scripts and bundle files. Empty when not set.</summary>
    public string ScriptMergerPath { get; set; } = "";

    /// <summary>The Find in files window's last search: the text, the file filters, where it looked, and its options.</summary>
    public string FindText { get; set; } = "";
    public string FindFilters { get; set; } = "*.ws";
    public string FindScope { get; set; } = "";
    public string FindFolder { get; set; } = "";
    public bool FindMatchCase { get; set; }
    public bool FindWholeWord { get; set; }
    public bool FindRegex { get; set; }
    public bool FindSubfolders { get; set; } = true;

    /// <summary>Where each script project's compiled blob was last saved, keyed by the project's scripts folder.</summary>
    public Dictionary<string, string> CompileOutputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    readonly static JsonSerializerOptions? _sOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            // the first start after the rename carries the old settings over
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(OldFilePath, FilePath);
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), _sOptions) ?? new Settings();
        }
        catch (Exception)
        {
            // a broken settings file just means defaults
        }
        return new Settings();
    }


    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _sOptions));
        }
        catch (Exception)
        {
            // not worth interrupting the user over
        }
    }
}
