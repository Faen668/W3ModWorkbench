using System.IO;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Restarting into an update: what's open is written down before the app closes, and the updated copy opens it all
/// again (<see cref="SessionSnapshot"/>), so the user carries on where they were.
/// </summary>
public partial class MainWindow : IPendingChanges
{
    /// <summary>The Tools windows, by class name, and how each opens (as the Tools menu opens it).</summary>
    private Dictionary<string, Action> ToolOpeners() => new()
    {
        [nameof(AssetBrowserWindow)] = () => _ = BrowseGameFilesAsync(),
        [nameof(GameStringsWindow)] = OpenGameStrings,
        [nameof(SourceInspectorWindow)] = () => SourceInspectorWindow.Open(this),
        [nameof(LoadOrderWindow)] = () => LoadOrderWindow.Open(this),
        [nameof(XmlOverridesWindow)] = () => XmlOverridesWindow.Open(this),
        [nameof(ItemEditorWindow)] = () => ItemEditorWindow.Open(this),
        [nameof(LootLocatorWindow)] = () => LootLocatorWindow.Open(this),
        [nameof(BestiaryWindow)] = () => BestiaryWindow.Open(this),
        [nameof(MerchantFinderWindow)] = () => MerchantFinderWindow.Open(this),
        [nameof(ActionPointFinderWindow)] = () => ActionPointFinderWindow.Open(this),
        [nameof(ComparePickWindow)] = () => ComparePickWindow.Open(this),
        [nameof(ConsoleCommandsWindow)] = () => ConsoleCommandsWindow.Open(this),
        [nameof(FactsWindow)] = () => FactsWindow.Open(this),
        [nameof(LogWindow)] = () => LogWindow.Open(this),
        [nameof(QuestLogWindow)] = () => QuestLogWindow.Open(this),
        [nameof(InputBindingsWindow)] = () => InputBindingsWindow.Open(this),
        [nameof(HashToolsWindow)] = () => HashToolsWindow.Open(this),
        [nameof(Cr2wJsonWindow)] = () => Cr2wJsonWindow.Open(this),
        [nameof(SoundUpgradeWindow)] = () => SoundUpgradeWindow.Open(this),
        [nameof(SpeechWindow)] = () => SpeechWindow.Open(this),
    };

    /// <summary>A mod being built or files being extracted: an update waits for them.</summary>
    string? IPendingChanges.PendingChange =>
        _building ? L.T("UpdateWindow.Building") : _extractCts is not null ? L.T("UpdateWindow.Extracting") : null;

    /// <summary>Writes down what's open, for the updated copy to open again.</summary>
    internal void SaveSession()
    {
        var snapshot = new SessionSnapshot { Project = ProjectWorkspace.Current?.FilePath };
        // the project's own scripts reopen with it, from its workspace state
        SaveWorkspaceState();
        foreach (var root in _roots)
        {
            var archive = root.Archive;
            bool reopens = archive is Bundle or ScriptArchive or TextureCacheArchive or StringsFolder || archive is ScriptFolder { Project: null };
            if (reopens && (File.Exists(archive.FilePath) || Directory.Exists(archive.FilePath))) snapshot.Archives.Add(archive.FilePath);
        }

        var project = ProjectWorkspace.Current;
        var (items, active) = PreviewWindow.OpenItems();
        foreach (var item in items)
        {
            if (item.DiskPath is { } disk && project?.Contains(disk) == true) continue;
            snapshot.Tabs.Add(new SessionSnapshot.Tab { Disk = item.DiskPath, Source = item.Source, Path = item.Path, Active = item == active });
        }

        var tools = ToolOpeners();
        foreach (Window window in Application.Current.Windows)
        {
            if (window == this || !window.IsVisible) continue;
            string type = window.GetType().Name;
            if (tools.ContainsKey(type)) snapshot.Tools.Add(type);
            else if (window is IReopenable { } view)
            {
                string source = view.ReopenSource, path = view.ReopenPath;
                if (Path.IsPathFullyQualified(source) && File.Exists(source)) snapshot.Viewers.Add(new SessionSnapshot.Tab { Disk = source });
                else if (path.Length > 0) snapshot.Viewers.Add(new SessionSnapshot.Tab { Source = source, Path = path });
            }
        }
        snapshot.Tools = [.. snapshot.Tools.Distinct()];
        snapshot.Save();
    }

    /// <summary>Opens again what was open before the update (the project first, then the rest), quietly skipping what's gone.</summary>
    private async Task RestoreSessionAsync(SessionSnapshot snapshot)
    {
        try
        {
            if (snapshot.Project is { } project && File.Exists(project)) await OpenProjectAsync(project, quiet: true);
            var files = snapshot.Archives.Where(File.Exists).ToList();
            var folders = snapshot.Archives.Where(Directory.Exists).ToList();
            if (files.Count > 0) await LoadPathsAsync(files);
            if (folders.Count > 0) await LoadPathsAsync(folders, asFolders: true);

            var locator = new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles);
            var all = _roots.SelectMany(r => r.AllFiles()).ToList();
            // the tab in front opens last, so it's in front again
            foreach (var tab in snapshot.Tabs.OrderBy(t => t.Active))
            {
                var item = tab.Disk is { } disk
                    ? all.FirstOrDefault(i => i.Entry is LooseScriptEntry loose && loose.FullPath.Equals(disk, StringComparison.OrdinalIgnoreCase))
                    : all.FirstOrDefault(i => i.Entry.Path.Equals(tab.Path, StringComparison.OrdinalIgnoreCase)
                        && $"{i.Entry.Archive.Owner}\\{i.Entry.Archive.Name}".Equals(tab.Source, StringComparison.OrdinalIgnoreCase));
                if (item is not null) await PreviewAsync(item);
                else if (tab.Disk is { } loose && File.Exists(loose)) await LoadPathsAsync([loose]);
                else if (tab.Path is { Length: > 0 } path
                    && await Task.Run(() => { try { return locator.ReadFile(path); } catch { return null; } }) is { } data)
                    PreviewWindow.Show(new PreviewItem(path, tab.Source ?? "", data), this);
            }

            foreach (var view in snapshot.Viewers)
            {
                if (view.Disk is { } disk) { if (File.Exists(disk)) await LoadPathsAsync([disk]); }
                else if (view.Path is { Length: > 0 } path) await DataViewers.OpenLinkAsync(path, locator, this, this);
            }

            var tools = ToolOpeners();
            foreach (string tool in snapshot.Tools)
                if (tools.TryGetValue(tool, out var open)) open();
            Activate();
        }
        catch (Exception e)
        {
            // what couldn't be opened again is no reason to stop the app starting
            ShowError(L.T("UpdateWindow.RestoreFailed", e.Message));
        }
    }
}
