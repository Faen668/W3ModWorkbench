using System.Numerics;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// Simulates one level of detail of a cloth resource as the game does: a port of the PhysX 3 cloth solver APEX runs
/// (NVIDIA's software solver, <c>SwSolverKernel</c>, BSD licensed), on the fabric APEX cooked for it.
/// <para>
/// Each frame the bones say where every particle is skinned to. The frame is split into iterations at the material's
/// solver frequency (250 a second in every cloth the game has), and each iteration, as the solver does:
/// moves the particles on by their velocity, damped, and gravity; keeps each within its max distance of where it's
/// skinned to (one with none is held there); pulls each back within its tethers' reach; solves the fabric's phases,
/// keeping pairs of particles at their rest length as stiffly as the phase's kind of fibre says; keeps them out of
/// their backstop spheres, behind them along their skinned normal; and pushes them out of the spheres and capsules on
/// the bones, with friction. Stiffness, damping and drag are given per the stiffness frequency, so they hold the same
/// whatever the step.
/// </para>
/// <para>
/// It runs in the model's space, where the bones are posed: what the body does moves the cloth as it would in the
/// game, but the model being placed somewhere else doesn't (the game passes on part of that, as the inertia scale says).
/// Self collision and virtual particles, which a few cloths have, aren't simulated.
/// </para>
/// </summary>
public sealed class ClothSimulation
{
    private const float Epsilon = 1.1920929e-7f;
    private const float Gravity = 9.81f;

    private readonly ClothAsset _asset;
    private readonly ClothLod _lod;
    private readonly ClothFabric _fabric;
    private readonly int _count;
    private readonly Vector3[] _current, _previous;
    // each particle's inverse mass this iteration: as cooked, 0 while it's held, less while it's colliding
    private readonly float[] _weight;
    // where each particle is skinned to, and its skinned normal, at the start and end of the step
    private Vector3[] _fromTarget, _toTarget, _fromNormal, _toNormal;
    private Vector4[] _fromSpheres = [], _toSpheres = [];
    private readonly Vector3[] _normals;
    // the collision shapes in the iteration being solved: the spheres' centres, how far they've moved, their radii; the capsules' axes
    private Vector3[] _centres = [], _moves = [], _axes = [];
    private float[] _radii = [], _inverseLengths = [];
    private bool _started;
    // whether the particles' normals worked out from the triangles face the other way to the mesh's own
    private bool _flipNormals;

    public ClothSimulation(ClothAsset asset, int lod)
    {
        _asset = asset;
        _lod = asset.Lods[lod];
        _fabric = _lod.Fabric ?? throw new ArgumentException("The level of detail has no fabric.", nameof(lod)); // loc-ignore: never shown
        _count = _fabric.Particles;
        _current = new Vector3[_count];
        _previous = new Vector3[_count];
        _weight = new float[_count];
        _fromTarget = new Vector3[_count];
        _toTarget = new Vector3[_count];
        _fromNormal = new Vector3[_count];
        _toNormal = new Vector3[_count];
        _normals = new Vector3[_count];
        Material = asset.Material;
        Thickness = asset.Thickness;
    }

    /// <summary>The settings it's simulated with; changing them takes effect on the next step.</summary>
    public ClothMaterial Material { get; set; }

    /// <summary>How far the cloth keeps from the spheres and capsules it collides with: the asset's, unless changed.</summary>
    public float Thickness { get; set; }

    /// <summary>The wind blowing on it, in metres a second, in the model's space.</summary>
    public Vector3 Wind { get; set; }

    /// <summary>The particles, where they are now.</summary>
    public ReadOnlySpan<Vector3> Particles => _current;

    /// <summary>The collision spheres where they are now: their centre, and their radius with the cloth's thickness.</summary>
    public ReadOnlySpan<Vector4> Spheres => _toSpheres;

    public ClothAsset Asset => _asset;

    /// <summary>Whether it's been put where the bones skin it to yet; until then its particles are nowhere.</summary>
    public bool Started => _started;

    public ClothLod Lod => _lod;

    /// <summary>Puts every particle where the bones skin it to, at rest. <paramref name="bones"/> are the asset's bones' skinning matrices.</summary>
    public void Reset(Matrix4x4[] bones)
    {
        Skin(bones, _toTarget, _toNormal);
        _toSpheres = SpheresAt(bones);
        Array.Copy(_toTarget, _current, _count);
        Array.Copy(_toTarget, _previous, _count);
        _started = true;
        _flipNormals = false;
        ComputeNormals();
        // the triangles' normals may wind the other way to the mesh's own; the render mesh's map expects its own
        float agree = 0;
        for (int i = 0; i < _count; i++) agree += Vector3.Dot(_normals[i], _toNormal[i]);
        if (agree < 0)
        {
            _flipNormals = true;
            ComputeNormals();
        }
    }

    /// <summary>Lets the cloth fall into place, the bones held still, for a while.</summary>
    public void Settle(Matrix4x4[] bones, float seconds)
    {
        if (!_started) Reset(bones);
        // it only has to come to rest, so it's solved less often than the material says, which is much quicker
        var material = Material;
        Material = material with { SolverFrequency = Math.Min(material.SolverFrequency, 90) };
        try
        {
            for (float t = 0; t < seconds; t += 1 / 30f) Step(bones, 1 / 30f);
        }
        finally
        {
            Material = material;
        }
    }

    /// <summary>Simulates a step of time, the bones moving from where they were to <paramref name="bones"/>.</summary>
    public void Step(Matrix4x4[] bones, float dt)
    {
        if (!_started)
        {
            Reset(bones);
            return;
        }
        (_fromTarget, _toTarget) = (_toTarget, _fromTarget);
        (_fromNormal, _toNormal) = (_toNormal, _fromNormal);
        _fromSpheres = _toSpheres;
        Skin(bones, _toTarget, _toNormal);
        _toSpheres = SpheresAt(bones);
        // a jump further than a body moves in a step (an animation looping back to where it started, a cut) is a
        // teleport, as APEX has it: the cloth goes with it as it is, rather than being dragged there
        var jump = Vector3.Zero;
        for (int i = 0; i < _count; i++) jump += _toTarget[i] - _fromTarget[i];
        jump /= Math.Max(1, _count);
        if (jump.Length() > Math.Max(0.3f, 15 * dt))
        {
            for (int i = 0; i < _count; i++)
            {
                _current[i] += jump;
                _previous[i] += jump;
                _fromTarget[i] += jump;
            }
            for (int s = 0; s < _fromSpheres.Length; s++) _fromSpheres[s] += new Vector4(jump, 0);
        }
        if (dt <= 0)
        {
            ComputeNormals();
            return;
        }
        dt = Math.Min(dt, 0.1f);

        var m = Material;
        int iterations = Math.Max(1, (int)(dt * Math.Max(1, m.SolverFrequency) + 0.5f));
        float iterDt = dt / iterations;
        float stiffnessFrequency = Math.Max(1e-3f, m.StiffnessFrequency);
        float exponent = stiffnessFrequency * iterDt;
        // APEX takes damping and drag as at a stiffness frequency of 10, whatever the material's
        float dampScale = MathF.Pow(1 - Math.Clamp(m.Damping, 0, 0.999f), 10 * iterDt);
        // the air drags the cloth towards the wind's speed, at least as a light drag would
        float windDrag = Wind == default ? 0 : 1 - MathF.Pow(1 - Math.Clamp(Math.Max(m.Drag, 0.1f), 0, 0.999f), 10 * iterDt);
        var gravity = _asset.GravityDirection * (Gravity * m.GravityScale * iterDt * iterDt);
        var wind = Wind * iterDt;
        var phases = Phases(m, exponent);
        float motionBias = -m.MaxDistanceBias;
        bool backstops = Array.Exists(_lod.Physical.BackstopRadius, r => r > 0);

        for (int it = 0; it < iterations; it++)
        {
            float alpha = (it + 1f) / iterations;
            Integrate(dampScale, gravity, wind, windDrag);
            ConstrainMotion(alpha, motionBias);
            ConstrainTethers(m);
            SolveFabric(phases);
            if (backstops) ConstrainSeparation(alpha);
            Collide(alpha, alpha - 1f / iterations, m);
        }
        ComputeNormals();
    }

    /// <summary>The particles and their normals as they are now, to place render vertices with while the simulation goes on.</summary>
    public ClothFrame Frame() => new(_lod, (Vector3[])_current.Clone(), (Vector3[])_normals.Clone());

    /// <summary>Where each particle is skinned to, and its normal, with the bones' skinning matrices.</summary>
    private void Skin(Matrix4x4[] bones, Vector3[] positions, Vector3[] normals)
    {
        var mesh = _lod.Physical;
        int per = mesh.BonesPerVertex;
        for (int v = 0; v < _count; v++)
        {
            var p = mesh.Positions[v];
            var n = mesh.Normals[v];
            if (per == 0 || bones.Length == 0)
            {
                (positions[v], normals[v]) = (p, n);
                continue;
            }
            Vector3 sum = default, normal = default;
            float total = 0;
            for (int b = 0; b < per; b++)
            {
                float w = mesh.BoneWeights[v * per + b];
                int bone = mesh.BoneIndices[v * per + b];
                if (w <= 0 || bone >= bones.Length) continue;
                sum += Vector3.Transform(p, bones[bone]) * w;
                normal += Vector3.TransformNormal(n, bones[bone]) * w;
                total += w;
            }
            positions[v] = total > 0 ? sum / total : p;
            normals[v] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : n;
        }
    }

    /// <summary>The collision spheres with the bones where they are: each on its bone as bound, moved as the bone has moved.</summary>
    private Vector4[] SpheresAt(Matrix4x4[] bones)
    {
        var spheres = new Vector4[_asset.Spheres.Length];
        for (int i = 0; i < spheres.Length; i++)
        {
            var s = _asset.Spheres[i];
            var bound = s.Bone >= 0 && s.Bone < _asset.BindPoses.Length ? Vector3.Transform(s.Local, _asset.BindPoses[s.Bone]) : s.Local;
            var at = s.Bone >= 0 && s.Bone < bones.Length ? Vector3.Transform(bound, bones[s.Bone]) : bound;
            spheres[i] = new Vector4(at, s.Radius + Thickness);
        }
        return spheres;
    }

    /// <summary>x' = x + (x - x₀)·damping + gravity·dt², for every particle that isn't held; and each back to its own weight.</summary>
    private void Integrate(float dampScale, Vector3 gravity, Vector3 wind, float windDrag)
    {
        var masses = _fabric.InverseMasses;
        for (int i = 0; i < _count; i++)
        {
            var current = _current[i];
            _weight[i] = masses[i];
            if (masses[i] > 0)
            {
                var velocity = current - _previous[i];
                _current[i] = current + velocity * dampScale + gravity;
                // wind drags it towards the air's speed
                if (windDrag > 0) _current[i] += (wind - velocity) * windDrag;
            }
            _previous[i] = current;
        }
    }

    /// <summary>Keeps each particle within its max distance of where it's skinned to, there and then; one with none is held on it.</summary>
    private void ConstrainMotion(float alpha, float bias)
    {
        var maxDistance = _lod.Physical.MaxDistance;
        for (int i = 0; i < _count; i++)
        {
            var target = Vector3.Lerp(_fromTarget[i], _toTarget[i], alpha);
            var delta = target - _current[i];
            float radius = Math.Max(0, maxDistance[i] + bias);
            if (radius <= 0) _weight[i] = 0;
            float length = MathF.Sqrt(Epsilon + delta.LengthSquared());
            float slack = 1 - radius / length;
            if (slack > 0) _current[i] += delta * slack;
        }
    }

    /// <summary>Pulls each particle back towards its tethers' anchors when it's further from one than the tether allows.</summary>
    private void ConstrainTethers(ClothMaterial m)
    {
        var anchors = _fabric.TetherAnchors;
        if (anchors.Length == 0 || m.TetherStiffness <= 0) return;
        var lengths = _fabric.TetherLengths;
        float stiffness = _count * m.TetherStiffness / anchors.Length;
        for (int i = 0; i < _count; i++)
        {
            var position = _current[i];
            var offset = Vector3.Zero;
            for (int t = i; t < anchors.Length; t += _count)
            {
                var delta = _current[anchors[t]] - position;
                float slack = 1 - lengths[t] * m.TetherLimit / MathF.Sqrt(Epsilon + delta.LengthSquared());
                if (slack > 0) offset += delta * slack;
            }
            _current[i] = position + offset * stiffness;
        }
    }

    /// <summary>A phase to solve: its constraints, its stiffness this iteration, and how much more it gives beyond its compression and stretch range.</summary>
    private readonly record struct Phase(int Start, int End, float Stiffness, float Multiplier, float Compression, float Stretch);

    /// <summary>
    /// Each phase's settings for an iteration, from the material by its kind of fibre: a stiffness s, given as a part
    /// of the error each 1/frequency of a second, is 1 - (1 - s)^(frequency·dt) for one iteration of dt.
    /// </summary>
    private Phase[] Phases(ClothMaterial m, float exponent)
    {
        var phases = new Phase[_fabric.PhaseTypes.Length];
        for (int p = 0; p < phases.Length; p++)
        {
            int kind = _fabric.PhaseTypes[p];
            float stiffness = kind switch { 1 => m.VerticalStretching, 2 => m.HorizontalStretching, 3 => m.Bending, 4 => m.Shearing, _ => 1 };
            var (compression, stretch, scale) = kind is >= 1 and <= 4 && m.Scaling.Length >= kind ? m.Scaling[kind - 1] : (1, 1, 1);
            int set = _fabric.PhaseSets[p];
            phases[p] = new Phase(_fabric.SetStarts[set], _fabric.SetStarts[set + 1],
                1 - Exp2(SafeLog2(1 - stiffness) * exponent),
                1 - Exp2(SafeLog2(scale) * exponent),
                compression > 0 ? 1 - 1 / compression : 0,
                stretch > 0 ? 1 - 1 / stretch : 0);
        }
        return phases;
    }

    private static float SafeLog2(float x)
    {
        float saturated = Math.Clamp(x, 0, 1);
        return saturated > 0 ? MathF.Log2(saturated) : -128;
    }

    private static float Exp2(float x) => MathF.Pow(2, x);

    /// <summary>Gauss-Seidel over each phase's pairs, keeping them at their rest length as stiffly as the phase says.</summary>
    private void SolveFabric(Phase[] phases)
    {
        var rest = _fabric.RestLengths;
        var pairs = _fabric.Pairs;
        foreach (var phase in phases)
        {
            bool neutral = phase.Multiplier == 0 && phase.Compression == 0 && phase.Stretch == 0;
            for (int c = phase.Start; c < phase.End; c++)
            {
                int i = pairs[c * 2], j = pairs[c * 2 + 1];
                float r = rest[c];
                if (r <= Epsilon) continue;
                var h = _current[j] - _current[i];
                float wij = _weight[i] + _weight[j];
                float e = 1 - r / MathF.Sqrt(Epsilon + h.LengthSquared());
                if (!neutral) e -= phase.Multiplier * Math.Max(phase.Compression, Math.Min(e, phase.Stretch));
                float ex = e * phase.Stiffness / (Epsilon + wij);
                _current[i] += h * (ex * _weight[i]);
                _current[j] -= h * (ex * _weight[j]);
            }
        }
    }

    /// <summary>Keeps each particle out of its backstop: a sphere behind where it's skinned to, along its skinned normal.</summary>
    private void ConstrainSeparation(float alpha)
    {
        var radius = _lod.Physical.BackstopRadius;
        var distance = _lod.Physical.BackstopDistance;
        for (int i = 0; i < _count; i++)
        {
            if (radius[i] <= 0) continue;
            var normal = Vector3.Lerp(_fromNormal[i], _toNormal[i], alpha);
            var centre = Vector3.Lerp(_fromTarget[i], _toTarget[i], alpha) - normal * distance[i];
            var delta = centre - _current[i];
            float slack = 1 - radius[i] / MathF.Sqrt(Epsilon + delta.LengthSquared());
            if (slack < 0) _current[i] += delta * slack;
        }
    }

    /// <summary>
    /// Pushes the particles out of the bones' spheres and the capsules joining them (each narrowing from one sphere's
    /// radius to the other's), the pushes from every shape a particle is in averaged; friction takes off some of its
    /// sliding along them, and it's made heavier while it collides, as the material's mass scale says.
    /// </summary>
    private void Collide(float alpha, float previousAlpha, ClothMaterial m)
    {
        var spheres = _toSpheres;
        if (spheres.Length == 0) return;
        if (_centres.Length != spheres.Length)
        {
            _centres = new Vector3[spheres.Length];
            _moves = new Vector3[spheres.Length];
            _radii = new float[spheres.Length];
        }
        // the shapes where they are this iteration, and how far each has moved since the last
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (int s = 0; s < spheres.Length; s++)
        {
            var from = s < _fromSpheres.Length ? _fromSpheres[s] : spheres[s];
            var now = Vector4.Lerp(from, spheres[s], alpha);
            var before = Vector4.Lerp(from, spheres[s], Math.Max(0, previousAlpha));
            _centres[s] = new Vector3(now.X, now.Y, now.Z);
            _moves[s] = _centres[s] - new Vector3(before.X, before.Y, before.Z);
            _radii[s] = now.W;
            low = Vector3.Min(low, _centres[s] - new Vector3(now.W));
            high = Vector3.Max(high, _centres[s] + new Vector3(now.W));
        }
        var capsules = _asset.Capsules;
        int capsuleCount = capsules.Length / 2;
        if (_axes.Length != capsuleCount)
        {
            _axes = new Vector3[capsuleCount];
            _inverseLengths = new float[capsuleCount];
        }
        for (int k = 0; k < capsuleCount; k++)
        {
            var axis = _centres[capsules[k * 2 + 1]] - _centres[capsules[k * 2]];
            float lengthSq = axis.LengthSquared();
            _axes[k] = axis;
            _inverseLengths[k] = lengthSq < 1e-10f ? 0 : 1 / lengthSq;
        }
        var centres = _centres;
        var moves = _moves;
        var radii = _radii;
        var axes = _axes;
        var inverseLengths = _inverseLengths;
        var current = _current;
        var masses = _fabric.InverseMasses;
        for (int i = 0; i < _count; i++)
        {
            if (masses[i] <= 0) continue;
            var p = current[i];
            // nothing to collide with this far from every shape
            if (p.X < low.X || p.Y < low.Y || p.Z < low.Z || p.X > high.X || p.Y > high.Y || p.Z > high.Z) continue;
            Vector3 push = default, shapeMove = default;
            int collisions = 0;
            for (int s = 0; s < centres.Length; s++)
            {
                var d = p - centres[s];
                float r = radii[s], lengthSq = d.LengthSquared();
                if (lengthSq >= r * r || r <= 0) continue;
                float length = MathF.Sqrt(lengthSq);
                push += length > 1e-6f ? d * (r / length - 1) : new Vector3(0, 0, r);
                shapeMove += moves[s];
                collisions++;
            }
            for (int k = 0; k < capsuleCount; k++)
            {
                if (inverseLengths[k] == 0) continue;
                int a = capsules[k * 2], b = capsules[k * 2 + 1];
                float t = Vector3.Dot(p - centres[a], axes[k]) * inverseLengths[k];
                if (t <= 0 || t >= 1) continue; // the spheres at its ends are kept to on their own
                var d = p - (centres[a] + axes[k] * t);
                float r = radii[a] + (radii[b] - radii[a]) * t, lengthSq = d.LengthSquared();
                if (lengthSq >= r * r || lengthSq < 1e-12f) continue;
                push += d * (r / MathF.Sqrt(lengthSq) - 1);
                shapeMove += Vector3.Lerp(moves[a], moves[b], t);
                collisions++;
            }
            if (collisions == 0) continue;
            var delta = push / collisions;
            if (m.Friction > 0)
            {
                // the particle's sliding against the shapes, less their own movement, takes off its velocity
                float deltaSq = delta.LengthSquared();
                var n = delta / MathF.Sqrt(deltaSq + Epsilon);
                var relative = p - _previous[i] - shapeMove / collisions;
                var tangent = relative - n * Vector3.Dot(relative, n);
                float j = Math.Max(-m.Friction * deltaSq / MathF.Sqrt(deltaSq + Epsilon) / MathF.Sqrt(tangent.LengthSquared() + Epsilon), -1);
                _previous[i] -= tangent * j;
            }
            if (m.MassScale > 0) _weight[i] /= 1 + m.MassScale * delta.LengthSquared();
            _current[i] = p + delta;
        }
    }

    /// <summary>Each particle's normal, from the triangles of the physical mesh around it, facing as the mesh's own.</summary>
    private void ComputeNormals()
    {
        Array.Clear(_normals);
        var indices = _lod.Physical.Indices;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if (a >= _count || b >= _count || c >= _count) continue;
            var face = Vector3.Cross(_current[b] - _current[a], _current[c] - _current[a]);
            if (_flipNormals) face = -face;
            _normals[a] += face;
            _normals[b] += face;
            _normals[c] += face;
        }
        for (int i = 0; i < _count; i++)
            _normals[i] = _normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(_normals[i]) : _toNormal[i];
    }
}
