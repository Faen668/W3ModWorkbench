using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>
/// One size of a terrain tile: its heights (0 to 65535 between the world's lowest and highest elevation), its control
/// map (which two textures each point is painted with, its slope threshold and UV scale) and its colour map, all
/// <see cref="Resolution"/> a side, row by row from the south, each row from the west.
/// </summary>
public sealed record TerrainLevel(int Resolution, ushort[] Heights, ushort[] Control, byte[] Colour, bool ColourCompressed)
{
    /// <summary>A point's background texture: a slice of the world's texture array, 1-based (0 is a hole).</summary>
    public static int Background(ushort control) => control & 31;

    /// <summary>A point's overlay texture, painted over the background on slopes: 1-based, as the background.</summary>
    public static int Overlay(ushort control) => (control >> 5) & 31;

    /// <summary>How steep a slope must be before the overlay shows, 0 to 7.</summary>
    public static int SlopeThreshold(ushort control) => (control >> 10) & 7;

    /// <summary>How the textures are scaled, 0 to 7.</summary>
    public static int UvScale(ushort control) => (control >> 13) & 7;

    /// <summary>The colour map as BGRA bytes, Resolution a side: REDkit keeps it as RGBA, the game cooks it to BC1.</summary>
    public byte[] ColourBgra()
    {
        int n = Resolution;
        var bgra = new byte[n * n * 4];
        if (!ColourCompressed)
        {
            for (int i = 0; i < n * n && i * 4 + 3 < Colour.Length; i++)
            {
                bgra[i * 4] = Colour[i * 4 + 2];
                bgra[i * 4 + 1] = Colour[i * 4 + 1];
                bgra[i * 4 + 2] = Colour[i * 4];
                bgra[i * 4 + 3] = 255;
            }
            return bgra;
        }
        var tile = new byte[64];
        int blocks = Math.Max(1, n / 4);
        for (int by = 0; by < blocks; by++)
            for (int bx = 0; bx < blocks; bx++)
            {
                int at = (by * blocks + bx) * 8;
                if (at + 8 > Colour.Length) continue;
                Bc.Bc1(Colour.AsSpan(at, 8), tile, false);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int o = ((by * 4 + y) * n + bx * 4 + x) * 4, t = (y * 4 + x) * 4;
                        if (o + 3 >= bgra.Length) continue;
                        bgra[o] = tile[t];
                        bgra[o + 1] = tile[t + 1];
                        bgra[o + 2] = tile[t + 2];
                        bgra[o + 3] = 255;
                    }
            }
        return bgra;
    }
}

/// <summary>
/// A terrain tile (.w2ter, a CTerrainTile): a square of a world's ground, in several sizes, largest first (REDkit keeps
/// three, 256, 128 and 64 a side; the game's cooked tiles the first two, their colour compressed). After its properties
/// the tile lists its sizes: for each, the buffers holding its heights, control map and colour map, and its size.
/// </summary>
public sealed class TerrainTileFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required IReadOnlyList<TerrainLevel> Levels { get; init; }

    public static bool IsTerrainTile(string path) => path.EndsWith(".w2ter", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lowest and highest height value the tile has, 0 to 65535.</summary>
    public (int Min, int Max) HeightValues => ((int)Resource.Integer("minHeightValue"), (int)Resource.Integer("maxHeightValue", 65535));

    /// <summary>
    /// Reads a tile; <paramref name="external"/> reads a buffer the file keeps outside (the game's cooked tiles keep
    /// theirs as .N.buffer files beside them). Only the largest <paramref name="levels"/> sizes are read.
    /// </summary>
    public static TerrainTileFile Read(byte[] data, Func<int, byte[]?> external, int levels = 99)
    {
        var file = Cr2wFile.Read(data);
        var tile = file.First("CTerrainTile") ?? throw new InvalidDataException(L.T("TerrainTileFile.NotTerrainTile"));
        var tail = tile.Tail;
        var result = new List<TerrainLevel>();
        int count = tail.Length >= 4 ? BitConverter.ToInt32(tail, 0) : 0;
        for (int i = 0; i < count && i < levels && 4 + 10 * i + 10 <= tail.Length; i++)
        {
            int at = 4 + 10 * i;
            int height = BitConverter.ToUInt16(tail, at), control = BitConverter.ToUInt16(tail, at + 2), colour = BitConverter.ToUInt16(tail, at + 4);
            int resolution = BitConverter.ToInt32(tail, at + 6);
            var h = file.Buffer(height, external);
            var c = file.Buffer(control, external);
            var col = file.Buffer(colour, external) ?? [];
            if (h is null || c is null || resolution <= 0) continue;
            result.Add(new TerrainLevel(resolution, Words(h, resolution), Words(c, resolution), col, col.Length < resolution * resolution * 4));
        }
        return new TerrainTileFile { File = file, Resource = tile, Levels = result };
    }

    private static ushort[] Words(byte[] bytes, int resolution)
    {
        var words = new ushort[resolution * resolution];
        Buffer.BlockCopy(bytes, 0, words, 0, Math.Min(bytes.Length, words.Length * 2));
        return words;
    }
}
