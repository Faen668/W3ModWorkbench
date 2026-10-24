using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Particles;

/// <summary>What an evaluator gives: a constant, a random value between two, a straight line from start to end, or curves.</summary>
public enum EvaluatorKind
{
    Constant,
    Random,
    StartEnd,
    Curve,
}

/// <summary>
/// A value a particle module reads at a time (the particle's life from 0 to 1, or the emitter's), an IEvaluator of the
/// engine's: a float, a vector (with how many of its axes are free: one for all, two, or three) or a colour (0 to 1 a
/// channel). A curve evaluator keeps a CCurve per axis, or, cooked, the same curves sampled evenly.
/// </summary>
public sealed class ParticleEvaluator
{
    public required RedObject Object { get; init; }
    public required EvaluatorKind Kind { get; init; }
    /// <summary>1 for a float, 3 or 4 for a vector, 4 for a colour.</summary>
    public required int Channels { get; init; }
    public bool IsColor { get; init; }
    /// <summary>The constant, the low end of a random value, or the start.</summary>
    public Vector4 A { get; init; }
    /// <summary>The high end of a random value, or the end.</summary>
    public Vector4 B { get; init; }
    /// <summary>A curve evaluator's curve for each axis.</summary>
    public IReadOnlyList<RedCurve> Curves { get; init; } = [];
    /// <summary>How many of a vector's axes are free: 1 (X for all three), 2 or 3.</summary>
    public int FreeAxes { get; init; } = 3;

    public string ClassName => Object.ClassName;

    /// <summary>The value at a time (0 to 1), for a random one at a place between its ends (0 to 1 an axis).</summary>
    public Vector4 Value(float time, Vector4 random)
    {
        var v = Kind switch
        {
            EvaluatorKind.Constant => A,
            EvaluatorKind.Random => A + (B - A) * random,
            EvaluatorKind.StartEnd => Vector4.Lerp(A, B, Math.Clamp(time, 0, 1)),
            _ => new Vector4(Curve(0, time), Curve(1, time), Curve(2, time), Curve(3, time)),
        };
        return Free(v);
    }

    private float Curve(int axis, float time)
    {
        if (Curves.Count == 0) return 0;
        // one curve for all axes when there's only one
        return Curves[Math.Min(axis, Curves.Count - 1)].At(time);
    }

    /// <summary>A vector with its fixed axes taken from the free ones: one free axis is X for all three.</summary>
    private Vector4 Free(Vector4 v) => Channels < 3 || FreeAxes >= 3 ? v : FreeAxes == 1 ? new Vector4(v.X, v.X, v.X, v.W) : new Vector4(v.X, v.Y, v.Y, v.W);

    /// <summary>The value at a time as a float: X.</summary>
    public float Float(float time, float random) => Value(time, new Vector4(random)).X;

    /// <summary>The evaluator in words: "0.5", "1 to 2 at random", "0 → 1", "curve".</summary>
    public string Describe()
    {
        string V(Vector4 v) => Channels == 1 ? Number(v.X) : IsColor ? $"{v.X * 255:0} {v.Y * 255:0} {v.Z * 255:0} α{v.W:0.##}"
            : FreeAxes == 1 ? Number(v.X) : FreeAxes == 2 ? $"{Number(v.X)}, {Number(v.Y)}" : $"{Number(v.X)}, {Number(v.Y)}, {Number(v.Z)}";
        return Kind switch
        {
            EvaluatorKind.Constant => V(A),
            EvaluatorKind.Random => L.T("ParticleSystemFile.RandomBetween", V(A), V(B)),
            EvaluatorKind.StartEnd => $"{V(A)} → {V(B)}",
            _ => L.T("ParticleSystemFile.CurveOf", L.T("Count.Key", Curves.Count == 0 ? 0 : Curves.Max(c => c.Keys.Count))),
        };
    }

    private static string Number(float v) => v.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// The evaluator an object is, or null when it isn't one. A curve evaluator keeps its curves after its properties:
    /// a byte, how many, then either that many object numbers (CCurve exports, the editor's form) or, flagged 1 after
    /// how many, each curve sampled (a u16 how many, then the floats), as the game's cooked files keep them.
    /// </summary>
    public static ParticleEvaluator? Read(Cr2wFile file, RedObject o)
    {
        string c = o.ClassName;
        if (!c.StartsWith("CEvaluator", StringComparison.Ordinal)) return null;
        bool color = c.StartsWith("CEvaluatorColor", StringComparison.Ordinal);
        int channels = color ? 4 : c.StartsWith("CEvaluatorVector", StringComparison.Ordinal) ? 3 : 1;
        int free = (o["freeAxes"]?.ToString()) switch { "FVA_One" => 1, "FVA_Two" => 2, _ => 3 };
        Vector4 Get(string name)
        {
            object? value = o[name];
            return value switch
            {
                float f => new Vector4(f),
                RedObject { ClassName: "Color" } col => new Vector4(col.Float("Red"), col.Float("Green"), col.Float("Blue"), col.Float("Alpha")) / 255f,
                RedObject vec => new Vector4(vec.Float("X"), vec.Float("Y"), vec.Float("Z"), vec.Float("W")),
                null when color => Vector4.One,
                _ => Vector4.Zero,
            };
        }
        if (c.EndsWith("Const", StringComparison.Ordinal) || c == "CEvaluatorColorRandom")
            return new ParticleEvaluator { Object = o, Kind = EvaluatorKind.Constant, Channels = channels, IsColor = color, A = Get("value"), FreeAxes = free };
        if (c.EndsWith("RandomUniform", StringComparison.Ordinal) || c.EndsWith("RandomRange", StringComparison.Ordinal))
            return new ParticleEvaluator { Object = o, Kind = EvaluatorKind.Random, Channels = channels, IsColor = color, A = Get("min"), B = Get("max"), FreeAxes = free };
        if (c.EndsWith("StartEnd", StringComparison.Ordinal))
            return new ParticleEvaluator { Object = o, Kind = EvaluatorKind.StartEnd, Channels = channels, IsColor = color, A = Get("start"), B = Get("end"), FreeAxes = free };
        if (c.EndsWith("Curve", StringComparison.Ordinal))
            return new ParticleEvaluator { Object = o, Kind = EvaluatorKind.Curve, Channels = channels, IsColor = color, Curves = TailCurves(file, o.Tail), FreeAxes = free };
        return null;
    }

    private static List<RedCurve> TailCurves(Cr2wFile file, byte[] tail)
    {
        var curves = new List<RedCurve>();
        if (tail.Length < 4) return curves;
        int count = tail[2];
        if (tail[3] == 1)
        {
            // sampled evenly over 0 to 1
            int at = 4;
            for (int i = 0; i < count && at + 2 <= tail.Length; i++)
            {
                int samples = BitConverter.ToUInt16(tail, at);
                at += 2;
                var keys = new List<CurveKey>();
                for (int s = 0; s < samples && at + 4 <= tail.Length; s++, at += 4)
                    keys.Add(new CurveKey(samples > 1 ? s / (float)(samples - 1) : 0, new Vector4(BitConverter.ToSingle(tail, at), 0, 0, 0), default, default, CurveSegment.Interpolate, CurveSegment.Interpolate));
                curves.Add(new RedCurve { Kind = CurveKind.Float, Keys = keys });
            }
            return curves;
        }
        for (int i = 0; i < count && 4 + 4 * i + 4 <= tail.Length; i++)
        {
            int handle = BitConverter.ToInt32(tail, 4 + 4 * i);
            if (handle > 0 && handle <= file.Exports.Count && RedCurve.FromCurveTail(file.Object(handle - 1).Tail) is { } curve) curves.Add(curve);
        }
        return curves;
    }
}

/// <summary>A module of an emitter: an initializer (sets a particle up when it's born) or a modificator (changes it as it lives).</summary>
public sealed class ParticleModule
{
    public required RedObject Object { get; init; }
    public required string ClassName { get; init; }
    public required bool Enabled { get; set; }
    /// <summary>Its values, by property, in the order it has them.</summary>
    public required IReadOnlyList<(string Name, ParticleEvaluator Evaluator)> Values { get; init; }

    public bool IsInitializer => ClassName.StartsWith("CParticleInitializer", StringComparison.Ordinal);

    /// <summary>The module's name in words, as REDkit's emitter boxes list them: "Size over life", "Initial velocity".</summary>
    public string Title => ParticleSystemFile.ModuleTitle(ClassName);

    public ParticleEvaluator? this[string name] => Values.FirstOrDefault(v => v.Name == name).Evaluator;

    public bool Flag(string name, bool otherwise = false) => Object[name] is bool b ? b : otherwise;
}

/// <summary>A burst of an emitter: so many particles at a time, again every so often when it repeats.</summary>
public readonly record struct ParticleBurst(int Count, float Time, float Spread, float Repeat);

/// <summary>
/// An emitter of a particle system: its name and colour in the editor and where its box is, its material (a shader and
/// its texture, an atlas of frames), how particles are drawn, how many at most, how fast they're born (by LOD) and in
/// bursts, how long it runs, and its modules.
/// </summary>
public sealed class ParticleEmitter
{
    public required RedObject Object { get; init; }
    public required string Name { get; init; }
    public required System.Windows.Media.Color EditorColor { get; init; }
    public required Vector2 GraphPosition { get; init; }
    public required RedObject? Material { get; init; }
    public required string? Shader { get; init; }
    /// <summary>Its material's parameters: the instance's own over its shader's defaults.</summary>
    public required IReadOnlyList<Meshes.MaterialParameter> Parameters { get; init; }
    public required string? Texture { get; init; }
    /// <summary>The texture's frames across and down, for an atlas the particles flip through.</summary>
    public required (int Columns, int Rows) Atlas { get; init; }
    /// <summary>Whether its shader adds its light to what's behind (fire, sparks, glow) rather than covering it.</summary>
    public required bool Additive { get; init; }
    public required System.Windows.Media.Color Tint { get; init; }
    public required string Drawer { get; init; }
    public required int MaxParticles { get; init; }
    public required bool Showing { get; set; }

    /// <summary>Whether its particles move with the effect (keepSimulationLocal), rather than staying where they were born.</summary>
    public bool KeepLocal => Object["keepSimulationLocal"] is true;
    public required IReadOnlyList<ParticleModule> Modules { get; init; }
    /// <summary>Its birth rate (particles a second over the emitter's time) for each LOD, null for none (bursts only).</summary>
    public required IReadOnlyList<ParticleEvaluator?> BirthRates { get; init; }
    /// <summary>Whether it plays at each LOD.</summary>
    public required IReadOnlyList<bool> LodsOn { get; init; }
    public required IReadOnlyList<IReadOnlyList<ParticleBurst>> Bursts { get; init; }
    /// <summary>How long one run lasts (0 for unbounded), how long before it starts, and how many runs (0 for for ever).</summary>
    public required float Duration { get; init; }
    public required float Delay { get; init; }
    public required int Loops { get; init; }
    public required bool Local { get; init; }
    public required string? ColorGroup { get; init; }
    /// <summary>Whether it was cooked: its modules compiled into data of the game's own, which this doesn't read.</summary>
    public bool Cooked { get; init; }

    public ParticleModule? Module(string className) => Modules.FirstOrDefault(m => m.ClassName == className && m.Enabled);
}

/// <summary>
/// A particle system (.w2p, a CParticleSystem): an effect made of emitters, each pouring out particles shaped by its
/// modules and drawn with its material. REDkit's particle editor shows it playing, each emitter as a coloured box of
/// its modules, and a module's curves.
/// </summary>
public sealed class ParticleSystemFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required IReadOnlyList<ParticleEmitter> Emitters { get; init; }
    /// <summary>The distance each LOD starts at.</summary>
    public required IReadOnlyList<float> LodDistances { get; init; }
    public required float Prewarm { get; init; }
    public required System.Windows.Media.Color? Background { get; init; }

    public static bool IsParticleSystem(string path) => path.EndsWith(".w2p", StringComparison.OrdinalIgnoreCase);

    public static ParticleSystemFile Read(byte[] data, Func<string, byte[]?>? readFile = null)
    {
        var file = Cr2wFile.Read(data);
        var system = file.First("CParticleSystem") ?? throw new InvalidDataException(L.T("ParticleSystemFile.NotParticleSystem"));
        var emitters = new List<ParticleEmitter>();
        foreach (var handle in system.Array("emitters").OfType<RedHandle>())
            if (file.Object(handle) is { } emitter) emitters.Add(Emitter(file, emitter, readFile));
        var lods = system.Array("lods").OfType<RedObject>().Select(l => l.Float("distance")).ToList();
        return new ParticleSystemFile
        {
            File = file,
            Resource = system,
            Emitters = emitters,
            LodDistances = lods.Count > 0 ? lods : [0],
            Prewarm = system.Float("prewarmingTime"),
            Background = system.Struct("previewBackgroundColor") is { } c ? Color(c) : null,
        };
    }

    private static System.Windows.Media.Color Color(RedObject c) =>
        System.Windows.Media.Color.FromArgb((byte)c.Integer("Alpha", 255), (byte)c.Integer("Red"), (byte)c.Integer("Green"), (byte)c.Integer("Blue"));

    private static ParticleEmitter Emitter(Cr2wFile file, RedObject e, Func<string, byte[]?>? readFile)
    {
        var modules = new List<ParticleModule>();
        foreach (var handle in e.Array("modules").OfType<RedHandle>())
            if (file.Object(handle) is { } m)
                modules.Add(new ParticleModule
                {
                    Object = m,
                    ClassName = m.ClassName,
                    Enabled = m["isEnabled"] is not false,
                    Values = m.Properties.Where(p => p.Value is RedHandle).Select(p => (p.Name, file.Object((RedHandle)p.Value!)))
                        .Where(p => p.Item2 is not null).Select(p => (p.Name, ParticleEvaluator.Read(file, p.Item2!)))
                        .Where(p => p.Item2 is not null).Select(p => (p.Name, p.Item2!)).ToList(),
                });
        var lods = e.Array("lods").OfType<RedObject>().ToList();
        var birthRates = lods.Select(l => l["birthRate"] is RedHandle h && file.Object(h) is { } o ? ParticleEvaluator.Read(file, o) : null).ToList();
        var lodsOn = lods.Select(l => l["isEnabled"] is not false).ToList();
        if (birthRates.Count == 0 && e["birthRate"] is RedHandle old && file.Object(old) is { } rate) birthRates.Add(ParticleEvaluator.Read(file, rate));
        var bursts = lods.Select(l => (IReadOnlyList<ParticleBurst>)Bursts(l.Array("burstList"))).ToList();
        if (bursts.Count == 0) bursts.Add(Bursts(e.Array("burstList")));
        var first = lods.FirstOrDefault() ?? e;

        // the material: its shader, how it blends, its texture and atlas, the instance's own values over its shader's
        var material = e["material"] is RedHandle mh ? file.Object(mh) : null;
        var resolved = material is not null && e["material"] is RedHandle materialHandle && readFile is not null ? Meshes.MeshMaterial.Resolve(file, materialHandle, readFile) : null;
        var parameters = resolved?.Parameters ?? [];
        string? shader = material?["baseMaterial"] is RedHandle sh ? file.PathOf(sh) : null;
        string? graph = resolved?.Bases.LastOrDefault(b => b.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase)) ?? shader;
        var textures = parameters.Where(p => p.Value is Meshes.TextureRef { Path: { Length: > 0 } t } && t.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(p.Name, "normal|distortion|noise|mask|cube|ramp", RegexOptions.IgnoreCase)).ToList();
        string? texture = (textures.FirstOrDefault(p => Regex.IsMatch(p.Name, "^(diffuse|tex|texture|subuv_texture)$", RegexOptions.IgnoreCase)) ?? textures.FirstOrDefault())?.Value is Meshes.TextureRef r ? r.Path : null;
        var graphFile = graph is not null && readFile?.Invoke(graph) is { } graphData ? Cr2wFile.Read(graphData) : null;
        // the atlas: the material's parameters, else the frames the shader's texture sampler is set to
        var sampler = graphFile?.Exports.Where(x => x.ClassName == "CMaterialBlockSamplerTexture").Select(x => graphFile.Object(x.Index))
            .FirstOrDefault(o => o.Integer("subUVWidth") > 1 || o.Integer("subUVHeight") > 1);
        int Atlas(string axis) => parameters.FirstOrDefault(p => Regex.IsMatch(p.Name, $"^sub_?u+_?v.*{axis}", RegexOptions.IgnoreCase)) is { Value: float f } && f >= 1 ? (int)f
            : (int)Math.Max(1, sampler?.Integer(axis == "w" ? "subUVWidth" : "subUVHeight", 1) ?? 1);
        var tint = parameters.FirstOrDefault(p => p.Name is "color" or "colour" or "color_multiply" && p.Value is Meshes.ColorValue)?.Value is Meshes.ColorValue tc
            ? System.Windows.Media.Color.FromArgb(tc.A, tc.R, tc.G, tc.B) : System.Windows.Media.Colors.White;
        bool additive = graph is not null && (graphFile?.First("CMaterialGraph")?["blendMode"]?.ToString() is { } blend
            ? blend.Contains("Add", StringComparison.OrdinalIgnoreCase)
            : Regex.IsMatch(Path.GetFileName(graph), "add|glow|fire|spark", RegexOptions.IgnoreCase));

        var drawer = e["particleDrawer"] is RedHandle dh ? file.Object(dh)?.ClassName ?? "" : "";
        var editor = e.Struct("editorColor");
        return new ParticleEmitter
        {
            Object = e,
            Name = e["editorName"] as string ?? L.T("ParticleSystemFile.Emitter"),
            EditorColor = editor is null ? System.Windows.Media.Color.FromRgb(0x4C, 0x8F, 0xC4) : Color(editor),
            GraphPosition = new Vector2(e.Float("positionX"), e.Float("positionY")),
            Material = material,
            Shader = graph,
            Parameters = parameters,
            Texture = texture,
            Atlas = (Atlas("w"), Atlas("h")),
            Additive = additive,
            Tint = tint,
            Drawer = drawer,
            MaxParticles = (int)e.Integer("maxParticles", 100),
            Showing = e["isShowing"] is not false && e["isEnabled"] is not false,
            Modules = modules,
            BirthRates = birthRates,
            LodsOn = lodsOn,
            Bursts = bursts,
            Duration = (first.Struct("emitterDurationSettings") ?? e.Struct("emitterDurationSettings"))?.Float("emitterDuration") ?? e.Float("emitterDuration"),
            Delay = (first.Struct("emitterDelaySettings") ?? e.Struct("emitterDelaySettings"))?.Float("emitterDelay") ?? 0,
            Loops = (int)e.Integer("emitterLoops"),
            Local = e["keepSimulationLocal"] is true,
            ColorGroup = e["envColorGroup"]?.ToString(),
            Cooked = modules.Count == 0 && e.Tail.Length > 0,
        };
    }

    private static List<ParticleBurst> Bursts(List<object?> list) => list.OfType<RedObject>()
        .Select(b => new ParticleBurst((int)b.Integer("spawnCount", 1), b.Float("burstTime"), b.Float("spawnTimeRange"), b.Float("repeatTime"))).ToList();

    /// <summary>A module's class in words: CParticleModificatorSizeOverLife as "Size over life", CParticleInitializerVelocity as "Initial velocity".</summary>
    public static string ModuleTitle(string className)
    {
        bool initial = className.StartsWith("CParticleInitializer", StringComparison.Ordinal);
        string bare = className.Replace("CParticleInitializer", "").Replace("CParticleModificator", "").Replace("CParticleDrawer", "");
        string words = Regex.Replace(bare, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").ToLowerInvariant().Replace("3 d", "3D").Replace("3d", "3D");
        if (words.Length == 0) return className;
        words = char.ToUpperInvariant(words[0]) + words[1..];
        return initial && !words.StartsWith("Spawn", StringComparison.Ordinal) ? L.T("ParticleSystemFile.Initial", words.ToLowerInvariant()) : words;
    }
}
