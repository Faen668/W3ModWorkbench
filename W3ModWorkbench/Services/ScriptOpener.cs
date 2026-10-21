using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Opens a script from a <see cref="ScriptCorpus"/> in a preview at a place in it, the way the main window would:
/// a project's file from the project, so it can be edited, a compiled mod's from its decompiled blob, and anything
/// else read-only, with go to definition working from it.
/// </summary>
public static class ScriptOpener
{
    private static readonly ConcurrentDictionary<string, ScriptFolder> Folders = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="from">The window it's opened from, which gets the focus back when the preview closes.</param>
    public static async Task OpenAsync(CorpusFile file, int offset, int length, Window from)
    {
        if (Application.Current.MainWindow is not MainWindow main) return;
        try
        {
            var open = main.OpenArchives().ToList();
            var item = await Task.Run(() =>
            {
                byte[] data = file.File.Read();
                if (file.Entry is { } entry) return main.PreviewItemFor(entry, data);

                // a project's file opens from the project open in the main window, so it can be edited and saved
                var loose = open.OfType<ScriptFolder>().SelectMany(a => a.Entries).OfType<LooseScriptEntry>()
                    .FirstOrDefault(e => e.FullPath.Equals(file.File.Key, StringComparison.OrdinalIgnoreCase));
                if (loose is not null) return main.PreviewItemFor(loose, data);

                var folder = Folders.GetOrAdd(file.File.SourceKey, ScriptFolder.Empty);
                return new PreviewItem(file.File.Path, file.File.Source, data, file.File, ScriptWorkspace.For(folder, main.OpenArchives));
            });
            PreviewWindow.ShowAt(item, from, offset, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (main is IMessageHost host)
                await host.ShowMessage(L.T("ScriptOpener.CouldNotRead", Path.GetFileName(file.File.Path)), ex.Message, MessageKind.Warning);
        }
    }
}
