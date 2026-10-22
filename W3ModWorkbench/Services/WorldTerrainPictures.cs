using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench;

/// <summary>How a world's terrain is drawn on its map.</summary>
public enum TerrainLook
{
    /// <summary>Shaded by the light from the north-west, tinted by height.</summary>
    Relief,
    /// <summary>The terrain's own colour map, as the game tints it from afar.</summary>
    Colour,
    /// <summary>Each point in the colour of the texture painted there (its background texture).</summary>
    Textures,
}

/// <summary>
/// A world's terrain as pictures for its map, every tile read (from the project, the game or a folder on disk) at the
/// size that keeps the whole under a few thousand pixels, laid out from the south-west tile, rows from the north as
/// pictures go. Also keeps the heights and control values, to say what's under the mouse.
/// </summary>
public sealed class WorldTerrainPictures
{
    public required WorldTerrain Terrain { get; init; }
    /// <summary>Samples a side, the whole terrain.</summary>
    public required int Side { get; init; }
    public required ushort[] Heights { get; init; }
    public required ushort[] Control { get; init; }
    public required byte[] Colour { get; init; }
    /// <summary>How many tiles could be read.</summary>
    public required int TilesRead { get; init; }

    /// <summary>
    /// Reads every tile. <paramref name="read"/> reads a game path (a tile, or one of its .N.buffer files); progress
    /// hears how many tiles are done.
    /// </summary>
    public static WorldTerrainPictures Load(WorldTerrain terrain, Func<string, byte[]?> read, IProgress<int>? progress, CancellationToken token)
    {
        int n = terrain.TilesPerEdge;
        // the largest size each tile has that keeps the whole within 4096 samples a side
        int level = 0, res = terrain.TileResolution;
        while (res * n > 4096 && res > 32)
        {
            res /= 2;
            level++;
        }
        int side = res * n;
        var heights = new ushort[side * side];
        var control = new ushort[side * side];
        var colour = new byte[side * side * 4];
        int done = 0, readCount = 0;
        Parallel.For(0, n * n, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8) }, i =>
        {
            int row = i / n, col = i % n;
            if (terrain.Tiles[row, col] is not { } path) return;
            try
            {
                if (read(path) is not { } data) return;
                var tile = TerrainTileFile.Read(data, b => read($"{path}.{b}.buffer"), level + 1);
                if (tile.Levels.Count <= level) return;
                var l = tile.Levels[level];
                int r = l.Resolution;
                if (r != res) return;
                var bgra = l.ColourBgra();
                for (int y = 0; y < r; y++)
                {
                    // the tile's rows go north from its south edge; the picture's rows go south from the top
                    int to = ((n - 1 - row) * r + (r - 1 - y)) * side + col * r;
                    Array.Copy(l.Heights, y * r, heights, to, r);
                    Array.Copy(l.Control, y * r, control, to, r);
                    Array.Copy(bgra, y * r * 4, colour, to * 4, r * 4);
                }
                Interlocked.Increment(ref readCount);
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            progress?.Report(Interlocked.Increment(ref done));
        });
        return new WorldTerrainPictures { Terrain = terrain, Side = side, Heights = heights, Control = control, Colour = colour, TilesRead = readCount };
    }

    /// <summary>The terrain's picture drawn as asked.</summary>
    public BitmapSource Picture(TerrainLook look)
    {
        var pixels = look switch
        {
            TerrainLook.Colour => Colour,
            TerrainLook.Textures => Textures(),
            _ => Relief(),
        };
        var bitmap = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Bgra32, null, pixels, Side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Height and texture under a point of the world, or null off the terrain.</summary>
    public (float Metres, ushort Control)? At(System.Numerics.Vector2 point)
    {
        float step = Terrain.Size / Side;
        int x = (int)((point.X - Terrain.Corner.X) / step), yFromSouth = (int)((point.Y - Terrain.Corner.Y) / step);
        if (x < 0 || yFromSouth < 0 || x >= Side || yFromSouth >= Side) return null;
        int i = (Side - 1 - yFromSouth) * Side + x;
        return (Terrain.Metres(Heights[i]), Control[i]);
    }

    private byte[] Relief()
    {
        var pixels = new byte[Side * Side * 4];
        float step = Terrain.Size / Side, range = Math.Max(1, Terrain.Highest - Terrain.Lowest);
        Parallel.For(0, Side, y =>
        {
            for (int x = 0; x < Side; x++)
            {
                int i = y * Side + x;
                float h = Terrain.Metres(Heights[i]);
                float left = Terrain.Metres(Heights[y * Side + Math.Max(0, x - 1)]), right = Terrain.Metres(Heights[y * Side + Math.Min(Side - 1, x + 1)]);
                float up = Terrain.Metres(Heights[Math.Max(0, y - 1) * Side + x]), down = Terrain.Metres(Heights[Math.Min(Side - 1, y + 1) * Side + x]);
                // light from the north-west, a little above
                float dx = (right - left) / (2 * step), dy = (down - up) / (2 * step);
                float shade = Math.Clamp(0.65f + (-dx + dy) * 0.6f, 0.15f, 1.25f);
                var tint = Tint((h - Terrain.Lowest) / range, h < 0);
                pixels[i * 4] = (byte)Math.Clamp(tint.B * shade, 0, 255);
                pixels[i * 4 + 1] = (byte)Math.Clamp(tint.G * shade, 0, 255);
                pixels[i * 4 + 2] = (byte)Math.Clamp(tint.R * shade, 0, 255);
                pixels[i * 4 + 3] = 255;
            }
        });
        return pixels;
    }

    /// <summary>A height's colour: blues under the sea, then greens, browns and greys up to white.</summary>
    private static Color Tint(float t, bool underwater)
    {
        if (underwater) return Color.FromRgb(70, 110, 150);
        (float At, Color C)[] stops = [(0, Color.FromRgb(96, 140, 80)), (0.35f, Color.FromRgb(150, 160, 95)), (0.65f, Color.FromRgb(150, 120, 90)), (0.85f, Color.FromRgb(160, 155, 150)), (1, Color.FromRgb(240, 240, 240))];
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].At)
            {
                float k = (t - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
                var (a, b) = (stops[i - 1].C, stops[i].C);
                return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
            }
        return stops[^1].C;
    }

    private byte[] Textures()
    {
        var pixels = new byte[Side * Side * 4];
        for (int i = 0; i < Side * Side; i++)
        {
            int texture = TerrainLevel.Background(Control[i]);
            var c = texture == 0 ? Colors.Black : SimplexMap.ColorOf(texture);
            pixels[i * 4] = c.B;
            pixels[i * 4 + 1] = c.G;
            pixels[i * 4 + 2] = c.R;
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }
}
