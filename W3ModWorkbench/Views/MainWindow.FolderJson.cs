using System.IO;
using System.Windows;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A folder's CR2W files as JSON and back, from the tree's right-click menu: every file in the folder and under it
/// gets its <c>name.ext.json</c> beside it (or its file back from one), the originals kept, as the CR2W and JSON
/// window's Convert does.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The folder on disk a folder in the tree is, when the app may write into it: one of the open project's, or one
    /// under loose files opened from a folder. Null for an archive's folders, and for the game's, REDkit's, installed
    /// mods' and builds' folders.
    /// </summary>
    private static string? DiskFolder(FolderNode? node)
    {
        if (node?.Archive is not ScriptFolder folder) return null;
        string path = folder.Project is { } project
            ? node.IsBundle ? project.Root : Path.Combine(project.Root, node.FullPath)
            // loose files are listed under their folder's own name, so a folder's path starts beside it
            : node.IsBundle ? folder.FilePath : Path.Combine(Path.GetDirectoryName(folder.FilePath) ?? "", node.FullPath);
        return Directory.Exists(path) && FileSafety.CanEdit(Path.Combine(path, "file")) ? path : null;
    }

    private void TreeToJson_Click(object sender, RoutedEventArgs e) => _ = ConvertFolderAsync(toJson: true);

    private void TreeFromJson_Click(object sender, RoutedEventArgs e) => _ = ConvertFolderAsync(toJson: false);

    /// <summary>
    /// Converts every CR2W file in the folder picked in the tree, and its subfolders, to JSON beside it; or every
    /// converted JSON file back. What's there already is asked about once: replaced, or left as it is.
    /// </summary>
    private async Task ConvertFolderAsync(bool toJson)
    {
        if (DiskFolder(FolderTree.SelectedItem as FolderNode) is not { } folder || _extractCts is not null) return;
        string name = Path.GetFileName(folder.TrimEnd('\\'));
        string Output((string File, string Root) input) =>
            Cr2wJsonBatch.OutputPath(input.File, input.Root, null, toJson ? Cr2wJsonBatch.JsonName(input.File) : Cr2wJsonBatch.Cr2wName(input.File));

        var cts = _extractCts = new CancellationTokenSource();
        SetBusy(true);
        ExtractProgressBar.Value = 0;
        ExtractProgressBar.Maximum = 1;
        var progress = new Progress<Cr2wJsonProgress>(p =>
        {
            if (p.Total > 0)
            {
                ExtractProgressBar.Maximum = p.Total;
                ExtractProgressBar.Value = p.Done;
            }
            SetStatus(p.Stage switch
            {
                Cr2wJsonStage.Finding => L.T("FolderJson.Finding", p.Done),
                Cr2wJsonStage.Checking => L.T("FolderJson.Checking", p.Done, p.Total),
                _ => L.T(toJson ? "FolderJson.Converting" : "FolderJson.ConvertingBack", p.Done, p.Total, Path.GetFileName(p.Current)),
            });
        });
        List<Cr2wJsonResult> results;
        int skipped = 0;
        try
        {
            var inputs = await Task.Run(() => Cr2wJsonBatch.Inputs([folder], fromJson: !toJson, recursive: true, progress, cts.Token), cts.Token);
            if (inputs.Count == 0)
            {
                SetStatus(L.T(toJson ? "FolderJson.NoCr2w" : "FolderJson.NoJson", name));
                return;
            }
            var there = inputs.Where(i => File.Exists(Output(i))).ToList();
            bool replace = false;
            if (there.Count > 0)
            {
                var choice = await Message.Ask(L.T("FolderJson.ThereAlready", L.T("Count.File", there.Count)),
                    L.T(toJson ? "FolderJson.ReplaceJson" : "FolderJson.ReplaceFiles", there.Count), MessageKind.Question,
                    L.T("ProjectFiles.Replace"), L.T("FolderJson.Skip"));
                if (choice == MessageChoice.Cancel)
                {
                    SetStatus(L.T("MainWindow.Ready"));
                    return;
                }
                replace = choice == MessageChoice.Primary;
            }
            // what's there already is passed over, not reported as a failure, when it's to be left as it is
            if (!replace)
            {
                skipped = there.Count;
                inputs = [.. inputs.Except(there)];
            }
            results = await Task.Run(() => toJson
                ? Cr2wJsonBatch.ToJson(inputs, null, replace, progress, cts.Token)
                : Cr2wJsonBatch.FromJson(inputs, null, replace, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus(L.T("FolderJson.Stopped"));
            return;
        }
        finally
        {
            cts.Dispose();
            _extractCts = null;
            SetBusy(false);
        }
        int made = results.Count(r => r.Ok && r.Output is not null);
        var failed = results.Where(r => !r.Ok).ToList();
        // what's made in the project's folders is in the project (read again first, as that says what's open)
        if (made > 0 && ProjectNode is not null) await ReloadProjectAsync();
        SetStatus(L.T(cts.IsCancellationRequested ? "FolderJson.StoppedAfter" : toJson ? "FolderJson.Converted" : "FolderJson.ConvertedBack", L.T("Count.File", made), name)
            + (skipped > 0 ? L.T("FolderJson.Skipped", skipped) : ""));
        if (failed.Count > 0)
            ShowError(L.T("FolderJson.NotConverted", L.T("Count.File", failed.Count)) + "\n\n"
                + string.Join("\n", failed.Take(15).Select(f => $"{Path.GetRelativePath(folder, f.Input)}\n    {f.Error}"))
                + (failed.Count > 15 ? L.T("MainWindow.AndMore", failed.Count - 15) : ""));
    }
}
