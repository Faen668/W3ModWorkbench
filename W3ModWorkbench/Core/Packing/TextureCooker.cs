using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench.Core.Packing;

/// <summary>A cooked texture: the resource the bundle gets, what the texture cache gets (if anything), and anything to tell the modder.</summary>
public sealed record CookedTexture(byte[] Resource, CacheTexture? Cache, string? Note = null);

/// <summary>One mip level of a texture as a texture resource lists it: its size, row pitch and bytes.</summary>
public sealed record TextureLevel(int Width, int Height, int Pitch, byte[] Data);

/// <summary>
/// Cooks textures as REDkit's <c>wcc_lite cook</c> and <c>buildcache textures</c> do. A cooked <c>.xbm</c> keeps its
/// properties (minus the editor's: where it was imported from and when, and its source pixels), every mip level's size,
/// and the smallest levels' bytes; the rest go in the texture cache under the texture's key. What stays in the resource:
/// the last six levels of a streamed texture (32 pixels and down, for a full chain), or all of them for a group that isn't
/// streamed (the GUI's, fonts', flares'…). The cooked file's properties keep the class's order, with
/// <c>residentMipIndex</c> (left out when 0) and <c>textureCacheKey</c> after them.
/// <para>
/// A REDkit source texture already holds its levels compressed, so cooking one gives REDkit's bytes exactly. A picture
/// (.dds, .png, .tga…) is made into a texture first: a .dds keeps its format and levels; anything else gets a full mip
/// chain and is block-compressed here, as the texture group (or the texture it replaces) asks.
/// </para>
/// </summary>
public static class TextureCooker
{
    /// <summary>The levels a streamed texture keeps in its resource: those after its first count minus this.</summary>
    public const int ResidentLevels = 6;

    private const int CookedFlag = 0x2000;

    /// <summary>The texture groups (REDkit's <c>engine\textures\texturegroups.xml</c>) the game doesn't stream: their textures stay whole in the resource.</summary>
    private static readonly HashSet<string> NotStreamed = new(StringComparer.Ordinal)
    {
        "SystemNoMips", "TerrainNormalAtlas", "TerrainDiffuse", "GUIWithoutAlpha", "FoliageDiffuse", "Flares", "DetailNormalMap",
        "TerrainSpecial", "BillboardAtlas", "TerrainDiffuseAtlas", "GUIWithAlpha", "TerrainNormal", "PostFxMap", "DiffuseNoMips", "NormalsNoMips",
    };

    /// <summary>The texture groups whose textures have one level only.</summary>
    private static readonly HashSet<string> NoMips = new(StringComparer.Ordinal)
    {
        "SystemNoMips", "Font", "GUIWithoutAlpha", "GUIWithAlpha", "TerrainSpecial", "PostFxMap", "DiffuseNoMips", "NormalsNoMips",
    };

    /// <summary>Whether textures of a group stream from the texture cache.</summary>
    public static bool IsStreamed(string? group) => group is null || !NotStreamed.Contains(group);

    /// <summary>
    /// The texture cache's format number for a compression: 7 BC1, 8 BC3, 10 BC7, 14 BC4, 15 BC5, 13 RGBE, 253 RGBA8, as
    /// the game's own cooked textures and its cache line up.
    /// </summary>
    public static byte CacheFormat(string? compression) => compression switch
    {
        "TCM_DXTNoAlpha" or "TCM_Normals" => 7,
        "TCM_DXTAlpha" or "TCM_NormalsHigh" or "TCM_NormalsGloss" or "TCM_DXTAlphaLinear" => 8,
        "TCM_QualityColor" => 10,
        "TCM_QualityR" => 14,
        "TCM_QualityRG" => 15,
        "TCM_RGBE" => 13,
        _ => 253,
    };

    /// <summary>The compression a texture of a cache format gets when nothing says otherwise.</summary>
    private static string Compression(byte format) => format switch
    {
        7 => "TCM_DXTNoAlpha",
        8 => "TCM_DXTAlpha",
        10 => "TCM_QualityColor",
        14 => "TCM_QualityR",
        15 => "TCM_QualityRG",
        _ => "TCM_None",
    };

    // ---------------------------------------------------------------- reading a texture resource

    /// <summary>
    /// A property as a resource has it: its name, type and value's bytes, and for a name or enum value (whose bytes are an
    /// index into the file's names) the name itself.
    /// </summary>
    public sealed record SourceProperty(string Name, string Type, byte[] Value, string? Text = null);

    /// <summary>A texture resource's class, properties and what its class keeps after them, read from any CR2W version.</summary>
    public sealed record TextureResource(string ClassName, bool Cooked, List<SourceProperty> Properties, byte[] Tail)
    {
        /// <summary>A name or enum property's value, or a number's as text; null when the texture doesn't have it.</summary>
        public string? Text(string name) => Properties.FirstOrDefault(p => p.Name == name) is { } p
            ? p.Text ?? (p.Value.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(p.Value).ToString() : null)
            : null;
    }

    /// <summary>The first object of a texture resource, read; null when it isn't one.</summary>
    public static TextureResource? ReadTexture(byte[] data)
    {
        if (!Cr2wFile.IsCr2w(data)) return null;
        var file = Cr2wFile.Read(data);
        if (file.Exports.Count == 0) return null;
        var export = file.Exports[0];
        if (export.ClassName is not ("CBitmapTexture" or "CCubeTexture" or "CTextureArray")) return null;
        var (properties, tail) = ReadProperties(file, export.Offset, export.Offset + export.Size);
        return new TextureResource(export.ClassName, IsCooked(file), properties, tail);
    }

    /// <summary>
    /// An object's properties: a zero first (from some versions on), then each one's name, type (two bytes more before
    /// version 139) and size, up to a name of 0.
    /// </summary>
    private static (List<SourceProperty>, byte[] Tail) ReadProperties(Cr2wFile file, int start, int end)
    {
        var d = file.Data;
        var list = new List<SourceProperty>();
        int at = start;
        if (at < end && d[at] == 0 && at + 3 <= end && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 1)) < file.Names.Count) at++;
        bool pad = file.Version < 139;
        while (at + 2 <= end)
        {
            int name = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
            at += 2;
            if (name == 0) break;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
            at += pad ? 4 : 2;
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
            if (size < 4 || at + size > end || name >= file.Names.Count || type >= file.Names.Count)
                throw new InvalidDataException("A texture's property runs past its object.");
            var value = d.AsSpan(at + 4, size - 4).ToArray();
            string typeName = file.Names[type];
            string? text = null;
            if (value.Length == 2 && (typeName == "CName" || typeName.StartsWith('E')) && BinaryPrimitives.ReadUInt16LittleEndian(value) is var i && i < file.Names.Count)
                text = file.Names[i];
            list.Add(new SourceProperty(file.Names[name], typeName, value, text));
            at += size;
        }
        return (list, d.AsSpan(at, end - at).ToArray());
    }

    /// <summary>Whether a CR2W resource has been cooked: its objects carry the cook's flag.</summary>
    public static bool IsCooked(Cr2wFile file)
    {
        if (file.Legacy is not null || file.Exports.Count == 0) return false;
        int exportsAt = BinaryPrimitives.ReadInt32LittleEndian(file.Data.AsSpan(40 + 4 * 12));
        for (int i = 0; i < file.Exports.Count; i++)
            if ((BinaryPrimitives.ReadUInt16LittleEndian(file.Data.AsSpan(exportsAt + i * 24 + 2)) & CookedFlag) == 0) return false;
        return true;
    }

    /// <summary>
    /// A source texture's levels, as REDkit keeps them after the properties: a zero, the level count, then each level's
    /// width, height, pitch, size and bytes. Null when it isn't laid out that way.
    /// </summary>
    public static List<TextureLevel>? SourceLevels(byte[] tail)
    {
        if (tail.Length < 8) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(4));
        if (count <= 0 || count > 16) return null;
        var levels = new List<TextureLevel>(count);
        int at = 8;
        for (int i = 0; i < count; i++)
        {
            if (at + 16 > tail.Length) return null;
            int width = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at));
            int height = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 4));
            int pitch = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 8));
            int size = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 12));
            at += 16;
            if (size < 0 || at + size > tail.Length) return null;
            levels.Add(new TextureLevel(width, height, pitch, tail.AsSpan(at, size).ToArray()));
            at += size;
        }
        return levels;
    }

    // ---------------------------------------------------------------- cooking

    /// <summary>
    /// Cooks a REDkit source texture (an <c>.xbm</c> not cooked yet, as REDkit's depot and its import keep them) for
    /// <paramref name="gamePath"/>. Null when the file isn't one, a cooked one included: that's packed as it is.
    /// </summary>
    public static CookedTexture? CookBitmap(string gamePath, byte[] source)
    {
        var texture = ReadTexture(source);
        if (texture is null || texture.Cooked || texture.ClassName != "CBitmapTexture") return null;
        var levels = SourceLevels(texture.Tail);
        if (levels is null) return null;
        return Cook(gamePath, texture.Properties, levels, texture.Text("compression"), texture.Text("textureGroup"));
    }

    /// <summary>The cooked resource and cache entry for a texture's properties and levels.</summary>
    private static CookedTexture Cook(string gamePath, IReadOnlyList<SourceProperty> properties, List<TextureLevel> levels,
        string? compression, string? group, string? note = null)
    {
        bool streamed = IsStreamed(group);
        int resident = streamed ? Math.Max(0, levels.Count - ResidentLevels) : 0;
        string path = BundleWriter.GamePath(gamePath).ToLowerInvariant();
        uint key = TextureCacheWriter.Key(path);

        var builder = NewResource();
        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        builder.Name("CBitmapTexture");
        w.Write((byte)0);
        foreach (var p in properties)
        {
            if (p.Name is "importFile" or "importFileTimeStamp" or "sourceData" or "residentMipIndex" or "textureCacheKey" or "encodedTextureFormat") continue;
            Property(builder, w, p);
        }
        if (resident > 0) Property(builder, w, new SourceProperty("residentMipIndex", "Uint8", [(byte)resident]));
        Property(builder, w, new SourceProperty("textureCacheKey", "Uint32", BitConverter.GetBytes(key)));
        w.Write((ushort)0);

        // every level's size, then the resident levels' bytes
        w.Write(0);
        w.Write(levels.Count);
        foreach (var level in levels) { w.Write(level.Width); w.Write(level.Height); w.Write(level.Pitch); }
        w.Write(levels.Skip(resident).Sum(l => l.Data.Length));
        w.Write(16);
        foreach (var level in levels.Skip(resident)) w.Write(level.Data);
        w.Flush();
        builder.AddExport("CBitmapTexture", CookedFlag, 0, 0, data.ToArray(), null);

        CacheTexture? cache = null;
        if (resident > 0)
        {
            byte format = CacheFormat(compression);
            cache = new CacheTexture(path, key, levels[0].Width, levels[0].Height, 1, format, (byte)(format == 253 ? 3 : 4), false,
                levels.Select(l => l.Data).ToList(), resident);
        }
        return new CookedTexture(builder.Build(), cache, note);
    }

    /// <summary>A new cooked resource as the cook starts one: version 164, nothing dated, the empty name hashed as 0.</summary>
    internal static Cr2wBuilder NewResource() => Cr2wBuilder.Create(164, 0, 0, 0, 6, [""], [], new byte[16], 1, []);

    /// <summary>Writes one property into a cooked object: a name or enum value by its name, from the builder; anything else as its bytes.</summary>
    private static void Property(Cr2wBuilder builder, BinaryWriter w, SourceProperty p)
    {
        w.Write((ushort)builder.Name(p.Name));
        w.Write((ushort)builder.Name(p.Type));
        if (p.Text is { } text)
        {
            w.Write(6);
            w.Write((ushort)builder.Name(text));
        }
        else
        {
            w.Write(4 + p.Value.Length);
            w.Write(p.Value);
        }
    }

    // ---------------------------------------------------------------- pictures

    /// <summary>
    /// Makes a picture into the cooked texture <paramref name="gamePath"/> (an <c>.xbm</c>). <paramref name="template"/>,
    /// when given, is the texture it takes the place of (the workspace's, or the game's): its group, compression and other
    /// settings carry over. Without one the group comes from the name and the picture: <c>_n</c> files are normal maps,
    /// pictures with alpha keep it.
    /// </summary>
    public static CookedTexture Import(string gamePath, string picturePath, byte[] picture, TextureResource? template)
    {
        string? group = template?.Text("textureGroup");
        string? compression = template?.Text("compression");
        var notes = new List<string>();
        List<TextureLevel> levels;

        if (Path.GetExtension(picturePath).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!Dds.TryRead(picture, out var info, out var pixels))
                throw new InvalidDataException("the .dds is in a format that can't be used (BC1, BC3, BC4, BC5, BC7 and 32-bit RGBA can)");
            if (info.Slices > 1) throw new InvalidDataException("the .dds is a cube map or an array; only plain 2D textures can be made from a .dds yet");
            byte format = info.Format switch
            {
                TextureFormat.Bc1 => 7, TextureFormat.Bc3 => 8, TextureFormat.Bc7 => 10, TextureFormat.Bc4 => 14, TextureFormat.Bc5 => 15,
                TextureFormat.Rgba8 => 253,
                _ => throw new InvalidDataException($"a {info.FormatName} .dds can't be made into a texture"),
            };
            group ??= GuessGroup(gamePath, format is 8 or 253);
            if (CacheFormat(compression) != format)
            {
                if (compression is not null) notes.Add($"the .dds is {info.FormatName}, so the texture is too (the one it replaces was {compression})");
                compression = Compression(format);
            }
            int wanted = NoMips.Contains(group) ? 1 : MipCount(info.Width, info.Height);
            if (info.Mips >= wanted || format == 10)
            {
                levels = [];
                int at = 0;
                for (int m = 0; m < Math.Min(info.Mips, wanted); m++)
                {
                    int w = Math.Max(1, info.Width >> m), h = Math.Max(1, info.Height >> m);
                    int size = TextureDecoder.LevelSize(info.Format, w, h);
                    levels.Add(Level(format, w, h, pixels.AsSpan(at, size).ToArray()));
                    at += size;
                }
                if (info.Mips < wanted) notes.Add("the BC7 .dds has too few mip levels, and BC7 can't be encoded here to make the rest");
            }
            else
            {
                // too few levels: the first decoded, and the chain made again in the same format
                var decoded = TextureDecoder.Decode(info, pixels) ?? throw new InvalidDataException("the .dds couldn't be decoded to make its mip levels");
                levels = Chain(ToRgba(decoded.Bgra), info.Width, info.Height, format, wanted);
                notes.Add($"the .dds had {info.Mips} mip level{(info.Mips == 1 ? "" : "s")}; the rest were made");
            }
        }
        else
        {
            var (rgba, width, height) = Pictures.Read(picturePath, picture);
            bool alpha = HasAlpha(rgba);
            group ??= GuessGroup(gamePath, alpha);
            compression ??= GroupCompression(group) ?? (alpha ? "TCM_DXTAlpha" : "TCM_DXTNoAlpha");
            byte format = CacheFormat(compression);
            if (format is 10 or 13)
            {
                notes.Add($"{compression} can't be encoded here, so the texture is BC3 (TCM_DXTAlpha)");
                compression = "TCM_DXTAlpha";
                format = 8;
            }
            levels = Chain(rgba, width, height, format, NoMips.Contains(group) ? 1 : MipCount(width, height));
        }

        // the properties in the class's order: size, raw format (for a plain one), compression, group, downscale biases
        var props = new List<SourceProperty>
        {
            new("width", "Uint32", BitConverter.GetBytes(levels[0].Width)),
            new("height", "Uint32", BitConverter.GetBytes(levels[0].Height)),
        };
        if (template?.Properties.FirstOrDefault(p => p.Name == "format") is { } rawFormat && CacheFormat(compression) == 253) props.Add(rawFormat);
        if (compression != "TCM_None") props.Add(new SourceProperty("compression", "ETextureCompression", [0, 0], compression));
        props.Add(new SourceProperty("textureGroup", "CName", [0, 0], group));
        foreach (var bias in new[] { "pcDownscaleBias", "xboneDownscaleBias", "ps4DownscaleBias" })
            if (template?.Properties.FirstOrDefault(p => p.Name == bias) is { } b) props.Add(b);
        return Cook(gamePath, props, levels, compression, group, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    /// <summary>The group a new texture gets: a normal map for <c>_n</c> names, else world diffuse, with alpha or without.</summary>
    private static string GuessGroup(string gamePath, bool alpha)
    {
        string stem = Path.GetFileNameWithoutExtension(gamePath).ToLowerInvariant();
        if (Regex.IsMatch(stem, @"_n\d*$")) return "NormalmapGloss";
        return alpha ? "WorldDiffuseWithAlpha" : "WorldDiffuse";
    }

    /// <summary>The compression a texture group asks for (REDkit's texturegroups.xml); null for those that don't say.</summary>
    private static string? GroupCompression(string group) => group switch
    {
        "SystemNoMips" => "TCM_None",
        "MimicDecalsNormal" or "FoliageDiffuse" or "WorldDiffuseWithAlpha" or "Particles" or "BillboardAtlas" or "TerrainDiffuseAtlas"
            or "GUIWithAlpha" or "CharacterDiffuseWithAlpha" or "HeadDiffuseWithAlpha" or "SpeedTreeDiffuseWithAlpha" => "TCM_DXTAlpha",
        "SpecialQuestDiffuse" or "TerrainDiffuse" or "GUIWithoutAlpha" or "WorldEmissive" or "WorldDiffuse" or "WorldSpecular"
            or "ParticlesWithoutAlpha" or "CharacterDiffuse" or "CharacterEmissive" or "HeadDiffuse" or "HeadEmissive" or "DiffuseNoMips" => "TCM_DXTNoAlpha",
        "TerrainNormalAtlas" or "DetailNormalMap" or "WorldNormalHQ" or "SpecialQuestNormal" or "CharacterNormalHQ" or "HeadNormalHQ" => "TCM_NormalsHigh",
        "WorldNormal" or "TerrainNormal" or "CharacterNormal" or "HeadNormal" => "TCM_Normals",
        "NormalmapGloss" or "CharacterNormalmapGloss" or "NormalsNoMips" => "TCM_NormalsGloss",
        "QualityOneChannel" => "TCM_QualityR",
        "QualityTwoChannels" => "TCM_QualityRG",
        "QualityColor" => "TCM_QualityColor",
        _ => null,
    };

    /// <summary>The levels of a full chain: halving until both sides are 1.</summary>
    public static int MipCount(int width, int height)
    {
        int count = 1;
        while (width > 1 || height > 1) { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); count++; }
        return count;
    }

    /// <summary>
    /// A level as the resource lists it: a block-compressed level is at least one block across and down, so its width
    /// and height are at least 4, and its pitch is a row of blocks; a plain one is four bytes a pixel.
    /// </summary>
    private static TextureLevel Level(byte format, int width, int height, byte[] data) => format == 253
        ? new TextureLevel(width, height, width * 4, data)
        : new TextureLevel(Math.Max(4, width), Math.Max(4, height), Math.Max(1, (width + 3) / 4) * BcEncoder.BlockBytes(format), data);

    /// <summary>A mip chain made from a picture, each level a box-filtered half of the one before, encoded in <paramref name="format"/>.</summary>
    private static List<TextureLevel> Chain(byte[] rgba, int width, int height, byte format, int count)
    {
        var levels = new List<TextureLevel>(count);
        for (int m = 0; m < count; m++)
        {
            levels.Add(Level(format, width, height, format == 253 ? rgba : BcEncoder.Encode(rgba, width, height, format)));
            if (m + 1 < count) (rgba, width, height) = Half(rgba, width, height);
        }
        return levels;
    }

    private static (byte[], int, int) Half(byte[] rgba, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var output = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(y * 2, height - 1), y1 = Math.Min(y * 2 + 1, height - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, width - 1), x1 = Math.Min(x * 2 + 1, width - 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = rgba[(y0 * width + x0) * 4 + c] + rgba[(y0 * width + x1) * 4 + c] + rgba[(y1 * width + x0) * 4 + c] + rgba[(y1 * width + x1) * 4 + c];
                    output[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
            }
        }
        return (output, w, h);
    }

    private static byte[] ToRgba(byte[] bgra)
    {
        var rgba = (byte[])bgra.Clone();
        for (int i = 0; i + 3 < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return rgba;
    }

    private static bool HasAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 255) return true;
        return false;
    }
}
