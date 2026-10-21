using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Script Bench's find in files: what to look for (match case, whole words, a regular expression), where (your mod,
/// the game's scripts, the installed mods with compiled ones decompiled, REDkit's scripts or any folder) and in which
/// files. Nothing needs to be open first, and a script changed but not saved is searched as it is in its tab.
/// </summary>
public partial class PreviewWindow
{
    private const string Everywhere = "Everywhere", MineScope = "Your mod", GameScope = "Game scripts",
        ModsScope = "Installed mods", RedkitScope = "REDkit scripts", ChooseFolder = "A folder…";

    /// <summary>The folder picked with A folder…, offered in the list from then on.</summary>
    private string? _findFolder;

    /// <summary>The place last picked, to go back to when A folder… is cancelled.</summary>
    private string? _findScope;

    private bool _benchFindReady;

    /// <summary>Opens Script Bench at its find box: Find in files in the main window.</summary>
    public static void FindInBench(MainWindow main)
    {
        if (BenchFor(main) is not { } bench) return;
        bench.BringForward();
        bench.FindInFiles();
    }

    /// <summary>The options last used, read once, and kept again when the bench closes.</summary>
    private void SetUpBenchFind()
    {
        if (_benchFindReady) return;
        _benchFindReady = true;
        var settings = App.Settings;
        Results.MatchCase = settings.FindMatchCase;
        Results.WholeWord = settings.FindWholeWord;
        Results.UseRegex = settings.FindRegex;
        // the old window's default; here every file is a script already
        Results.FileFilter = settings.FindFilters.Trim() is "*.ws" or "*.*" ? "" : settings.FindFilters;
        _findFolder = Directory.Exists(settings.FindFolder) ? settings.FindFolder : null;
        _findScope = settings.FindScope;
        Results.ScopePicked += BenchScopePicked;
        Closed += (_, _) =>
        {
            settings.FindText = Results.FindText;
            settings.FindFilters = Results.FileFilter;
            settings.FindMatchCase = Results.MatchCase;
            settings.FindWholeWord = Results.WholeWord;
            settings.FindRegex = Results.UseRegex;
            settings.FindScope = _findScope ?? "";
            settings.FindFolder = _findFolder ?? "";
        };
    }

    /// <summary>The places to look, with the folder picked before, and the last one picked chosen.</summary>
    private void RefreshBenchScopes()
    {
        SetUpBenchFind();
        List<string> scopes = [Everywhere, MineScope, GameScope, ModsScope];
        if (RedkitScripts() is not null) scopes.Add(RedkitScope);
        if (_findFolder is not null) scopes.Add(_findFolder);
        scopes.Add(ChooseFolder);
        Results.SetScopes(scopes, _findScope);
        _findScope = Results.Scope;
    }

    private void BenchScopePicked(string scope)
    {
        if (scope != ChooseFolder)
        {
            _findScope = scope;
            return;
        }
        var dialog = new OpenFolderDialog
        {
            Title = "Search the scripts in a folder",
            InitialDirectory = _findFolder ?? (Directory.Exists(App.Settings.LastOpenFolder) ? App.Settings.LastOpenFolder : ""),
        };
        if (dialog.ShowDialog(this) == true)
        {
            _findFolder = Path.GetFullPath(dialog.FolderName).TrimEnd('\\');
            _findScope = _findFolder;
        }
        // set after the list's own selection change is over
        Dispatcher.BeginInvoke(RefreshBenchScopes);
    }

    /// <summary>REDkit's own scripts (r4data\scripts), next to the wcc_lite.exe set in Settings.</summary>
    private static string? RedkitScripts()
    {
        if (string.IsNullOrWhiteSpace(App.Settings.WccLitePath)) return null;
        try
        {
            string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(App.Settings.WccLitePath)!, "..", "..", "r4data", "scripts"));
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private void BenchFind(Regex pattern, string text)
    {
        string scope = Results.Scope ?? Everywhere;
        var pass = FolderSearch.PathFilter(Results.FileFilter);
        // the tree's own files, so a match opens in the tab the tree would open
        List<ScriptFile> Listed(BenchNode? group) =>
            group is null ? [] : [.. group.Files().Where(n => n.Entry is not null).Select(n => ScriptWorkspace.FileFor(n.Entry!))];
        var mine = scope is Everywhere or MineScope ? Listed(_mine) : [];
        var game = scope is Everywhere or GameScope ? Listed(_game) : [];
        string? mods = scope is Everywhere or ModsScope ? _modsFolder : null;
        string? folder = scope == RedkitScope ? RedkitScripts() : scope == _findFolder ? _findFolder : null;
        var unsaved = AllDocuments.Where(d => d.IsDirty && d.Item.Script is not null)
            .GroupBy(d => d.Item.Script!.Key).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
        string where = scope == Everywhere ? "your mod, the game's scripts and the installed mods" : scope == _findFolder ? Path.GetFileName(scope) : scope;

        ShowResults(Current, FindTitle, $"Looking for '{text}' in {where}…", cancel =>
        {
            var files = new List<ScriptFile>(mine);
            files.AddRange(game);
            if (mods is not null) files.AddRange(InstalledModScripts(mods, cancel));
            if (folder is not null) files.AddRange(FolderScripts(folder));
            files = [.. files.Where(f => pass(UnderScripts(f.Path)))];
            var hits = ScriptSearch.Text(pattern, files, unsaved, cancel);
            return (ResultRow.ByFile(hits), $"{Summary(hits, "match", "matches")}  ·  {files.Count:N0} scripts searched");
        }, mine.Count + game.Count == 0 && mods is null && folder is null
            ? scope == MineScope ? "No project is open, so there are no scripts of yours to search. Open or create one from the main window."
              : "The game's scripts weren't found. Set the game folder in Settings, or pick a folder in Look in."
            : $"Nothing matches '{text}' in {where}" + (Results.FileFilter.Length > 0 ? $" (files: {Results.FileFilter})." : "."));
    }

    /// <summary>A script's path under its scripts folder, as the Files box matches it: game\player\r4Player.ws.</summary>
    private static string UnderScripts(string path)
    {
        foreach (string prefix in new[] { @"content\scripts\", @"scripts\" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path[prefix.Length..];
        return path;
    }

    /// <summary>Each installed mod's scripts in the folder's order: a compiled one's decompiled, others' loose.</summary>
    private static List<ScriptFile> InstalledModScripts(string modsFolder, CancellationToken cancel)
    {
        var blobs = ScriptWorkspace.InstalledBlobsAsync(modsFolder).GetAwaiter().GetResult()
            .ToDictionary(b => b.Mod, b => b.Files, StringComparer.OrdinalIgnoreCase);
        var files = new List<ScriptFile>();
        string[] mods;
        try
        {
            mods = Directory.GetDirectories(modsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return files;
        }
        foreach (string mod in mods.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            if (blobs.TryGetValue(Path.GetFileName(mod), out var decompiled)) files.AddRange(decompiled);
            else files.AddRange(FolderScripts(Path.Combine(mod, "content", "scripts")));
        }
        return files;
    }

    /// <summary>Every .ws under a folder, read from disk, as the tree lists a folder of loose scripts.</summary>
    private static IEnumerable<ScriptFile> FolderScripts(string folder) =>
        Directory.Exists(folder)
            ? ScriptFolder.Open(folder).Entries.OfType<LooseScriptEntry>().Where(e => e.IsScript).Select(e => ScriptWorkspace.FileFor(e))
            : [];
}
