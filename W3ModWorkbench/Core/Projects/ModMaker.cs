using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Projects;

/// <summary>What a file to put in a mod is to the one it was made from.</summary>
public enum ModMakerGroup { Item, Meshes, Materials, Textures, Other, Shared }

/// <summary>One file that can go into the mod: the one asked for, or one it uses.</summary>
/// <param name="UsedBy">The file that imports it, for saying why it's listed.</param>
/// <param name="Note">Why a shared file is left out, or a texture's format and size.</param>
/// <param name="Pick">Whether it's ticked to start with: the file asked for and what's its own.</param>
public sealed record ModMakerFile(string GamePath, ModMakerGroup Group, string? UsedBy, int Size, string Note, bool Pick, bool InProject)
{
    public bool IsTexture => Group == ModMakerGroup.Textures;

    /// <summary>A cube map or texture array can only go in as a .dds, which keeps its faces or slices.</summary>
    public bool IsLayered => GamePath.EndsWith(".w2cube", StringComparison.OrdinalIgnoreCase) || GamePath.EndsWith(".texarray", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// "Make a mod of this": a game file and what it uses (an entity's meshes, their materials and textures), found through
/// each file's imports, to copy into the project ready to change. Files many others use (the engine's material graphs,
/// common detail maps) are listed but left out, as changing them changes everything that uses them, and behaviour,
/// animation and other files are listed for those who want them. Textures go in as pictures, which Build makes into
/// the textures again with the game's settings.
/// </summary>
public static class ModMaker
{
    /// <summary>The most files followed, so an entity that reaches half the game stops somewhere.</summary>
    private const int MaxFiles = 400;

    /// <summary>The picture formats a texture can be copied as.</summary>
    public static readonly string[] PictureFormats = [".png", ".tga", ".dds"];

    public static ModMakerGroup GroupOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".w2ent" or ".w2mesh" => ModMakerGroup.Meshes,
        ".w2mi" => ModMakerGroup.Materials,
        ".xbm" or ".w2cube" or ".texarray" => ModMakerGroup.Textures,
        _ => ModMakerGroup.Other,
    };

    /// <summary>Why a file is one that many others use, so it's best left alone; null when it isn't.</summary>
    public static string? SharedReason(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        if (p.StartsWith(@"engine\")) return L.T("ModMaker.SharedEngine");
        if (p.EndsWith(".w2mg")) return L.T("ModMaker.SharedGraph");
        if (p.Contains(@"\common\") || p.Contains("detailmap") || p.Contains("textures_tileable")) return L.T("ModMaker.SharedCommon");
        if (p.Contains(@"\base_entities\")) return L.T("ModMaker.SharedBase");
        return null;
    }

    /// <summary>The file asked for and everything it reaches through entities, meshes and materials, the file first.</summary>
    /// <param name="inProject">Whether the project has a game path already, as a file or (for a texture) a picture.</param>
    /// <param name="rootData">The file as the viewer has it, when it isn't one the game has (opened from an archive of its own).</param>
    public static List<ModMakerFile> Find(string gamePath, ResourceLocator game, Func<string, bool> inProject, byte[]? rootData = null, CancellationToken token = default)
    {
        var result = new List<ModMakerFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string? UsedBy)>();
        queue.Enqueue((gamePath, null));
        seen.Add(gamePath);
        while (queue.Count > 0 && result.Count < MaxFiles)
        {
            token.ThrowIfCancellationRequested();
            var (path, usedBy) = queue.Dequeue();
            bool root = usedBy is null;
            string? shared = root ? null : SharedReason(path);
            var group = root ? ModMakerGroup.Item : shared is not null ? ModMakerGroup.Shared : GroupOf(path);
            byte[]? data = root && rootData is not null ? rootData : game.ReadFile(path);
            string note = shared ?? "";
            int size = data?.Length ?? 0;
            if (IsTexturePath(path) && game.FindTexture(path) is { } texture)
            {
                note = texture.Info.Description;
                size = texture.PackedSize;
            }
            else if (data is null && !IsTexturePath(path))
                note = L.T("ModMaker.NotFound");
            bool pick = group is ModMakerGroup.Item or ModMakerGroup.Meshes or ModMakerGroup.Materials or ModMakerGroup.Textures
                && (data is not null || group == ModMakerGroup.Textures && note.Length > 0);
            result.Add(new ModMakerFile(path, group, usedBy, size, note, pick, inProject(path)));

            // only what the item is made of is followed: its entities, meshes, materials and arrays' slices
            if (data is null || shared is not null || !(root || group is ModMakerGroup.Meshes or ModMakerGroup.Materials || path.EndsWith(".texarray", StringComparison.OrdinalIgnoreCase)))
                continue;
            Cr2wFile file;
            try { file = Cr2wFile.Read(data); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
            {
                continue;
            }
            foreach (var import in file.Imports)
            {
                string next = import.Path.Replace('/', '\\');
                if (next.Length > 0 && seen.Add(next)) queue.Enqueue((next, path));
            }
        }
        return result;
    }

    public static bool IsTexturePath(string path) => GroupOf(path) == ModMakerGroup.Textures;

    /// <summary>Where a file goes in the project: at its game path, a texture as a picture beside it.</summary>
    public static string TargetFor(WorkbenchProject project, ModMakerFile file, string pictureFormat) =>
        IsTexturePath(file.GamePath)
            ? project.GameFilePath(Path.ChangeExtension(file.GamePath, file.IsLayered ? ".dds" : pictureFormat))
            : project.CopyPathFor(file.GamePath);

    /// <summary>Whether the project has a game path already: the file itself or, for a texture, a picture for it.</summary>
    public static bool InProject(WorkbenchProject project, string gamePath) =>
        File.Exists(project.CopyPathFor(gamePath))
        || IsTexturePath(gamePath) && PictureFormats.Concat(Pictures.Extensions).Any(e => File.Exists(project.GameFilePath(Path.ChangeExtension(gamePath, e))));

    /// <summary>What copying did: the files written, the ones the project had already (left as they are), and what failed.</summary>
    public sealed record CopyResult(List<string> Copied, List<string> Existing, List<string> Failed);

    /// <summary>
    /// Copies the files picked into the project: each at its game path, a mesh with its buffers, a texture as a picture
    /// in <paramref name="pictureFormat"/> (a cube map or array as a .dds). What the project has already is left alone.
    /// </summary>
    public static CopyResult Copy(IEnumerable<ModMakerFile> files, string pictureFormat, ResourceLocator game, WorkbenchProject project, byte[]? rootData = null, IProgress<string>? progress = null)
    {
        var result = new CopyResult([], [], []);
        foreach (var file in files)
        {
            if (InProject(project, file.GamePath))
            {
                result.Existing.Add(file.GamePath);
                continue;
            }
            progress?.Report(file.GamePath);
            try
            {
                var writes = new List<(string Target, byte[] Data)>();
                if (IsTexturePath(file.GamePath))
                {
                    if (Picture(file.GamePath, file.IsLayered ? ".dds" : pictureFormat, game) is not { } picture)
                        throw new InvalidDataException(L.T("ModMaker.NoPixels"));
                    writes.Add((TargetFor(project, file, pictureFormat), picture));
                }
                else
                {
                    if ((file.Group == ModMakerGroup.Item && rootData is not null ? rootData : game.ReadFile(file.GamePath)) is not { } data) throw new InvalidDataException(L.T("ModMaker.NotFound"));
                    writes.Add((TargetFor(project, file, pictureFormat), data));
                    foreach (var (path, buffer) in Buffers(file.GamePath, game))
                        if (!File.Exists(project.CopyPathFor(path))) writes.Add((project.CopyPathFor(path), buffer));
                }
                foreach (var (target, data) in writes)
                {
                    FileSafety.CheckWrite(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                    result.Copied.Add(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                result.Failed.Add($"{file.GamePath}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>A file's numbered buffers (<c>x.w2mesh.1.buffer</c>, …), which hold a cooked mesh's vertices and go with it.</summary>
    public static List<(string GamePath, byte[] Data)> Buffers(string gamePath, ResourceLocator game)
    {
        var buffers = new List<(string, byte[])>();
        for (int n = 1; n < 64; n++)
        {
            string path = $"{gamePath}.{n}.buffer";
            if (game.ReadFile(path) is not { } data) break;
            buffers.Add((path, data));
        }
        return buffers;
    }

    /// <summary>
    /// A texture as a picture to edit: its top level as a .png or .tga, or every level (and every face or slice) as a
    /// .dds. Null when the game has no pixels for it.
    /// </summary>
    public static byte[]? Picture(string xbmPath, string format, ResourceLocator game)
    {
        if (game.FindTexture(xbmPath) is not { } entry) return null;
        byte[] dds;
        using (var stream = File.OpenRead(entry.Archive.FilePath)) dds = entry.Read(stream);
        if (format.Equals(".dds", StringComparison.OrdinalIgnoreCase)) return dds;
        if (!Dds.TryRead(dds, out var info, out var pixels) || TextureDecoder.Decode(info, pixels) is not { } image) return null;
        return format.Equals(".tga", StringComparison.OrdinalIgnoreCase) ? Tga(image) : Png(image);
    }

    private static byte[] Png(DecodedImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A 32-bit Targa, rows from the top, as <see cref="Pictures"/> reads it back.</summary>
    private static byte[] Tga(DecodedImage image)
    {
        var tga = new byte[18 + image.Bgra.Length];
        tga[2] = 2; // uncompressed true colour
        BitConverter.TryWriteBytes(tga.AsSpan(12), (ushort)image.Width);
        BitConverter.TryWriteBytes(tga.AsSpan(14), (ushort)image.Height);
        tga[16] = 32;
        tga[17] = 0x28; // 8 bits of alpha, top-left origin
        image.Bgra.CopyTo(tga, 18);
        return tga;
    }
}
