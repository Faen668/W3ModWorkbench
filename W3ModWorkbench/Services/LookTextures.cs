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
/// shaders): the texture's red parts take the colour entry's first shift and its blue parts the second, as much as
/// the tint mask's green says (or all of it, when the material's ColorShift_Enabled is off). The block itself is built into the engine, so how far each part is shifted is near
/// the game's rather than exact.</item>
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
        if (coloring is { } shifts && tintMask is not null)
        {
            paint.Shifting = true;
            paint.Shifts = shifts;
            paint.KeepGray = Parameter(resolved, "ColorShift_ KeepGray") is float and > 0;
            if (Parameter(resolved, "ColorShift_Enabled") is float and > 0) paint.Mask = await texture(tintMask.Path);
        }
        return (paint.Does ? paint.Apply(picture) : picture, false);
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
        public (ColorShift? First, ColorShift? Second) Shifts;
        public bool KeepGray;

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
        /// The <c>ShiftColorRB</c> block as near as it can be told from outside: the red of a colour takes the first
        /// shift and its blue the second, a grey (with KeepGray) neither, as much as the mask says.
        /// </summary>
        private (float, float, float) ShiftColor(float r, float g, float b, float mask)
        {
            if (mask <= 0) return (r, g, b);
            float red = Math.Max(0, r - Math.Max(g, b)), blue = Math.Max(0, b - Math.Max(r, g)), colour = red + blue;
            float amount = mask * (KeepGray ? Math.Clamp(colour * 4, 0, 1) : 1);
            if (amount <= 0) return (r, g, b);
            float first = colour > 0 ? red / colour : 1;
            // shifted in linear light, as the shader has the colour when it shifts it
            var one = LookTextures.Apply(r, g, b, Shifts.First);
            var two = LookTextures.Apply(r, g, b, Shifts.Second);
            float Mix(float original, float x, float y) => original + (x * first + y * (1 - first) - original) * amount;
            return (Mix(r, one.R, two.R), Mix(g, one.G, two.G), Mix(b, one.B, two.B));
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

    private static readonly float[] ToLinear = [.. Enumerable.Range(0, 256).Select(i => LinearF(i / 255f))];

    private static float Linear(byte value) => ToLinear[value];

    private static float LinearF(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ScreenF(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(Math.Max(c, 0), 1 / 2.4f) - 0.055f;

    private static byte Screen(float c) => (byte)Math.Clamp(ScreenF(c) * 255 + 0.5f, 0, 255);

    /// <summary>One shift on a colour, in hue, saturation and lightness; a colour with no shift is left as it is.</summary>
    internal static (float R, float G, float B) Apply(float r, float g, float b, ColorShift? shift)
    {
        if (shift is null) return (r, g, b);
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float l = (max + min) / 2, d = max - min;
        float s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        float h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        h = ((h + shift.Hue) % 360 + 360) % 360;
        // saturation toward none or full by the share given, so -100 takes all the colour out and 100 makes it as strong
        // as it goes; lightness scaled by it, which keeps the texture's own light and dark (a towel's dirt and weave)
        // where drawing it toward white would wash them out
        float By(float value, int percent) => percent < 0 ? value * (1 + percent / 100f) : value + (1 - value) * percent / 100f;
        s = Math.Clamp(By(s, shift.Saturation), 0, 1);
        l = Math.Clamp(l * (1 + shift.Luminance / 100f), 0, 1);
        float c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r1, g1, b1) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return (r1 + m, g1 + m, b1 + m);
    }
}
