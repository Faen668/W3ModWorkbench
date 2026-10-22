using System.Numerics;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench;

/// <summary>
/// The game's own map of a world, finished where the game left it off. Its minimap covers only the middle of a world (the
/// part you can walk); the rest is painted here the way the map is made, from the terrain: each point the average colour
/// of the textures painted on it (the overlay on slopes past the point's threshold), the colour map laid over that and
/// the relief shaded, then matched to the game's map colour for colour (land and water each by a straight-line fit over
/// the part both have), and blended into it across the edge so the two read as one.
/// </summary>
public static class GameMapFill
{
    /// <summary>
    /// Fills in <paramref name="map"/> (BGRA, <paramref name="side"/> a side, rows from the north, alpha 0 where the
    /// game has no map) from the terrain; <paramref name="textures"/> is the terrain material's diffuse array, for its
    /// textures' colours. Returns how many pixels were painted.
    /// </summary>
    public static int Complete(byte[] map, int side, WorldTerrainPictures terrain, (TextureInfo Info, byte[] Pixels)? textures,
        IReadOnlyList<(Vector2 At, float Reach)>? trees = null)
    {
        var known = new bool[side * side];
        int missing = 0;
        for (int i = 0; i < known.Length; i++)
            if (!(known[i] = map[i * 4 + 3] > 0)) missing++;
        if (missing == 0 || missing == known.Length) return 0;

        // the game's map fades to a pale haze in its outer ring of tiles: only well inside its edge is it trusted
        var inside = Blur(known, side, Math.Max(16, side / 22));
        var core = new bool[side * side];
        for (int i = 0; i < core.Length; i++) core[i] = inside[i] > 0.999f;
        var colours = TextureColours(textures);
        var t = terrain.Terrain;
        int ts = terrain.Side;
        float spacing = t.Size / ts, range = t.Highest - t.Lowest;
        var settings = t.TextureParams;
        // the painted picture and whether each point is under water
        var painted = new Vector3[side * side];
        var water = new bool[side * side];
        Parallel.For(0, side, y =>
        {
            for (int x = 0; x < side; x++)
            {
                int tx = Math.Min(ts - 1, x * ts / side), ty = Math.Min(ts - 1, y * ts / side);
                float H(int ax, int ay) => t.Lowest + terrain.Heights[Math.Clamp(ay, 0, ts - 1) * ts + Math.Clamp(ax, 0, ts - 1)] / 65535f * range;
                float h = H(tx, ty);
                float dx = (H(tx + 1, ty) - H(tx - 1, ty)) / (2 * spacing), dy = (H(tx, ty - 1) - H(tx, ty + 1)) / (2 * spacing);
                ushort control = terrain.Control[ty * ts + tx];
                int back = Math.Max(control & 31, 1) - 1, over = Math.Max((control >> 5) & 31, 1) - 1;
                float threshold = ((control >> 10) & 7) / 8f;
                float falloff = back < settings.Count ? Math.Max(settings[back].Val.X, 0.05f) : 0.2f;
                float amount = Math.Clamp((MathF.Sqrt(dx * dx + dy * dy) - threshold) / falloff, 0, 1);
                var colour = Vector3.Lerp(colours[back], colours[over], amount);
                // the colour map laid over it, as the terrain shader does
                int c = (ty * ts + tx) * 4;
                var tint = new Vector3(terrain.Colour[c + 2], terrain.Colour[c + 1], terrain.Colour[c]) / 255;
                colour = new Vector3(Overlay(colour.X, tint.X), Overlay(colour.Y, tint.Y), Overlay(colour.Z, tint.Z));
                // the relief, lit from the north-west as maps are
                var normal = Vector3.Normalize(new Vector3(-dx, -dy, 1));
                float shade = Math.Clamp(Vector3.Dot(normal, Vector3.Normalize(new Vector3(-1, 1, 1.4f))), 0, 1);
                painted[y * side + x] = colour * (0.55f + 0.6f * shade);
                water[y * side + x] = h < -0.3f;
            }
        });

        // the game's map against the painting, land and water apart: a gain and an offset for each channel
        var land = Fit(map, painted, core, water, false);
        var sea = Fit(map, painted, core, water, true);
        // the trees, as the game's map paints them: dark crowns, their colour taken from the game's map under its own trees
        var t2 = terrain.Terrain;
        float perMetre = side / t2.Size;
        var treeMask = new float[side * side];
        if (trees is not null)
            foreach (var (at, reach) in trees)
            {
                float cx = (at.X - t2.Corner.X) * perMetre, cy = side - 1 - (at.Y - t2.Corner.Y) * perMetre, r = Math.Max(1, reach * 0.6f * perMetre);
                for (int y = (int)(cy - r); y <= (int)(cy + r); y++)
                    for (int x = (int)(cx - r); x <= (int)(cx + r); x++)
                    {
                        if (x < 0 || y < 0 || x >= side || y >= side) continue;
                        float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        treeMask[y * side + x] = Math.Max(treeMask[y * side + x], Math.Clamp(r + 0.5f - d, 0, 1));
                    }
            }
        var treeColour = Vector3.Zero;
        double treeCount = 0;
        for (int i = 0; i < treeMask.Length; i += 2)
            if (core[i] && treeMask[i] > 0.9f)
            {
                treeColour += new Vector3(map[i * 4 + 2], map[i * 4 + 1], map[i * 4]) / 255;
                treeCount++;
            }
        treeColour = treeCount > 50 ? treeColour / (float)treeCount : new Vector3(0.16f, 0.22f, 0.12f);
        // how near each point is to the game's map, for blending across its edge
        var near = Blur(core, side, Math.Max(16, side / 40));
        Parallel.For(0, side * side, i =>
        {
            var p = painted[i];
            var fit = water[i] ? sea : land;
            var fitted = Vector3.Clamp(Vector3.Transform(p, fit), Vector3.Zero, Vector3.One);
            if (!water[i]) fitted = Vector3.Lerp(fitted, treeColour, treeMask[i] * 0.85f);
            fitted *= 255;
            float keep = core[i] ? Math.Clamp(near[i] * 2 - 1, 0, 1) : 0;
            int o = i * 4;
            var game = new Vector3(map[o + 2], map[o + 1], map[o]);
            var mixed = Vector3.Lerp(fitted, game, keep);
            map[o] = (byte)mixed.Z;
            map[o + 1] = (byte)mixed.Y;
            map[o + 2] = (byte)mixed.X;
            map[o + 3] = 255;
        });
        return missing;
    }

    private static float Overlay(float c, float tint) => tint < 0.5f ? 2 * c * tint : 1 - 2 * (1 - c) * (1 - tint);

    /// <summary>Each of the 31 textures' average colour, from a small mip level of the diffuse array; grey for any not there.</summary>
    private static Vector3[] TextureColours((TextureInfo Info, byte[] Pixels)? textures)
    {
        var colours = Enumerable.Repeat(new Vector3(0.45f, 0.42f, 0.35f), 32).ToArray();
        if (textures is not { } array) return colours;
        int level = Math.Max(0, array.Info.Mips - 4);
        for (int slice = 0; slice < Math.Min(32, array.Info.Slices); slice++)
            if (TextureDecoder.Decode(array.Info, array.Pixels, level, slice) is { } image)
            {
                var sum = Vector3.Zero;
                for (int i = 0; i < image.Bgra.Length; i += 4) sum += new Vector3(image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i]);
                colours[slice] = sum / (image.Bgra.Length / 4) / 255;
            }
        return colours;
    }

    /// <summary>
    /// The least-squares colour matrix (each channel from all three and an offset) taking the painting to the game's map
    /// where both are, on land or water; the identity when there's too little to fit.
    /// </summary>
    private static Matrix4x4 Fit(byte[] map, Vector3[] painted, bool[] known, bool[] water, bool wet)
    {
        // normal equations: (XᵀX) B = XᵀY, X rows (r, g, b, 1)
        var xtx = new double[4, 4];
        var xty = new double[4, 3];
        int n = 0;
        for (int i = 0; i < painted.Length; i += 3)
        {
            if (!known[i] || water[i] != wet) continue;
            n++;
            double[] x = [painted[i].X, painted[i].Y, painted[i].Z, 1];
            double[] y = [map[i * 4 + 2] / 255.0, map[i * 4 + 1] / 255.0, map[i * 4] / 255.0];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++) xtx[r, c] += x[r] * x[c];
                for (int c = 0; c < 3; c++) xty[r, c] += x[r] * y[c];
            }
        }
        if (n < 200) return Matrix4x4.Identity;
        var solved = Solve(xtx, xty);
        if (solved is null) return Matrix4x4.Identity;
        // Vector3.Transform multiplies a row vector: M11..M31 take r, g, b into r, and M41 is the offset
        return new Matrix4x4(
            (float)solved[0, 0], (float)solved[0, 1], (float)solved[0, 2], 0,
            (float)solved[1, 0], (float)solved[1, 1], (float)solved[1, 2], 0,
            (float)solved[2, 0], (float)solved[2, 1], (float)solved[2, 2], 0,
            (float)solved[3, 0], (float)solved[3, 1], (float)solved[3, 2], 1);
    }

    /// <summary>A 4x4 system solved for three right-hand sides by Gaussian elimination, or null when it's singular.</summary>
    private static double[,]? Solve(double[,] a, double[,] b)
    {
        a = (double[,])a.Clone();
        b = (double[,])b.Clone();
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 4; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;
            for (int c = 0; c < 4; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            for (int c = 0; c < 3; c++) (b[col, c], b[pivot, c]) = (b[pivot, c], b[col, c]);
            for (int r = 0; r < 4; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                for (int c = 0; c < 4; c++) a[r, c] -= f * a[col, c];
                for (int c = 0; c < 3; c++) b[r, c] -= f * b[col, c];
            }
        }
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 3; c++) b[r, c] /= a[r, r];
        return b;
    }

    /// <summary>A mask's share of set points within <paramref name="radius"/> of each point (a box blur, both ways).</summary>
    private static float[] Blur(bool[] mask, int side, int radius)
    {
        var across = new float[side * side];
        Parallel.For(0, side, y =>
        {
            float sum = 0;
            for (int x = -radius; x < side; x++)
            {
                if (x + radius < side && mask[y * side + x + radius]) sum++;
                if (x - radius - 1 >= 0 && mask[y * side + x - radius - 1]) sum--;
                if (x >= 0) across[y * side + x] = sum;
            }
        });
        var result = new float[side * side];
        float area = (2 * radius + 1) * (2 * radius + 1);
        Parallel.For(0, side, x =>
        {
            float sum = 0;
            for (int y = -radius; y < side; y++)
            {
                if (y + radius < side) sum += across[(y + radius) * side + x];
                if (y - radius - 1 >= 0) sum -= across[(y - radius - 1) * side + x];
                if (y >= 0) result[y * side + x] = sum / area;
            }
        });
        return result;
    }
}
