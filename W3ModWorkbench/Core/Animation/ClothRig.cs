using System.Numerics;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench.Core.Animation;

/// <summary>A cloth's particles and their normals at one moment, which places its render vertices.</summary>
public sealed class ClothFrame(ClothLod lod, Vector3[] particles, Vector3[] normals)
{
    public ReadOnlySpan<Vector3> Particles => particles;

    /// <summary>
    /// A render chunk's vertices as the cloth has them: <paramref name="skinned"/> (the chunk skinned as a plain mesh)
    /// with every vertex the cloth carries moved where the particles put it: on a triangle of them, so far along their
    /// normal (APEX's mesh-to-mesh skinning), or onto one. <paramref name="first"/> is where the chunk's vertices start
    /// among its level of detail's.
    /// </summary>
    public Vector3[] Place(Vector3[] skinned, int first)
    {
        var placed = (Vector3[])skinned.Clone();
        int end = first + placed.Length, count = particles.Length;
        foreach (var e in lod.SkinMap)
        {
            if (e.Vertex < first || e.Vertex >= end || e.A >= count || e.B >= count || e.C >= count) continue;
            float c = 1 - e.Bary.X - e.Bary.Y;
            var at = particles[e.A] * e.Bary.X + particles[e.B] * e.Bary.Y + particles[e.C] * c;
            var normal = normals[e.A] * e.Bary.X + normals[e.B] * e.Bary.Y + normals[e.C] * c;
            placed[e.Vertex - first] = at + normal * e.Bary.Z;
        }
        var immediate = lod.Immediate;
        for (int v = Math.Max(0, first); v < end && v < immediate.Length; v++)
            if (immediate[v] is >= 0 and var particle && particle < count) placed[v - first] = particles[particle];
        return placed;
    }
}

/// <summary>
/// The cloth among an actor's meshes (its parts), simulated together as its bones move. Each part that's a cloth
/// resource gets a simulation for each of its levels of detail, so whichever the view draws moves; only those the
/// view has drawn are simulated (the most detailed to begin with), one starting when it's first drawn. A frame's result
/// is a <see cref="ClothPose"/>, which places the cloth's render vertices and can be used while the next frame is worked out.
/// </summary>
public sealed class ClothRig
{
    /// <summary>Whether cloth is simulated at all; when it isn't, it's skinned like any mesh. The app sets it from its settings.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>How long cloth is let fall into place after it's put where it's skinned to (a jump in time, or first shown).</summary>
    public const float SettleTime = 1f;

    // a jump in time longer than this starts the cloth over, where it's skinned to
    private const float LongestStep = 0.25f;

    private readonly Dictionary<int, ClothSimulation?[]> _parts;
    // for each part, the levels of detail the view has drawn, which are the ones simulated
    private readonly Dictionary<int, bool[]> _drawn;
    private readonly object _lock = new();
    private float? _time;

    private ClothRig(Dictionary<int, ClothSimulation?[]> parts)
    {
        _parts = parts;
        _drawn = parts.ToDictionary(p => p.Key, p => p.Value.Select((_, i) => i == 0).ToArray());
    }

    /// <summary>
    /// A rig for an actor's meshes, or null when none of them is cloth that can be simulated, or simulation is off
    /// (<paramref name="always"/> simulates it anyway, as the cloth viewer does).
    /// </summary>
    public static ClothRig? For(IReadOnlyList<MeshFile> meshes, bool always = false)
    {
        if (!Enabled && !always) return null;
        var parts = new Dictionary<int, ClothSimulation?[]>();
        for (int p = 0; p < meshes.Count; p++)
            if (meshes[p].Cloth is { } cloth)
                parts[p] = [.. cloth.Lods.Select((l, i) => l.Fabric is null ? null : new ClothSimulation(cloth, i))];
        return parts.Count == 0 ? null : new ClothRig(parts);
    }

    /// <summary>The parts that are cloth.</summary>
    public IEnumerable<int> Parts => _parts.Keys;

    /// <summary>Times the cloth is put back where it's skinned to, as a cutscene's reset cloth events do at its cuts.</summary>
    public IReadOnlyList<float> Resets { get; set; } = [];

    /// <summary>
    /// When a cutscene turns its cloth off, and how far: from each time on, the cloth is drawn that far (0 to 1) back
    /// towards where it's skinned to, until the next.
    /// </summary>
    public IReadOnlyList<(float Time, float Weight)> Off { get; set; } = [];

    /// <summary>How far the cloth is turned off at a time, by the last of <see cref="Off"/> by then.</summary>
    private float OffAt(float time)
    {
        float weight = 0;
        foreach (var (at, w) in Off)
            if (at <= time) weight = Math.Clamp(w, 0, 1);
        return weight;
    }

    /// <summary>The simulations of a part, one for each level of detail (null for one that has no fabric).</summary>
    public IReadOnlyList<ClothSimulation?> Of(int part) => _parts.TryGetValue(part, out var sims) ? sims : [];

    /// <summary>
    /// Simulates on to a time, the bones of each part (<paramref name="bones"/> gives a part's skinning matrices) where
    /// they are then. A step back in time, or a long jump, starts the cloth over where it's skinned to and lets it settle;
    /// <paramref name="loop"/>, the length of an animation that loops, makes a step back past its end a step on.
    /// </summary>
    public ClothPose StepTo(float time, Func<int, Matrix4x4[]?> bones, float loop = 0)
    {
        lock (_lock)
        {
            float? dt = _time is float last ? time - last : null;
            if (dt < 0 && loop > 0) dt += loop;
            // a reset passed starts it over, as the game does at a cut
            bool reset = _time is float from && Resets.Any(r => r > from && r <= time);
            _time = time;
            var pose = !reset && dt is float step and >= 0 and <= LongestStep ? Advance(step, bones) : Restart(bones);
            pose.Off = OffAt(time);
            return pose;
        }
    }

    /// <summary>Simulates a step of time on, the bones where they are at its end.</summary>
    public ClothPose Advance(float dt, Func<int, Matrix4x4[]?> bones)
    {
        lock (_lock)
        {
            Each(bones, (sim, matrices) =>
            {
                if (sim.Started) sim.Step(matrices, dt);
                else
                {
                    // a level of detail drawn for the first time starts where it's skinned to, settled
                    sim.Reset(matrices);
                    sim.Settle(matrices, SettleTime);
                }
            });
            return Pose();
        }
    }

    /// <summary>Starts every cloth over where the bones skin it to, and lets it settle.</summary>
    public ClothPose Restart(Func<int, Matrix4x4[]?> bones)
    {
        lock (_lock)
        {
            Each(bones, (sim, matrices) =>
            {
                sim.Reset(matrices);
                sim.Settle(matrices, SettleTime);
            });
            return Pose();
        }
    }

    /// <summary>Does something to every simulation with its part's bones, the simulations in parallel.</summary>
    private void Each(Func<int, Matrix4x4[]?> bones, Action<ClothSimulation, Matrix4x4[]> act)
    {
        var work = new List<(ClothSimulation, Matrix4x4[])>();
        foreach (var (part, sims) in _parts)
            if (bones(part) is { } matrices)
                for (int lod = 0; lod < sims.Length; lod++)
                    if (sims[lod] is { } sim && Volatile.Read(ref _drawn[part][lod])) work.Add((sim, matrices));
        if (work.Count == 0) return;
        if (work.Count == 1) act(work[0].Item1, work[0].Item2);
        else Parallel.ForEach(work, w => act(w.Item1, w.Item2));
    }

    /// <summary>Forgets the time it was last simulated to, so the next step starts it over.</summary>
    public void Forget()
    {
        lock (_lock) _time = null;
    }

    private ClothPose Pose() =>
        new(_parts.ToDictionary(p => p.Key, p => p.Value.Select(s => s is { Started: true } ? s.Frame() : null).ToArray()),
            _parts.ToDictionary(p => p.Key, p => p.Value.FirstOrDefault(s => s is not null)?.Asset), Drawn);

    /// <summary>Notes that a level of detail of a part is drawn, so it's simulated from the next step on.</summary>
    private void Drawn(int part, int lod)
    {
        if (_drawn.TryGetValue(part, out var lods) && lod < lods.Length) Volatile.Write(ref lods[lod], true);
    }
}

/// <summary>Where an actor's cloth is at one moment: each cloth part's particles, for every level of detail simulated.</summary>
public sealed class ClothPose(Dictionary<int, ClothFrame?[]> frames, Dictionary<int, ClothAsset?> assets, Action<int, int> drawn)
{
    /// <summary>
    /// A chunk of a part as the cloth has it, <paramref name="skinned"/> being it skinned as a plain mesh; null when
    /// the part isn't cloth, or the chunk's level of detail isn't simulated.
    /// </summary>
    public Vector3[]? Place(int part, MeshChunk chunk, Vector3[] skinned)
    {
        if (!frames.TryGetValue(part, out var lods) || assets.GetValueOrDefault(part) is not { } asset
            || !asset.Chunks.TryGetValue(chunk.Index, out var at) || at.Lod >= lods.Length) return null;
        drawn(part, at.Lod);
        return lods[at.Lod]?.Place(skinned, at.First);
    }

    /// <summary>Whether a part is cloth this moves.</summary>
    public bool Moves(int part) => frames.ContainsKey(part);

    /// <summary>How far the cloth is drawn back to where it's skinned to (a cutscene turning it off): 0 not at all, 1 wholly.</summary>
    public float Off { get; set; }

    /// <summary>
    /// A chunk of a part skinned to its bones' matrices (<see cref="Skinning.Pose"/>), and then, when the part is cloth
    /// <paramref name="cloth"/> moves, placed as the cloth has it.
    /// </summary>
    public static Vector3[]? Skin(ClothPose? cloth, int part, MeshChunk chunk, Matrix4x4[] bones)
    {
        var skinned = Skinning.Pose(chunk, bones);
        if (cloth is null || !cloth.Moves(part) || cloth.Off >= 1) return skinned;
        var from = skinned ?? chunk.Positions;
        if (cloth.Place(part, chunk, from) is not { } placed) return skinned;
        if (cloth.Off > 0)
            for (int v = 0; v < placed.Length; v++) placed[v] = Vector3.Lerp(placed[v], from[v], cloth.Off);
        return placed;
    }
}
