using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// A shift of a colour's hue (in degrees), saturation and luminance (each -100 to 100), as an entity's colour entries
/// give a component's materials: the same mesh and textures in another colour for each appearance.
/// </summary>
public sealed record ColorShift(int Hue, int Saturation, int Luminance)
{
    /// <summary>A CColorShift from a file, or null for none or one that changes nothing.</summary>
    public static ColorShift? From(object? value)
    {
        if (value is not RedObject o) return null;
        static int Number(object? v) => v is null ? 0 : Convert.ToInt32(v);
        var shift = new ColorShift(Number(o["hue"]), Number(o["saturation"]), Number(o["luminance"]));
        return shift is { Hue: 0, Saturation: 0, Luminance: 0 } ? null : shift;
    }
}

/// <summary>
/// The colour texture a material is shown with, worked out as the game's shaders work it out, as far as a plain
/// texture on a mesh can show it. The sums come from the material graphs the shaders are built from (REDkit's
/// <c>engine\materials\graphs</c>), read once while writing this; nothing of REDkit is needed to run it.
/// <list type="bullet">
/// <item>Hair (<c>pbr_hair</c>, <c>pbr_hair_moving</c>, <c>pbr_hair_simple</c>): the grey texture, drawn toward its
/// VarianceColor by the graphs' own <c>customColoring</c> function, times the mesh's vertex colour, which is where a
/// character's hair colour is kept. The vertex colours of a material's chunks are averaged, as WPF can't blend them
/// over a texture, and hair keeps nearly one colour.</item>
/// <item>A component recoloured by its appearance (the <c>ShiftColorRB</c> block of the colour shift and tint mask
/// shaders), as the engine does it: each colour shift becomes a colour matrix (the luminance scaled, the hue turned
/// round the grey axis, the saturation scaled, as REDkit's <c>CColorShift</c> works it out), and the texture's colour,
/// as it's stored, is put through the first where it's redder and the second where it's bluer (or a blend of both,
/// with ColorShift_ BlendColors), as much as the tint mask's green says and, with ColorShift_ KeepGray, less where
/// it's grey. Nothing is shifted when ColorShift_Enabled is off. Read from REDkit's material compiler while writing
/// this.</item>
/// <item>The soft shadow over the eyes, which the game blends over them, is left out.</item>
/// </list>
/// </summary>
public static class LookTextures
{
    /// <summary>
    /// A material the game blends over what's under it, such as the soft shadow around the eyes: drawn solid it would
    /// cover the eyes, so it's left out.
    /// </summary>
    public static bool IsOverlay(MeshMaterial material) => material.Shader.Contains("eye_shadow", StringComparison.OrdinalIgnoreCase);

    private static bool IsHair(MeshMaterial material) => material.Shader.StartsWith("pbr_hair", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The colour entry an appearance gives a component: the entity's own first, then those of the templates it
    /// includes. Null when it has none.
    /// </summary>
    public static (ColorShift? First, ColorShift? Second)? Coloring(EntityFile entity, string? appearance, string component)
    {
        if (appearance is null) return null;
        var entries = entity.Template.Array("coloringEntries").OfType<RedObject>().Concat(entity.IncludedColorings.Select(c => c.Entry));
        foreach (var entry in entries)
            if (string.Equals(entry["appearance"] as string, appearance, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry["componentName"] as string, component, StringComparison.OrdinalIgnoreCase))
            {
                var (first, second) = (ColorShift.From(entry["colorShift1"]), ColorShift.From(entry["colorShift2"]));
                return first is null && second is null ? null : (first, second);
            }
        return null;
    }

    /// <summary>
    /// The picture to show a mesh's material with, or null for none; and whether to leave its chunks out. Reads from
    /// disk, so it belongs off the UI thread. <paramref name="texture"/> reads a texture, as the caller keeps them.
    /// </summary>
    public static async Task<(BitmapSource? Picture, bool LeaveOut)> ForMaterial(MeshFile mesh, int material, ResourceLocator locator,
        Func<string, Task<BitmapSource?>> texture, (ColorShift? First, ColorShift? Second)? coloring = null)
    {
        var resolved = MeshMaterial.Resolve(mesh.File, mesh.Materials[material], locator.ReadFile);
        if (IsOverlay(resolved)) return (null, true);
        if (resolved.Diffuse is not { } diffuse || await texture(diffuse.Path) is not { } picture) return (null, false);

        var paint = new Paint();
        if (IsHair(resolved))
        {
            paint.Vertex = VertexColor(mesh, material);
            if (Parameter(resolved, "VarianceColor") is ColorValue variance)
                paint.Variance = (variance.R / 255f, variance.G / 255f, variance.B / 255f, Parameter(resolved, "VarianceOffset") as float? ?? 0);
        }
        // a colour entry recolours a material that can be: one with a tint mask. ColorShift_Enabled says whether the
        // mask is used; without it the whole texture is shifted (as Ciri's bath towel is, white in the game)
        var tintMask = resolved.Textures.FirstOrDefault(t => t.Parameter.Equals("TintMask", StringComparison.OrdinalIgnoreCase));
        if (coloring is { } shifts && tintMask is not null && Parameter(resolved, "ColorShift_Enabled") is float and > 0
            && await texture(tintMask.Path) is { } mask)
        {
            paint.Shifting = true;
            paint.Mask = mask;
            // the first shift goes on the red parts and the second on the blue, as the engine hands them to the shader.
            // The matrices are made for a red colour, so the blue parts have their red and blue swapped on the way in:
            // through the same shift, red and blue parts come out alike (dye_blue makes both blue)
            paint.Red = ShiftMatrix(shifts.First);
            paint.Blue = SwapRedBlue * ShiftMatrix(shifts.Second);
            paint.KeepGray = Parameter(resolved, "ColorShift_ KeepGray") is float and > 0;
            paint.BlendColors = Parameter(resolved, "ColorShift_ BlendColors") is float and > 0;
        }
        var shown = paint.Does ? paint.Apply(picture) : picture;
        // hair is cards cut out by the texture's alpha, as the game tests it: drawn whole they're solid sheets
        if (IsHair(resolved) && MeshTextures.LoadWithAlpha(locator, diffuse.Path, Math.Max(shown.PixelWidth, shown.PixelHeight)) is { } alpha)
            shown = CutOut(shown, alpha);
        return (shown, false);
    }

    /// <summary>A picture with the parts another's alpha leaves out made transparent (under a fifth: a strand's soft edges count, as layered cards fill a beard out in the game), the rest solid.</summary>
    private static BitmapSource CutOut(BitmapSource picture, BitmapSource alphaFrom)
    {
        var colour = picture.Format == PixelFormats.Bgr32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgr32, null, 0);
        int width = colour.PixelWidth, height = colour.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        colour.CopyPixels(pixels, stride, 0);
        BitmapSource scaled = alphaFrom.PixelWidth == width && alphaFrom.PixelHeight == height ? alphaFrom
            : new TransformedBitmap(alphaFrom, new ScaleTransform((double)width / alphaFrom.PixelWidth, (double)height / alphaFrom.PixelHeight));
        var alpha = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        if (alpha.PixelWidth != width || alpha.PixelHeight != height) return picture;
        var mask = new byte[stride * height];
        alpha.CopyPixels(mask, stride, 0);
        bool any = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            bool keep = mask[i] >= 51;
            any |= !keep;
            // premultiplied: a left-out pixel is all zero
            if (keep) pixels[i] = 255;
            else pixels[i - 3] = pixels[i - 2] = pixels[i - 1] = pixels[i] = 0;
        }
        if (!any) return picture;
        var cut = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        cut.Freeze();
        return cut;
    }

    private static object? Parameter(MeshMaterial material, string name) =>
        material.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The average vertex colour of a material's chunks (its most detailed ones), from 0 to 1; null when they have none.</summary>
    private static (float R, float G, float B)? VertexColor(MeshFile mesh, int material)
    {
        var chunks = mesh.Chunks.Where(c => c.Material == material && c.Colors.Length > 0).ToList();
        var detailed = chunks.Where(c => c.InLod(0)).ToList();
        if (detailed.Count > 0) chunks = detailed;
        long r = 0, g = 0, b = 0, n = 0;
        foreach (var colors in chunks.Select(c => c.Colors))
            for (int i = 0; i + 2 < colors.Length; i += 3, n++)
                (r, g, b) = (r + colors[i], g + colors[i + 1], b + colors[i + 2]);
        return n == 0 ? null : (r / (255f * n), g / (255f * n), b / (255f * n));
    }

    /// <summary>What's done to a texture's pixels, in the order the shaders do it.</summary>
    private sealed class Paint
    {
        public (float R, float G, float B, float Offset)? Variance;
        public (float R, float G, float B)? Vertex;
        public bool Shifting;
        public BitmapSource? Mask;
        public Matrix4x4 Red = Matrix4x4.Identity, Blue = Matrix4x4.Identity;
        public bool KeepGray, BlendColors;

        public bool Does => Variance is not null || Vertex is not null || Shifting;

        public BitmapSource Apply(BitmapSource picture)
        {
            var source = picture.Format == PixelFormats.Bgr32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgr32, null, 0);
            int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);
            byte[]? mask = null;
            if (Mask is not null)
            {
                // the mask at the picture's size, so each pixel has its own
                var scaled = new FormatConvertedBitmap(new TransformedBitmap(Mask, new ScaleTransform((double)width / Mask.PixelWidth, (double)height / Mask.PixelHeight)), PixelFormats.Bgr32, null, 0);
                if (scaled.PixelWidth == width && scaled.PixelHeight == height)
                {
                    mask = new byte[stride * height];
                    scaled.CopyPixels(mask, stride, 0);
                }
            }
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                // the shaders work in linear light, as the texture is read; shown again as the screen takes it
                float r = Linear(pixels[i + 2]), g = Linear(pixels[i + 1]), b = Linear(pixels[i]);
                if (Variance is { } v) (r, g, b) = CustomColoring(v.Offset, r, g, b, v.R, v.G, v.B);
                if (Vertex is { } c) (r, g, b) = (r * c.R, g * c.G, b * c.B);
                if (Shifting) (r, g, b) = ShiftColor(r, g, b, mask is null ? 1 : mask[i + 1] / 255f);
                pixels[i + 2] = Screen(r);
                pixels[i + 1] = Screen(g);
                pixels[i] = Screen(b);
            }
            var painted = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            painted.Freeze();
            return painted;
        }

        /// <summary>
        /// The <c>ShiftColorRB</c> block, as REDkit's material compiler writes it, on a colour as it's stored (the graphs
        /// turn it from linear light first): how red and how blue it is picks the matrix, the matrix gives the shifted
        /// colour, and the mask (less a grey, with KeepGray) says how far toward it to go. Thresholds are the block's own.
        /// </summary>
        private (float, float, float) ShiftColor(float r, float g, float b, float mask)
        {
            const float ColorLow = 0.3f, ColorHigh = 1, SaturationLow = 0.1f, SaturationHigh = 0.3f;
            var c = new Vector3(ScreenF(r), ScreenF(g), ScreenF(b));
            Matrix4x4 m;
            if (BlendColors)
            {
                // the red and blue shares, each from the threshold up, as weights for the two matrices
                float sum = c.X + c.Z;
                float red = sum > 0 ? Math.Clamp((c.X / sum - ColorLow) / (ColorHigh - ColorLow), 0, 1) : 0;
                float blue = sum > 0 ? Math.Clamp((c.Z / sum - ColorLow) / (ColorHigh - ColorLow), 0, 1) : 0;
                float toBlue = red + blue > 0 ? blue / (red + blue) : 0;
                m = Red * (1 - toBlue) + Blue * toBlue;
            }
            else m = c.X >= c.Z ? Red : Blue;
            var shifted = Vector3.Transform(c, m);
            float amount = mask;
            if (KeepGray)
            {
                float most = Math.Max(c.X, Math.Max(c.Y, c.Z)), least = Math.Min(c.X, Math.Min(c.Y, c.Z));
                float saturation = most > 0 ? (most - least) / most : 0;
                amount *= Math.Clamp((saturation - SaturationLow) / (SaturationHigh - SaturationLow), 0, 1);
            }
            var result = Vector3.Lerp(c, shifted, amount);
            return (LinearF(result.X), LinearF(result.Y), LinearF(result.Z));
        }
    }

    /// <summary>
    /// The graphs' <c>customColoring</c> function, as they have it: the colour drawn toward the texture times its
    /// VarianceColor, as much as VarianceOffset says, more for light parts than dark when it's below 1.
    /// </summary>
    private static (float, float, float) CustomColoring(float offset, float r, float g, float b, float br, float bg, float bb)
    {
        float a = (r + g + b) * 0.3333f;
        float inverse = 0.5f * (offset - 1);
        float blend = a + (1 - a - a) * Math.Clamp(inverse, 0, 1);
        float t = Math.Clamp(blend * Math.Abs(inverse), 0, 1);
        static float Lerp(float x, float y, float t) => x + (y - x) * t;
        return (Lerp(r, Math.Clamp(1.5f * r * br, 0, 1), t), Lerp(g, Math.Clamp(1.5f * g * bg, 0, 1), t), Lerp(b, Math.Clamp(1.5f * b * bb, 0, 1), t));
    }

    private static (float R, float G, float B) Linear3((float R, float G, float B) c) => (LinearF(c.R), LinearF(c.G), LinearF(c.B));

    private static readonly float[] ToLinear = [.. Enumerable.Range(0, 256).Select(i => LinearF(i / 255f))];

    private static float Linear(byte value) => ToLinear[value];

    private static float LinearF(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ScreenF(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(Math.Max(c, 0), 1 / 2.4f) - 0.055f;

    private static byte Screen(float c) => (byte)Math.Clamp(ScreenF(c) * 255 + 0.5f, 0, 255);

    /// <summary>Swaps a colour's red and blue, so a blue placeholder goes through a shift made for a red one.</summary>
    private static readonly Matrix4x4 SwapRedBlue = new(0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);

    // the engine turns the hue round the grey axis in a space of its own: these take a colour there and back
    private static readonly Matrix4x4 ToHueSpace = new(0.8165f, -0.4082f, 0.4082f, 0, 0, 0.7071f, 0.7071f, 0, -0.8539f, -0.7405f, 0.1377f, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 FromHueSpace = new(0.621f, -0.2461f, -0.5774f, 0, -0.6038f, 0.461f, -0.5774f, 0, 0.6038f, 0.9532f, 0.5774f, 0, 0, 0, 0, 1);

    /// <summary>
    /// The colour matrix a shift makes, as REDkit's <c>CColorShift</c> works it out: the luminance scaled by
    /// 1 + luminance/100, the hue turned by its degrees, and the saturation scaled by 1 + saturation/100 toward the
    /// colour's grey, for a colour taken as a row (<see cref="Vector3.Transform(Vector3, Matrix4x4)"/>). Checked against
    /// REDkit's own function, run on the same shifts. It's made for a red colour: a dye's shifts turn the red
    /// placeholder into the colour the dye is named for. A shift that isn't there changes nothing.
    /// </summary>
    internal static Matrix4x4 ShiftMatrix(ColorShift? shift)
    {
        if (shift is null) return Matrix4x4.Identity;
        float saturation = 1 + shift.Saturation / 100f, luminance = 1 + shift.Luminance / 100f;
        float other = (1 - saturation) / 3, same = other + saturation;
        var saturate = new Matrix4x4(same, other, other, 0, other, same, other, 0, other, other, same, 0, 0, 0, 0, 1);
        var lighten = Matrix4x4.CreateScale(luminance, luminance, luminance);
        float angle = shift.Hue * MathF.PI / 180, cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        var turn = new Matrix4x4(cos, -sin, 0, 0, sin, cos, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        return lighten * ToHueSpace * turn * FromHueSpace * saturate;
    }
}
