using System.Buffers.Binary;
using System.IO;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>A texture's level chain in one format, for a texture array to take a slice from: its format and its levels, largest first.</summary>
public sealed record TextureLevels(byte Format, List<TextureLevel> Levels);

/// <summary>
/// What a layered texture (a cube map or a texture array) is, as the cooked one a picture takes the place of says: its
/// cache format, its size and level count, its slices, and an array's group.
/// </summary>
public sealed record LayeredTemplate(byte Format, int Width, int Height, int Mips, int Slices, string? Group);

/// <summary>
/// Cube maps (<c>.w2cube</c>) and texture arrays (<c>.texarray</c>), cooked as REDkit's <c>wcc_lite cook</c> and
/// <c>buildcache textures</c> do. Both keep their levels "mip-major": each level holds every slice's (a cube's six faces,
/// in the cache's order) one after another. The cooked resource keeps the last six levels itself (none when there are
/// six or fewer) and the texture cache the whole chain, the larger levels a block each and the resident ones in one.
/// <para>
/// A cooked cube keeps no properties; after them come its cache key, the first resident level (16 bits), its cache
/// format and kind (3 plain, 4 block-compressed) with 0x80 set, its edge and level count (16 bits each), the resident
/// levels' size, FFFFFFFF, and those levels. A REDkit source cube keeps the same header (its key −1, nothing resident,
/// kind without 0x80), from version 151 on four bytes more, then every level of each face in turn, face by face; its
/// faces' own bitmaps, which it names, aren't read, as the cook doesn't.
/// </para>
/// <para>
/// A cooked array keeps its <c>textureGroup</c>; after it come its key, cache format and kind (no 0x80), width, height,
/// slice and level counts and first resident level (16 bits each), the resident size, FFFFFFFF and the levels. A REDkit
/// source array names a bitmap for each slice; their levels are put together, made again in the array's format (from
/// its group) when a slice's is another.
/// </para>
/// <para>
/// REDkit's wcc_lite as installed drops each cube's top level (its LOD settings are missing); the game's own cubes keep
/// it, as these do, unless <c>dropLevels</c> asks otherwise (to compare with wcc_lite).
/// </para>
/// </summary>
public static partial class TextureCooker
{
    private const int LayeredResident = 6;

    // ---------------------------------------------------------------- cube maps

    /// <summary>Cooks a REDkit source cube for <paramref name="gamePath"/>. Null when it isn't one (a cooked one is packed as it is).</summary>
    public static CookedTexture? CookCube(string gamePath, byte[] source, int dropLevels = 0)
    {
        var texture = ReadTexture(source);
        if (texture is null || texture.Cooked || texture.ClassName != "CCubeTexture") return null;
        var t = texture.Tail;
        if (t.Length < 16) return null;
        byte format = t[6], kind = t[7];
        int edge = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(8)), mips = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(10));
        int size = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(12));
        // the oldest sources keep their faces another way, with no levels counted
        if (mips == 0 || edge == 0 || FormatOf(format) == TextureFormat.Unknown || size <= 0 || t.Length - 16 < size)
            throw new InvalidDataException(L.T("TextureCooker.OldCube"));
        int at = 16 + (t.Length - 16 - size);
        var info = FormatOf(format);
        var faces = new byte[6][][];
        for (int f = 0; f < 6; f++)
        {
            faces[f] = new byte[mips][];
            for (int m = 0; m < mips; m++)
            {
                int bytes = TextureDecoder.LevelSize(info, Math.Max(1, edge >> m), Math.Max(1, edge >> m));
                if (at + bytes > t.Length) throw new InvalidDataException(L.T("TextureCooker.CubeShort"));
                faces[f][m] = t.AsSpan(at, bytes).ToArray();
                at += bytes;
            }
        }
        var levels = Enumerable.Range(0, mips).Select(m => faces.SelectMany(face => face[m]).ToArray()).ToList();
        return CookedCube(gamePath, format, kind, edge, levels, dropLevels);
    }

    private static CookedTexture CookedCube(string gamePath, byte format, byte kind, int edge, List<byte[]> levels, int dropLevels = 0, string? note = null)
    {
        int drop = Math.Clamp(dropLevels, 0, levels.Count - 1);
        levels = levels.Skip(drop).ToList();
        edge = Math.Max(1, edge >> drop);
        int resident = Math.Max(0, levels.Count - LayeredResident);
        string path = BundleWriter.GamePath(gamePath).ToLowerInvariant();
        uint key = TextureCacheWriter.Key(path);

        var builder = NewResource();
        builder.Name("CCubeTexture");
        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        w.Write((byte)0);
        w.Write((ushort)0);
        w.Write(key);
        w.Write((ushort)resident);
        w.Write(format);
        w.Write((byte)(kind | 0x80));
        w.Write((ushort)edge);
        w.Write((ushort)levels.Count);
        w.Write(levels.Skip(resident).Sum(l => l.Length));
        w.Write(uint.MaxValue);
        foreach (var level in levels.Skip(resident)) w.Write(level);
        w.Flush();
        builder.AddExport("CCubeTexture", CookedFlag, 0, 0, data.ToArray(), null);
        var cache = resident > 0 ? new CacheTexture(path, key, edge, edge, 6, format, kind, true, levels, resident) : null;
        return new CookedTexture(builder.Build(), cache, note);
    }

    // ---------------------------------------------------------------- texture arrays

    /// <summary>
    /// Cooks a REDkit source texture array for <paramref name="gamePath"/>, its slices read with <paramref name="slice"/>
    /// (by their game paths). Null when it isn't one (a cooked one is packed as it is).
    /// </summary>
    public static CookedTexture? CookArray(string gamePath, byte[] source, Func<string, TextureLevels?> slice, int dropLevels = 0)
    {
        var texture = ReadTexture(source);
        if (texture is null || texture.Cooked || texture.ClassName != "CTextureArray") return null;
        var json = Cr2wJson.ToJson(Cr2wFile.Read(source), out _);
        var bitmaps = (json["objects"]?[0]?["bitmaps"] as JsonArray ?? [])
            .Select(b => (string?)b?["m_texture"] is { } r && r.StartsWith("import:", StringComparison.Ordinal) ? r[7..] : null).ToList();
        if (bitmaps.Count == 0 || bitmaps.Any(b => b is null)) throw new InvalidDataException(L.T("TextureCooker.ArrayNoSlices"));
        string? group = texture.Text("textureGroup");
        var notes = new List<string>();
        var slices = new List<TextureLevels>();
        foreach (string path in bitmaps!)
            slices.Add(slice(path!) ?? throw new InvalidDataException(L.T("TextureCooker.ArraySliceMissing", path!)));
        // the array's format: its group's, else its first slice's
        byte format = GroupCompression(group ?? "") is { } c ? CacheFormat(c) : slices[0].Format;
        var levels = Stack(slices, format, notes);
        return CookedArray(gamePath, group, format, levels, slices.Count, dropLevels, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    /// <summary>
    /// Slices' levels put together, level by level, each slice in <paramref name="format"/>: one in another is decoded and
    /// made again. Every slice must be the size of the first.
    /// </summary>
    private static List<TextureLevel> Stack(List<TextureLevels> slices, byte format, List<string> notes)
    {
        int width = slices[0].Levels[0].Width, height = slices[0].Levels[0].Height;
        var chains = new List<List<TextureLevel>>();
        foreach (var s in slices)
        {
            if (s.Levels[0].Width != width || s.Levels[0].Height != height)
                throw new InvalidDataException(L.T("TextureCooker.ArraySizes", s.Levels[0].Width, s.Levels[0].Height, width, height));
            if (s.Format == format) { chains.Add(s.Levels); continue; }
            var decoded = DecodeLevel(s.Format, s.Levels[0]) ?? throw new InvalidDataException(L.T("TextureCooker.ArraySliceFormat"));
            if (format is 10 or 13) throw new InvalidDataException(L.T("TextureCooker.CantEncodedHereSo", Compression(format)));
            chains.Add(Chain(decoded, width, height, format, s.Levels.Count));
            if (notes.Count == 0) notes.Add(L.T("TextureCooker.ArraySliceRemade"));
        }
        int count = chains.Min(c => c.Count);
        return Enumerable.Range(0, count).Select(m => new TextureLevel(chains[0][m].Width, chains[0][m].Height, chains[0][m].Pitch,
            chains.SelectMany(c => c[m].Data).ToArray())).ToList();
    }

    private static CookedTexture CookedArray(string gamePath, string? group, byte format, List<TextureLevel> levels, int slices, int dropLevels = 0, string? note = null)
    {
        int drop = Math.Clamp(dropLevels, 0, levels.Count - 1);
        levels = levels.Skip(drop).ToList();
        int resident = Math.Max(0, levels.Count - LayeredResident);
        byte kind = (byte)(format == 253 ? 3 : 4);
        string path = BundleWriter.GamePath(gamePath).ToLowerInvariant();
        uint key = TextureCacheWriter.Key(path);
        int width = levels[0].Width, height = levels[0].Height;

        var builder = NewResource();
        builder.Name("CTextureArray");
        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        w.Write((byte)0);
        if (group is not null) Property(builder, w, new SourceProperty("textureGroup", "CName", [0, 0], group));
        w.Write((ushort)0);
        w.Write(key);
        w.Write(format);
        w.Write(kind);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((ushort)slices);
        w.Write((ushort)levels.Count);
        w.Write((ushort)resident);
        w.Write(levels.Skip(resident).Sum(l => l.Data.Length));
        w.Write(uint.MaxValue);
        foreach (var level in levels.Skip(resident)) w.Write(level.Data);
        w.Flush();
        builder.AddExport("CTextureArray", CookedFlag, 0, 0, data.ToArray(), null);
        var cache = resident > 0 ? new CacheTexture(path, key, width, height, slices, format, kind, false, levels.Select(l => l.Data).ToList(), resident) : null;
        return new CookedTexture(builder.Build(), cache, note);
    }

    // ---------------------------------------------------------------- reading textures for slices and templates

    /// <summary>
    /// A bitmap's levels: a REDkit source's, or a cooked one's, its larger levels from the texture cache
    /// (<paramref name="cachePixels"/>, its whole chain as the cache has it; null without one). Null when it isn't a bitmap.
    /// </summary>
    public static TextureLevels? BitmapLevels(byte[] resource, Func<byte[]?>? cachePixels = null)
    {
        var texture = ReadTexture(resource);
        if (texture is null || texture.ClassName != "CBitmapTexture") return null;
        byte format = texture.Properties.Any(p => p.Name == "compression") ? CacheFormat(texture.Text("compression")) : (byte)253;
        if (!texture.Cooked) return SourceLevels(texture.Tail) is { } source ? new TextureLevels(format, source) : null;

        // cooked: every level's size, then the resident ones
        var t = texture.Tail;
        if (t.Length < 8) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(4));
        if (count <= 0 || count > 16 || t.Length < 8 + count * 12 + 8) return null;
        var sizes = Enumerable.Range(0, count).Select(i => (W: BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(8 + i * 12)),
            H: BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(12 + i * 12)), P: BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(16 + i * 12)))).ToList();
        int resident = texture.Properties.FirstOrDefault(p => p.Name == "residentMipIndex")?.Value is [var r, ..] ? r : 0;
        int Bytes(int i) => format == 253 ? sizes[i].P * sizes[i].H : sizes[i].P * Math.Max(1, (sizes[i].H + 3) / 4);
        int at = 8 + count * 12 + 8;
        byte[]? whole = resident > 0 ? cachePixels?.Invoke() : null;
        if (resident > 0 && whole is null) return null;
        var levels = new List<TextureLevel>();
        int from = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] bytes;
            if (whole is not null)
            {
                bytes = whole.AsSpan(from, Math.Min(Bytes(i), whole.Length - from)).ToArray();
                from += Bytes(i);
            }
            else
            {
                if (at + Bytes(i) > t.Length) return null;
                bytes = t.AsSpan(at, Bytes(i)).ToArray();
                at += Bytes(i);
            }
            levels.Add(new TextureLevel(sizes[i].W, sizes[i].H, sizes[i].P, bytes));
        }
        return new TextureLevels(format, levels);
    }

    /// <summary>A game bitmap's whole chain, its larger levels from the texture cache that has them; null when it isn't found or isn't a bitmap.</summary>
    public static TextureLevels? GameLevels(ResourceLocator locator, string path)
    {
        if (locator.ReadFile(path) is not { } resource) return null;
        var entry = locator.FindTexture(path);
        return BitmapLevels(resource, entry is null ? null : () =>
        {
            using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TextureCacheArchive.ReadPixels(fs, entry.Info);
        });
    }

    /// <summary>What a cooked cube or array is, for a picture to take its place; null when it isn't one.</summary>
    public static LayeredTemplate? ReadLayered(byte[] resource)
    {
        var texture = ReadTexture(resource);
        if (texture is null || !texture.Cooked) return null;
        var t = texture.Tail;
        if (texture.ClassName == "CCubeTexture" && t.Length >= 12)
        {
            int edge = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(8));
            return new LayeredTemplate(t[6], edge, edge, BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(10)), 6, null);
        }
        if (texture.ClassName == "CTextureArray" && t.Length >= 16)
            return new LayeredTemplate(t[4], BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(10)), texture.Text("textureGroup"));
        return null;
    }

    // ---------------------------------------------------------------- pictures

    /// <summary>
    /// Makes a picture into a cooked cube (<paramref name="cube"/>) or texture array: a .dds cube or array, or a strip of
    /// square faces or slices side by side (or one above another), a cube's in the cache's order (+X, −X, +Y, −Y, +Z, −Z).
    /// The template, the cooked texture it takes the place of, gives the format and, for an array, the group and how many
    /// slices a strip has; without one a cube is plain 32-bit colour and an array's slices are counted from the strip.
    /// </summary>
    public static CookedTexture ImportLayered(string gamePath, bool cube, string picturePath, byte[] picture, LayeredTemplate? template)
    {
        var notes = new List<string>();
        List<(byte[] Rgba, int Size)> tiles;
        if (Path.GetExtension(picturePath).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!Dds.TryRead(picture, out var info, out var pixels)) throw new InvalidDataException(L.T("TextureCooker.DdsFormatCantUsed"));
            if (cube && info.Slices != 6) throw new InvalidDataException(L.T("TextureCooker.CubeNeedsSix", info.Slices));
            tiles = [];
            for (int s = 0; s < info.Slices; s++)
            {
                var decoded = TextureDecoder.Decode(info, pixels, 0, s) ?? throw new InvalidDataException(L.T("TextureCooker.DdsCouldntDecodedMake"));
                if (decoded.Width != decoded.Height && cube) throw new InvalidDataException(L.T("TextureCooker.FacesSquare"));
                tiles.Add((ToRgba(decoded.Bgra), decoded.Width));
            }
            if (!cube) return ArrayFrom(gamePath, tiles.Select(x => x.Rgba).ToList(), info.Width, info.Height, template, notes);
        }
        else
        {
            var (rgba, width, height) = Pictures.Read(picturePath, picture);
            // side by side when wider than tall, one above another when taller; an array's slices are square ones when the
            // picture splits into those, else as many as the array it takes the place of has
            bool across = width >= height;
            int count = cube ? 6 : 0;
            if (!cube && (across ? width % Math.Max(1, height) : height % Math.Max(1, width)) == 0)
                count = across ? width / Math.Max(1, height) : height / Math.Max(1, width);
            if (count <= 0) count = template?.Slices ?? 0;
            int tileW = across ? width / count : width, tileH = across ? height : height / count;
            if (count <= 0 || (across ? width % count : height % count) != 0 || cube && tileW != tileH)
                throw new InvalidDataException(cube ? L.T("TextureCooker.CubeStrip", width, height) : L.T("TextureCooker.ArrayStrip", width, height, count));
            tiles = [];
            for (int i = 0; i < count; i++)
            {
                var tile = new byte[tileW * tileH * 4];
                for (int y = 0; y < tileH; y++)
                {
                    int sx = across ? i * tileW : 0, sy = across ? y : i * tileH + y;
                    Array.Copy(rgba, (sy * width + sx) * 4, tile, y * tileW * 4, tileW * 4);
                }
                tiles.Add((tile, tileW));
            }
            if (!cube) return ArrayFrom(gamePath, tiles.Select(x => x.Rgba).ToList(), tileW, tileH, template, notes);
        }

        // a cube: every face's chain in the template's format (plain 32-bit colour without one)
        int edge = tiles[0].Size;
        if (tiles.Any(x => x.Size != edge)) throw new InvalidDataException(L.T("TextureCooker.FacesSquare"));
        byte format = template?.Format ?? 253;
        if (format is 10 or 13 or 16) { notes.Add(L.T("TextureCooker.CantEncodedHereSo", Compression(format))); format = 253; }
        if (template is { } tc && tc.Width != edge) notes.Add(L.T("TextureCooker.SizeChanged", tc.Width, edge));
        int mips = MipCount(edge, edge);
        var chains = tiles.Select(x => Chain(x.Rgba, edge, edge, format, mips)).ToList();
        var levels = Enumerable.Range(0, mips).Select(m => chains.SelectMany(c => c[m].Data).ToArray()).ToList();
        return CookedCube(gamePath, format, (byte)(format == 253 ? 3 : 4), edge, levels, 0, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    private static CookedTexture ArrayFrom(string gamePath, List<byte[]> tiles, int width, int height, LayeredTemplate? template, List<string> notes)
    {
        string group = template?.Group ?? (tiles.Any(HasAlpha) ? "WorldDiffuseWithAlpha" : "WorldDiffuse");
        byte format = template?.Format ?? CacheFormat(GroupCompression(group));
        if (format is 10 or 13) { notes.Add(L.T("TextureCooker.CantEncodedHereSo", Compression(format))); format = 8; }
        if (template is { } t && (t.Width != width || t.Height != height)) notes.Add(L.T("TextureCooker.SizeChanged", $"{t.Width}x{t.Height}", $"{width}x{height}"));
        if (template is { } ts && ts.Slices != tiles.Count) notes.Add(L.T("TextureCooker.SlicesChanged", ts.Slices, tiles.Count));
        int mips = MipCount(width, height);
        var slices = tiles.Select(x => new TextureLevels(format, Chain(x, width, height, format, mips))).ToList();
        var levels = Stack(slices, format, notes);
        return CookedArray(gamePath, group, format, levels, tiles.Count, 0, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    /// <summary>
    /// A cooked texture's whole chain as a texture cache lays it out (every level, each with every slice), with what it
    /// is, to show before it's built: the cache entry's when it has one, else what the resource keeps (all of it then).
    /// Null when it can't be read.
    /// </summary>
    public static (TextureInfo Info, byte[] Pixels)? Preview(CookedTexture cooked)
    {
        if (cooked.Cache is { } c)
        {
            var pixels = c.Mips.SelectMany(m => m).ToArray();
            return (new TextureInfo(c.Width, c.Height, c.Mips.Count, c.Slices, c.Format, FormatOf(c.Format), 16, 0, pixels.Length, pixels.Length) { IsCube = c.IsCube }, pixels);
        }
        var texture = ReadTexture(cooked.Resource);
        if (texture is null) return null;
        var t = texture.Tail;
        if (texture.ClassName == "CBitmapTexture" && BitmapLevels(cooked.Resource) is { } bitmap)
        {
            var pixels = bitmap.Levels.SelectMany(l => l.Data).ToArray();
            var l0 = bitmap.Levels[0];
            return (new TextureInfo(l0.Width, l0.Height, bitmap.Levels.Count, 1, bitmap.Format, FormatOf(bitmap.Format), 16, 0, pixels.Length, pixels.Length), pixels);
        }
        if (ReadLayered(cooked.Resource) is { } layered)
        {
            int at = texture.ClassName == "CCubeTexture" ? 20 : 24;
            var pixels = t.AsSpan(Math.Min(at, t.Length)).ToArray();
            return (new TextureInfo(layered.Width, layered.Height, layered.Mips, layered.Slices, layered.Format, FormatOf(layered.Format), 16, 0, pixels.Length, pixels.Length)
                { IsCube = texture.ClassName == "CCubeTexture" }, pixels);
        }
        return null;
    }

    /// <summary>A level's pixels as red, green, blue and alpha, from its cache format; null for one that can't be decoded.</summary>
    private static byte[]? DecodeLevel(byte format, TextureLevel level)
    {
        var tf = FormatOf(format);
        if (tf == TextureFormat.Unknown) return null;
        var info = new TextureInfo(level.Width, level.Height, 1, 1, format, tf, 16, 0, level.Data.Length, level.Data.Length);
        return TextureDecoder.Decode(info, level.Data, 0) is { } image ? ToRgba(image.Bgra) : null;
    }

    private static TextureFormat FormatOf(byte format) => TextureCacheArchive.FormatOf(format);
}
