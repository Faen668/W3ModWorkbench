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

    /// <summary>Extract a file's companions with it: a mesh's .buffer files, or the mesh a buffer belongs to.</summary>
    public bool ExtractCompanions { get; set; } = true;

    /// <summary>Write textures (from a texture cache, and .dds files) as PNG pictures instead of .dds.</summary>
    public bool ExtractTexturesAsPng { get; set; }

    /// <summary>Write CR2W resources (.env, .w2mi, .w2ent…) as JSON to edit, instead of as they are.</summary>
    public bool ExtractCr2wAsJson { get; set; }

    /// <summary>The CR2W and JSON window's output folder (empty for beside each file), and whether it looks in subfolders.</summary>
    public string Cr2wJsonOutput { get; set; } = "";
    public bool Cr2wJsonRecursive { get; set; } = true;

    /// <summary>Folders (ending in a backslash) and files bookmarked in the asset browser, by game path.</summary>
    public List<string> AssetBookmarks { get; set; } = [];

    /// <summary>Whether opening a file from the asset browser that more than one archive has asks which copy to open.</summary>
    public bool AskWhichCopy { get; set; } = true;

    /// <summary>Bundles, files and folders opened most recently, newest first.</summary>
    public List<string> RecentPaths { get; set; } = [];

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

    /// <summary>Whether the script editor lists names that fit as you type: after a dot, and at the start of a name.</summary>
    public bool SuggestNames { get; set; } = true;

    /// <summary>Show the outline side bar beside scripts.</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>The outline side bar's width.</summary>
    public double OutlineWidth { get; set; } = 270;

    /// <summary>REDkit's wcc_lite.exe, which compiles a script project into a .rsblob. Compiling is off while it's empty.</summary>
    public string WccLitePath { get; set; } = "";

    /// <summary>Compile with the built-in compiler even when wcc_lite is set. It needs only the game's scripts, and is quicker.</summary>
    public bool PreferBuiltInCompiler { get; set; }

    /// <summary>Where the game strings search reads from: "Game" (the .w3strings files), "REDkit" (its strings database) or "File" (<see cref="StringsDatabasePath"/>).</summary>
    public string GameStringsSource { get; set; } = "Game";

    /// <summary>A REDkit strings database picked by hand for the game strings search, such as a copy or a mod project's.</summary>
    public string StringsDatabasePath { get; set; } = "";

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

    /// <summary>Where each scripts folder's compiled blob was last saved, from before projects had a .w3bench file. A new project picks its entry up.</summary>
    public Dictionary<string, string> CompileOutputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the main window lists files: Details, or Small, Medium or Large thumbnails.</summary>
    public string FileView { get; set; } = "Details";

    /// <summary>Show the preview pane beside the file list, and how wide it is.</summary>
    public bool ShowFilePreview { get; set; } = true;
    public double FilePreviewWidth { get; set; } = 380;

    /// <summary>Whether the mesh viewer draws a floor grid under the mesh, and shows what it's drawing over the view.</summary>
    public bool ShowMeshFloor { get; set; } = true;
    public bool ShowMeshInfo { get; set; } = true;

    /// <summary>Whether the entity viewer shows what it's drawing (triangles, frame rate…) over the view.</summary>
    public bool ShowEntityInfo { get; set; } = true;

    /// <summary>Whether the entity viewer draws a floor grid under the entity.</summary>
    public bool ShowEntityFloor { get; set; } = true;

    /// <summary>How many times a second the entity viewer poses an animation as it plays; 0 for as often as the screen draws.</summary>
    public int AnimationFps { get; set; } = 30;

    /// <summary>The project files opened most recently, newest first.</summary>
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>The project that was open when the tool closed, opened again at the next start. Empty when none was.</summary>
    public string LastProject { get; set; } = "";

    /// <summary>Open the project that was open last time when the tool starts.</summary>
    public bool ReopenLastProject { get; set; } = true;

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
