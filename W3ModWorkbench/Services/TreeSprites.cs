using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench;

/// <summary>A picture's pixels (BGRA, straight alpha), for sampling.</summary>
public sealed record SpritePixels(byte[] Pixels, int Width, int Height)
{
    public static SpritePixels? Of(BitmapSource? picture)
    {
        if (picture is null || picture.PixelWidth == 0) return null;
        var source = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new SpritePixels(pixels, source.PixelWidth, source.PixelHeight);
    }
}

/// <summary>A plant seen from above: its picture, how far it reaches from its foot (metres, at scale 1), and its average colour.</summary>
public sealed record TreeSprite(BitmapSource Picture, float Radius, Color Colour);

/// <summary>
/// Pictures of SpeedTree plants from straight above, drawn from their most detailed LOD with their own pictures: each
/// triangle filled from its material's colour picture (leaves cut out by its alpha), the highest surface kept, and the
/// lower parts shaded darker so the crown reads as a shape. Grass stands upright, so it's seen looking down at a slant
/// instead, or its blades would be slivers.
/// </summary>
public static class TreeSprites
{
    public static TreeSprite? Render(SpeedTreeFile tree, Func<SpeedTreeMaterial, SpritePixels?> texture, int size, bool grass = false)
    {
        // looking down at a slant: each metre up moves a point this far up the picture
        float lean = grass ? 0.7f : 0;
        if (tree.Lods.Count == 0) return null;
        float radius = new[] { -tree.Min.X, -tree.Min.Y, tree.Max.X, tree.Max.Y + lean * tree.Max.Z }.Max();
        if (radius <= 0.01f) return null;
        float low = tree.Min.Z, span = MathF.Max(0.01f, tree.Max.Z - tree.Min.Z);
        var colour = new byte[size * size * 4];
        var depth = new float[size * size];
        Array.Fill(depth, float.NegativeInfinity);
        var pictures = tree.Materials.Select(texture).ToList();

        foreach (var draw in tree.Lods[0])
        {
            var material = tree.Materials[draw.Material];
            var picture = pictures[draw.Material];
            bool cutout = !material.Branches || material.Leaves || material.FacingLeaves || material.Fronds;
            // where each vertex lands: a leaf card facing the camera spreads out from its middle by its corner
            var at = new Vector3[draw.Positions.Length];
            for (int v = 0; v < at.Length; v++)
            {
                var p = draw.Positions[v];
                if (draw.Corners is { } corners) p += new Vector3(corners[v].X, corners[v].Y, 0);
                at[v] = new Vector3((p.X + radius) / (2 * radius) * size, (radius - p.Y - lean * p.Z) / (2 * radius) * size, p.Z - lean * p.Y);
            }
            for (int i = 0; i + 2 < draw.Indices.Length; i += 3)
                Triangle(draw, at, draw.Indices[i], draw.Indices[i + 1], draw.Indices[i + 2], picture, cutout, size, colour, depth, low, span);
        }

        long r = 0, g = 0, b = 0, count = 0;
        for (int i = 0; i < size * size; i++)
        {
            if (colour[i * 4 + 3] == 0) continue;
            b += colour[i * 4];
            g += colour[i * 4 + 1];
            r += colour[i * 4 + 2];
            count++;
        }
        if (count == 0) return null;
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, colour, size * 4);
        bitmap.Freeze();
        return new TreeSprite(bitmap, radius, Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count)));
    }

    private static void Triangle(SpeedTreeDraw draw, Vector3[] at, int ia, int ib, int ic, SpritePixels? picture, bool cutout, int size,
        byte[] colour, float[] depth, float low, float span)
    {
        Vector3 a = at[ia], b = at[ib], c = at[ic];
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        Vector2 ua = draw.Uvs[ia], ub = draw.Uvs[ib], uc = draw.Uvs[ic];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                float wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                float wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                float z = wa * a.Z + wb * b.Z + wc * c.Z;
                int i = y * size + x;
                if (z <= depth[i]) continue;
                byte cb = 70, cg = 90, cr = 60;
                if (picture is not null)
                {
                    var uv = wa * ua + wb * ub + wc * uc;
                    int tx = (int)((uv.X - MathF.Floor(uv.X)) * picture.Width) % picture.Width;
                    int ty = (int)((uv.Y - MathF.Floor(uv.Y)) * picture.Height) % picture.Height;
                    int t = (ty * picture.Width + tx) * 4;
                    if (cutout && picture.Pixels[t + 3] < 110) continue;
                    (cb, cg, cr) = (picture.Pixels[t], picture.Pixels[t + 1], picture.Pixels[t + 2]);
                }
                // lower parts darker, so a crown has shape from above
                float light = 0.55f + 0.45f * Math.Clamp((z - low) / span, 0, 1);
                depth[i] = z;
                colour[i * 4] = (byte)(cb * light);
                colour[i * 4 + 1] = (byte)(cg * light);
                colour[i * 4 + 2] = (byte)(cr * light);
                colour[i * 4 + 3] = 255;
            }
    }
}
