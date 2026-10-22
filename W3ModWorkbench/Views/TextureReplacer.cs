using System.IO;
using System.Windows;
using Microsoft.Win32;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Replace with picture, for the texture viewers: a picture goes in the project beside the texture's game path (its
/// name, the picture's extension), where Build makes it into that texture, a bitmap, cube map or texture array, keeping
/// the game's settings for it. It's made the same way here first and shown, so what the mod will have can be seen.
/// </summary>
public static class TextureReplacer
{
    private static readonly string[] PictureExtensions = [".dds", .. Pictures.Extensions];

    /// <summary>Asks for the picture to replace a texture with; null when none is picked.</summary>
    public static string? Pick(Window owner, string gamePath)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("TextureReplacer.Pick", Path.GetFileName(gamePath)),
            Filter = L.T("TextureReplacer.Filter"),
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <param name="gamePath">The texture's game path (.xbm, .w2cube or .texarray).</param>
    /// <param name="resource">Reads the texture's cooked resource, for its settings; null when there's none to read.</param>
    public static async Task ReplaceAsync(Window owner, MessageCard message, string gamePath, Func<byte[]?>? resource, string picture)
    {
        bool cube = gamePath.EndsWith(".w2cube", StringComparison.OrdinalIgnoreCase);
        bool array = gamePath.EndsWith(".texarray", StringComparison.OrdinalIgnoreCase);

        // made here as Build will make it, to show and to find what's wrong before anything is saved
        CookedTexture cooked;
        try
        {
            cooked = await Task.Run(() =>
            {
                var template = resource?.Invoke();
                var bytes = File.ReadAllBytes(picture);
                return cube || array
                    ? TextureCooker.ImportLayered(gamePath, cube, picture, bytes, template is null ? null : TextureCooker.ReadLayered(template))
                    : TextureCooker.Import(gamePath, picture, bytes, template is null ? null : TextureCooker.ReadTexture(template));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await message.Show(L.T("TextureReplacer.CantUse"), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }

        var project = Application.Current.MainWindow is MainWindow main
            ? await main.EnsureProjectAsync(L.T("TextureReplacer.NeedsProject"), message)
            : ProjectWorkspace.Current;
        if (project is null) return;

        // the picture goes beside the texture's path; another picture already there for it would leave Build two to choose from
        string target = project.GameFilePath(Path.ChangeExtension(gamePath, Path.GetExtension(picture).ToLowerInvariant()));
        var others = PictureExtensions.Select(e => project.GameFilePath(Path.ChangeExtension(gamePath, e)))
            .Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToList();
        if (others.Count > 0)
        {
            bool replace = await message.Show(L.T("TextureReplacer.AlreadyOne"), L.T("TextureReplacer.AlreadyOneText", string.Join("\n", others.Select(Path.GetFileName))),
                MessageKind.Question, primary: L.T("TextureReplacer.Replace"), secondary: L.T("Common.Cancel"));
            if (!replace) return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            FileSafety.CheckWrite(target);
            if (!picture.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(picture, target, overwrite: true);
            foreach (string other in others)
            {
                FileSafety.CheckWrite(other);
                File.Delete(other);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await message.Show(L.T("TextureReplacer.CouldntSave"), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        if (Application.Current.MainWindow is MainWindow window)
        {
            if (!project.Contains(project.WorkspacePath)) await window.AddToProjectAsync([project.WorkspacePath]);
            else await window.ReloadProjectAsync();
        }

        // what the mod will have, shown as a texture of its own
        var preview = TextureCooker.Preview(cooked);
        var info = preview?.Info;
        if (preview is { } shown)
            TextureWindow.Open(Path.GetFileNameWithoutExtension(gamePath) + ".dds", L.T("TextureReplacer.PreviewSource"), Dds.Wrap(shown.Info, shown.Pixels), owner);
        string what = cube ? L.T("TextureReplacer.Cube") : array ? L.T("TextureReplacer.Array", info?.Slices ?? 0) : L.T("TextureReplacer.Bitmap");
        string text = L.T("TextureReplacer.Saved", Path.GetFileName(target), Path.GetFileName(gamePath), what, info?.Width ?? 0, info?.Height ?? 0, info?.FormatName ?? "-", info?.Mips ?? 0);
        if (cooked.Note is { } note) text += "\n\n" + L.T("TextureReplacer.Notes", note);
        await message.Show(L.T("TextureReplacer.Done"), text, cooked.Note is null ? MessageKind.Info : MessageKind.Warning, primary: L.T("Common.OK"));
    }
}
