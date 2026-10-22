using System.IO;
using System.Text;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Compare;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// One side of a comparison: what to call it (where it's from), its game path, its bytes, and for a texture how to get
/// its pixels (a texture's .xbm keeps them in a cache beside it).
/// </summary>
public sealed record ComparedFile(string Label, string GamePath, byte[] Data, Func<TextureEntry?>? Texture = null);

/// <summary>
/// Compares two versions of a file in the way that suits it: CR2W files property by property and strings by ID in the
/// compare window, compiled scripts decompiled and compared as script text, textures as pictures. Text is left to the
/// caller's side-by-side text compare.
/// </summary>
public static class FileCompare
{
    public static bool IsTexture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xbm" or ".w2cube" or ".texarray" or ".dds" || Pictures.Extensions.Contains(Path.GetExtension(path));

    /// <summary>Whether this compares the two files: false for text and anything it has no way for, which the caller compares itself.</summary>
    public static async Task<bool> TryShowAsync(Window owner, ComparedFile left, ComparedFile right)
    {
        string ext = Path.GetExtension(right.GamePath).ToLowerInvariant();
        string title = left.GamePath.Equals(right.GamePath, StringComparison.OrdinalIgnoreCase) ? left.GamePath : $"{left.GamePath}  ↔  {right.GamePath}";

        if (ext == ".w3strings")
        {
            var tree = await Task.Run(() =>
            {
                try { return DiffTree.Strings(W3Strings.Read(left.Data, left.GamePath).Strings, W3Strings.Read(right.Data, right.GamePath).Strings); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
            });
            if (tree is null) return false;
            CompareWindow.ShowTree(owner, title, L.T("CompareWindow.StringsKind"), left.Label, right.Label, tree);
            return true;
        }

        if (ext == ".rsblob")
        {
            // compiled scripts read as the scripts they were compiled from, compared as text
            var texts = await Task.Run(() => (Decompiled(left), Decompiled(right)));
            if (texts.Item1 is null || texts.Item2 is null) return false;
            await PreviewWindow.ShowComparedAsync(new PreviewItem(Path.ChangeExtension(right.GamePath, ".ws"), right.Label, Encoding.UTF8.GetBytes(texts.Item2)),
                new PreviewItem(Path.ChangeExtension(left.GamePath, ".ws"), left.Label, Encoding.UTF8.GetBytes(texts.Item1)), owner);
            return true;
        }

        if (IsTexture(left.GamePath) || IsTexture(right.GamePath))
        {
            var pictures = await Task.Run(() => (Picture(left), Picture(right)));
            if (pictures.Item1.Image is null && pictures.Item2.Image is null) return false;
            CompareWindow.ShowPictures(owner, title, left.Label, right.Label, pictures.Item1.Image, pictures.Item1.Info, pictures.Item2.Image, pictures.Item2.Info);
            return true;
        }

        if (Cr2wFile.IsCr2w(left.Data) && Cr2wFile.IsCr2w(right.Data))
        {
            var tree = await Task.Run(() => DiffTree.Cr2w(left.Data, right.Data));
            if (tree is null) return false;
            CompareWindow.ShowTree(owner, title, L.T("CompareWindow.Cr2wKind"), left.Label, right.Label, tree);
            return true;
        }
        return false;
    }

    /// <summary>The kinds of file compared as text, side by side in the preview.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ws", ".xml", ".csv", ".txt", ".json", ".ini", ".yml", ".yaml", ".log", ".lua", ".subs", ".list",
    };

    /// <summary>
    /// Compares two files whatever they are: as <see cref="TryShowAsync"/> does, text side by side in the preview, and
    /// anything else byte for byte, with a message saying whether they're the same and where they first differ.
    /// </summary>
    public static async Task ShowAnyAsync(Window owner, ComparedFile left, ComparedFile right)
    {
        if (await TryShowAsync(owner, left, right)) return;
        if (TextExtensions.Contains(Path.GetExtension(right.GamePath)))
        {
            await PreviewWindow.ShowComparedAsync(new PreviewItem(right.GamePath, right.Label, right.Data), new PreviewItem(left.GamePath, left.Label, left.Data), owner);
            return;
        }
        int first = left.Data.AsSpan().CommonPrefixLength(right.Data);
        bool same = first == left.Data.Length && left.Data.Length == right.Data.Length;
        string text = same ? L.T("CompareWindow.BytesSame", Format.Bytes(left.Data.Length))
            : L.T("CompareWindow.BytesDiffer", left.Label, Format.Bytes(left.Data.Length), right.Label, Format.Bytes(right.Data.Length), first);
        if (owner is IMessageHost host) await host.ShowMessage(same ? L.T("CompareWindow.Identical") : L.T("CompareWindow.Different"), text, MessageKind.Info);
    }

    /// <summary>
    /// A copy of a game file (a mod's, the project's, or one from an archive of its own) compared with the game's own:
    /// the button on a viewer's info line. <paramref name="mineTexture"/> finds the copy's pixels for a texture.
    /// </summary>
    /// <param name="minePath">The copy's own path when it isn't the game file's: a picture standing for a texture.</param>
    public static async Task CompareWithGameAsync(Window owner, string gamePath, string label, byte[] mine, Func<TextureEntry?>? mineTexture, string? minePath = null)
    {
        var game = new ResourceLocator([], null, GameLocator.Resolve(null), otherMods: false);
        var (data, texture) = await Task.Run(() => (game.ReadFile(gamePath), IsTexture(gamePath) ? game.FindTexture(gamePath) : null));
        if (data is null && texture is null)
        {
            if (owner is IMessageHost host) await host.ShowMessage(L.T("CompareWindow.NotInGame"), L.T("CompareWindow.NotInGameText", gamePath), MessageKind.Info);
            return;
        }
        await ShowAnyAsync(owner, new ComparedFile(L.T("CompareWindow.Game"), gamePath, data ?? [], texture is null ? null : () => texture),
            new ComparedFile(label, minePath ?? gamePath, mine, mineTexture));
    }

    /// <summary>A compiled blob as one script text: each class's file in turn, under a line naming it.</summary>
    private static string? Decompiled(ComparedFile file)
    {
        string temp = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "compare-" + Guid.NewGuid().ToString("N") + ".rsblob");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllBytes(temp, file.Data);
            var docs = ScriptWriter.Write(ScriptBlob.Open(temp), file.Label);
            var text = new StringBuilder();
            foreach (var doc in docs.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
                text.Append("// ").Append(doc.Path).Append("\n\n").Append(doc.Text.Replace("\r\n", "\n").TrimEnd()).Append("\n\n");
            return text.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A texture's top level, and its format in a few words: from its cache, a .dds, or a picture file.</summary>
    private static (DecodedImage? Image, string Info) Picture(ComparedFile file)
    {
        try
        {
            if (file.Texture?.Invoke() is { } entry)
            {
                byte[] dds;
                using (var stream = File.OpenRead(entry.Archive.FilePath)) dds = entry.Read(stream);
                return Dds.TryRead(dds, out var info, out var pixels) ? (TextureDecoder.Decode(info, pixels), entry.Info.Description) : (null, "");
            }
            string ext = Path.GetExtension(file.GamePath);
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) && Dds.TryRead(file.Data, out var ddsInfo, out var ddsPixels))
                return (TextureDecoder.Decode(ddsInfo, ddsPixels), ddsInfo.Description);
            if (Pictures.Extensions.Contains(ext))
            {
                var (rgba, width, height) = Pictures.Read(file.GamePath, file.Data);
                for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                return (new DecodedImage(width, height, rgba), $"{width}x{height}");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
        return (null, L.T("CompareWindow.NoPixels"));
    }
}
