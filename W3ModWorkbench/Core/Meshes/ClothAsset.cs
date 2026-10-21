using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>
/// How a cloth resource moves: the settings its material gives the solver. These are APEX's clothing material, as the
/// asset keeps it (<c>ClothingMaterialLibraryParameters</c> 0.13), with the resource's own <c>mtl…</c> settings over it
/// where it has them, as REDkit's Apex Cloth tab sets them.
/// </summary>
public sealed record ClothMaterial
{
    public string Name { get; init; } = "";
    /// <summary>How hard the cloth keeps its length along its fibres, up and down (0 to 1, of the error each millisecond at a stiffness frequency of 1000).</summary>
    public float VerticalStretching { get; init; } = 1;
    public float HorizontalStretching { get; init; } = 1;
    public float Bending { get; init; } = 0.5f;
    public float Shearing { get; init; } = 0.5f;
    public float ZeroStretch { get; init; }
    /// <summary>How hard each particle is pulled back towards where it's tethered (0 to 1).</summary>
    public float TetherStiffness { get; init; } = 1;
    /// <summary>How far a tether lets a particle go, as a part of its length (1 is not at all further).</summary>
    public float TetherLimit { get; init; } = 1;
    public bool OrthoBending { get; init; }
    /// <summary>For each kind of fibre (vertical, horizontal, bending, shearing): the compression range, the stretch range and the scale beyond them.</summary>
    public (float Compression, float Stretch, float Scale)[] Scaling { get; init; } = [(1, 1, 1), (1, 1, 1), (1, 1, 1), (1, 1, 1)];
    public float Damping { get; init; } = 0.1f;
    public float StiffnessFrequency { get; init; } = 100;
    public float Drag { get; init; }
    public bool ComDamping { get; init; }
    public float Friction { get; init; } = 0.25f;
    public float MassScale { get; init; } = 25;
    public int SolverIterations { get; init; } = 5;
    public float SolverFrequency { get; init; } = 250;
    public float GravityScale { get; init; } = 1;
    public float InertiaScale { get; init; } = 1;
    public float HardStretchLimitation { get; init; }
    public float MaxDistanceBias { get; init; }
    public int HierarchicalSolverIterations { get; init; }
    public float SelfcollisionThickness { get; init; }
    public float SelfcollisionSquashScale { get; init; }
    public float SelfcollisionStiffness { get; init; }
}

/// <summary>
/// The mesh a cloth's particles are: one for each level of detail. Each vertex is skinned to the bones like a render
/// mesh's, and may move up to its max distance from where it's skinned to (0 holds it there); a backstop sphere behind
/// it, along its normal, keeps it from going through the body. Its particles are its first <see cref="Particles"/>
/// vertices, the rest are only skinned.
/// </summary>
public sealed class ClothPhysicalMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required ushort[] BoneIndices { get; init; }
    public required float[] BoneWeights { get; init; }
    public required int BonesPerVertex { get; init; }
    public required float[] MaxDistance { get; init; }
    public required float[] BackstopRadius { get; init; }
    public required float[] BackstopDistance { get; init; }
    public required int[] Indices { get; init; }
    public float AverageEdgeLength { get; init; }
}

/// <summary>
/// The constraints the solver keeps a cloth's particles to, as APEX cooked them for PhysX 3 (<c>ClothingCookedPhysX3Param</c> 0.3):
/// pairs of particles kept at a length, in sets; each phase solves one set as one kind of fibre; tethers keep each
/// particle within a length of particles that are held.
/// </summary>
public sealed class ClothFabric
{
    public required int Particles { get; init; }
    /// <summary>Each phase's kind: 1 vertical, 2 horizontal, 3 bending, 4 shearing.</summary>
    public required int[] PhaseTypes { get; init; }
    public required int[] PhaseSets { get; init; }
    /// <summary>Where each set's constraints start, and after the last, where they end.</summary>
    public required int[] SetStarts { get; init; }
    public required float[] RestLengths { get; init; }
    /// <summary>Two particles for each constraint.</summary>
    public required int[] Pairs { get; init; }
    /// <summary>The particle each tether goes from, a particle's tethers each <see cref="Particles"/> apart.</summary>
    public required int[] TetherAnchors { get; init; }
    public required float[] TetherLengths { get; init; }
    /// <summary>One over each particle's mass; 0 for one that's only skinned.</summary>
    public required float[] InverseMasses { get; init; }
}

/// <summary>
/// Where one of a level of detail's render vertices goes: onto one particle (as the immediate map has it), or on a
/// triangle of particles, so far along their normal (the mesh-to-mesh skinning map).
/// </summary>
public readonly record struct ClothSkinEntry(int Vertex, int A, int B, int C, Vector3 Bary);

/// <summary>A level of detail's simulation: its physical mesh, its fabric, and how its render vertices follow the particles.</summary>
public sealed class ClothLod
{
    public required ClothPhysicalMesh Physical { get; init; }
    public ClothFabric? Fabric { get; init; }
    /// <summary>For each render vertex of the level (its submeshes one after another), the particle it sits on, or -1.</summary>
    public required int[] Immediate { get; init; }
    public required ClothSkinEntry[] SkinMap { get; init; }
}

/// <summary>A sphere on a bone that the cloth is kept out of; two joined make a capsule, narrowing from one to the other.</summary>
public readonly record struct ClothSphere(int Bone, float Radius, Vector3 Local);

/// <summary>
/// What it takes to simulate a cloth resource: its material, its levels of detail with their physical mesh, fabric
/// and maps, the spheres and capsules on its bones that the body pushes it with, and the bones' poses as bound.
/// Read from the APEX clothing asset (<c>ClothingAssetParameters</c> 0.13), whose struct layouts are NVIDIA's (APEX
/// 1.4's legacy clothing module keeps each old version).
/// </summary>
public sealed class ClothAsset
{
    public required ClothMaterial Material { get; init; }
    /// <summary>The material as the APEX asset has it, before the resource's own settings.</summary>
    public required ClothMaterial AssetMaterial { get; init; }
    /// <summary>How far the cloth keeps from what it collides with.</summary>
    public required float Thickness { get; init; }
    public required float VirtualParticleDensity { get; init; }
    public required bool DisableCcd { get; init; }
    public required Vector3 GravityDirection { get; init; }
    public required List<ClothLod> Lods { get; init; }
    public required ClothSphere[] Spheres { get; init; }
    /// <summary>Pairs of spheres that make capsules.</summary>
    public required int[] Capsules { get; init; }
    public required Matrix4x4[] BindPoses { get; init; }
    /// <summary>Each render chunk's level of detail, and where its vertices start among that level's.</summary>
    public required IReadOnlyDictionary<int, (int Lod, int First)> Chunks { get; init; }

    internal static ClothAsset? Read(ApexAsset apex, RedObject resource, Matrix4x4[] inverseBinds, IReadOnlyDictionary<int, (int Lod, int First)> chunks)
    {
        long asset = apex.First("ClothingAssetParameters");
        if (asset < 0) return null;
        // ParametersStruct: physicalMeshes, graphicalLods, simulation (at 48), bones (88), three counts, boneActors (128),
        // boneVertices, boneSpheres (176), boneSphereConnections (200), bonePlanes, collisionConvexes, cookedData (272),
        // boundingBox, materialLibrary (320), materialIndex (328)
        float thickness = apex.F32(asset + 48 + 4);
        float density = apex.F32(asset + 48 + 8);
        var gravity = apex.Vector(asset + 48 + 12);
        bool disableCcd = apex.U8(asset + 48 + 28) != 0;
        var assetMaterial = ReadMaterial(apex, apex.Reference(asset + 320), (int)apex.U32(asset + 328));
        var material = Overridden(assetMaterial, resource);
        thickness = resource["simThickness"] is float t ? t : thickness;
        density = resource["simVirtualParticleDensity"] is float d ? d : density;
        disableCcd = resource["simDisableCCD"] is bool ccd ? ccd : disableCcd;

        var binds = inverseBinds.Select(m => Matrix4x4.Invert(m, out var bind) ? bind : Matrix4x4.Identity).ToArray();
        var spheres = new List<ClothSphere>();
        var capsules = new List<int>();
        var (sphereAt, sphereCount) = apex.Items(asset + 176, 20);
        for (int i = 0; i < sphereCount; i++)
        {
            long s = sphereAt + i * 20L;
            spheres.Add(new ClothSphere((int)apex.U32(s), apex.F32(s + 4), apex.Vector(s + 8)));
        }
        var (pairAt, pairCount) = apex.Items(asset + 200, 2);
        for (int i = 0; i + 1 < pairCount; i += 2)
        {
            int a = (int)(apex.U32(pairAt + i * 2L) & 0xFFFF), b = (int)(apex.U32(pairAt + i * 2L + 2) & 0xFFFF);
            if (a < spheres.Count && b < spheres.Count) capsules.AddRange([a, b]);
        }
        // the older way: capsules on bones, each two spheres half its height up and down its Y axis, joined
        var (actorAt, actorCount) = apex.Items(asset + 128, 68);
        for (int i = 0; i < actorCount && spheres.Count == sphereCount; i++)
        {
            long a = actorAt + i * 68L;
            int bone = (int)apex.U32(a);
            float radius = apex.F32(a + 12), height = apex.F32(a + 16);
            if (apex.U32(a + 8) != 0 || radius <= 0 || bone < 0 || bone >= binds.Length) continue; // a convex, which isn't kept to
            var local = apex.Pose(a + 20);
            // the sphere's place is in the bone's space as bound, which the bone's own spheres keep relative to its pose as bound
            var up = Vector3.Transform(new Vector3(0, height / 2, 0), local);
            var down = Vector3.Transform(new Vector3(0, -height / 2, 0), local);
            capsules.AddRange([spheres.Count, spheres.Count + 1]);
            spheres.Add(new ClothSphere(bone, radius, up));
            spheres.Add(new ClothSphere(bone, radius, down));
        }

        // the cooked fabrics, each for a physical mesh by its number, linked one to the next
        var fabrics = new Dictionary<int, ClothFabric>();
        foreach (var (header, cls) in apex.Objects)
            if (cls == "ClothingCookedPhysX3Param" && Fabric(apex, header + ApexAsset.Fields) is { } fabric)
                fabrics.TryAdd((int)apex.U32(header + ApexAsset.Fields), fabric);

        var lods = new List<ClothLod>();
        var (physicalAt, physicalCount) = apex.Items(asset, 8);
        var (graphicalAt, graphicalCount) = apex.Items(asset + 24, 8);
        for (int g = 0; g < graphicalCount; g++)
        {
            long lod = apex.Reference(graphicalAt + g * 8L);
            if (lod < 0) return null;
            int physicalId = (int)apex.U32(lod + 28);
            if (physicalId >= physicalCount || Physical(apex, apex.Reference(physicalAt + physicalId * 8L)) is not { } physical) return null;
            var fabric = fabrics.GetValueOrDefault(physicalId);
            if (fabric is not null && (fabric.Particles > physical.Positions.Length || fabric.Pairs.Any(p => p >= fabric.Particles))) fabric = null;
            // the immediate map: one entry a render vertex, the particle's index with flags in its top bits
            var (immediateAt, immediateCount) = apex.Items(lod + 48, 4);
            var immediate = new int[immediateCount];
            for (int i = 0; i < immediateCount; i++)
            {
                uint entry = apex.U32(immediateAt + i * 4L);
                immediate[i] = (entry & 0x80000000) != 0 ? -1 : (int)(entry & 0x0FFFFFFF);
            }
            // the mesh-to-mesh map: a triangle of particles, two barycentric weights and the height along their normal
            var (mapAt, mapCount) = apex.Items(lod + 96, 52);
            var map = new ClothSkinEntry[mapCount];
            for (int i = 0; i < mapCount; i++)
            {
                long m = mapAt + i * 52L;
                map[i] = new ClothSkinEntry((int)apex.U32(m + 48), (int)apex.U32(m + 12), (int)apex.U32(m + 28), (int)apex.U32(m + 44), apex.Vector(m));
            }
            lods.Add(new ClothLod { Physical = physical, Fabric = fabric, Immediate = immediate, SkinMap = map });
        }
        if (lods.Count == 0 || lods.All(l => l.Fabric is null)) return null;
        return new ClothAsset
        {
            Material = material,
            AssetMaterial = assetMaterial,
            Thickness = thickness,
            VirtualParticleDensity = density,
            DisableCcd = disableCcd,
            GravityDirection = gravity.LengthSquared() > 0.5f ? Vector3.Normalize(gravity) : -Vector3.UnitZ,
            Lods = lods,
            Spheres = [.. spheres],
            Capsules = [.. capsules],
            BindPoses = binds,
            Chunks = chunks,
        };
    }

    /// <summary>
    /// The material the asset picks from its library. A ClothingMaterial is 160 bytes: its name, the stretching,
    /// bending, shearing, zero-stretch and tether settings, ortho bending, four stiffness scalings, then damping,
    /// stiffness frequency, drag, CoM damping, friction, mass scale, solver iterations and frequency, gravity and
    /// inertia scale, hard stretch limitation, max distance bias, hierarchical iterations and the self collision ones.
    /// </summary>
    private static ClothMaterial ReadMaterial(ApexAsset apex, long library, int index)
    {
        if (library < 0 || apex.Items(library, 160) is not { Count: > 0 } materials) return new ClothMaterial();
        long m = materials.At + Math.Clamp(index, 0, materials.Count - 1) * 160L;
        (float, float, float) Scaling(long at) => (apex.F32(at), apex.F32(at + 4), apex.F32(at + 8));
        return new ClothMaterial
        {
            Name = apex.Pointer(m, 0) is >= 0 and var name ? apex.Text(name) : "",
            VerticalStretching = apex.F32(m + 16),
            HorizontalStretching = apex.F32(m + 20),
            Bending = apex.F32(m + 24),
            Shearing = apex.F32(m + 28),
            ZeroStretch = apex.F32(m + 32),
            TetherStiffness = apex.F32(m + 36),
            TetherLimit = apex.F32(m + 40),
            OrthoBending = apex.U8(m + 44) != 0,
            Scaling = [Scaling(m + 48), Scaling(m + 60), Scaling(m + 72), Scaling(m + 84)],
            Damping = apex.F32(m + 96),
            StiffnessFrequency = apex.F32(m + 100),
            Drag = apex.F32(m + 104),
            ComDamping = apex.U8(m + 108) != 0,
            Friction = apex.F32(m + 112),
            MassScale = apex.F32(m + 116),
            SolverIterations = (int)apex.U32(m + 120),
            SolverFrequency = apex.F32(m + 124),
            GravityScale = apex.F32(m + 128),
            InertiaScale = apex.F32(m + 132),
            HardStretchLimitation = apex.F32(m + 136),
            MaxDistanceBias = apex.F32(m + 140),
            HierarchicalSolverIterations = (int)apex.U32(m + 144),
            SelfcollisionThickness = apex.F32(m + 148),
            SelfcollisionSquashScale = apex.F32(m + 152),
            SelfcollisionStiffness = apex.F32(m + 156),
        };
    }

    /// <summary>The material with the resource's own settings over it, where the resource has them.</summary>
    public static ClothMaterial Overridden(ClothMaterial material, RedObject resource)
    {
        float F(string name, float value) => resource[name] is float f ? f : value;
        return material with
        {
            MassScale = F("mtlMassScale", material.MassScale),
            Friction = F("mtlFriction", material.Friction),
            GravityScale = F("mtlGravityScale", material.GravityScale),
            Bending = F("mtlBendingStiffness", material.Bending),
            Shearing = F("mtlShearingStiffness", material.Shearing),
            TetherStiffness = F("mtlTetherStiffness", material.TetherStiffness),
            TetherLimit = F("mtlTetherLimit", material.TetherLimit),
            Damping = F("mtlDamping", material.Damping),
            Drag = F("mtlDrag", material.Drag),
            InertiaScale = F("mtlInertiaScale", material.InertiaScale),
            MaxDistanceBias = F("mtlMaxDistanceBias", material.MaxDistanceBias),
            SelfcollisionThickness = F("mtlSelfcollisionThickness", material.SelfcollisionThickness),
            SelfcollisionStiffness = F("mtlSelfcollisionStiffness", material.SelfcollisionStiffness),
            HardStretchLimitation = F("mtlHardStretchLimitation", material.HardStretchLimitation),
            ComDamping = resource["mtlComDamping"] is bool com ? com : material.ComDamping,
        };
    }

    /// <summary>
    /// A physical mesh (<c>ClothingPhysicalMeshParameters</c> 0.10): its vertex and index counts and bones per vertex,
    /// then arrays of its vertices (at 16), normals, skinning normals, constrain coefficients (88: max distance and the
    /// backstop sphere's radius and distance), bone indices (112), bone weights (136), and its triangles (192).
    /// </summary>
    private static ClothPhysicalMesh? Physical(ApexAsset apex, long mesh)
    {
        if (mesh < 0) return null;
        int vertices = (int)apex.U32(mesh), bonesPer = (int)apex.U32(mesh + 8);
        if (apex.Items(mesh + 16, 12) is not { Count: > 0 } positions || positions.Count != vertices) return null;
        var normalsAt = apex.Items(mesh + 64, 12) is { Count: var n } skinning && n == vertices ? skinning : apex.Items(mesh + 40, 12);
        var coefficients = apex.Items(mesh + 88, 12);
        var boneIndices = apex.Items(mesh + 112, 2);
        var boneWeights = apex.Items(mesh + 136, 4);
        var indices = apex.Items(mesh + 192, 4);
        if (coefficients.Count != vertices || indices.Count < 3) return null;
        var maxDistance = new float[vertices];
        var backstopRadius = new float[vertices];
        var backstopDistance = new float[vertices];
        for (int v = 0; v < vertices; v++)
        {
            long c = coefficients.At + v * 12L;
            maxDistance[v] = Math.Max(0, apex.F32(c));
            backstopRadius[v] = apex.F32(c + 4);
            backstopDistance[v] = Math.Max(0, apex.F32(c + 8)) + backstopRadius[v];
        }
        bool skinned = bonesPer > 0 && boneIndices.Count == vertices * bonesPer && boneWeights.Count == vertices * bonesPer;
        return new ClothPhysicalMesh
        {
            Positions = apex.Vectors((positions.At, 12, vertices), vertices),
            Normals = normalsAt.Count == vertices ? apex.Vectors((normalsAt.At, 12, vertices), vertices) : new Vector3[vertices],
            BonesPerVertex = skinned ? bonesPer : 0,
            BoneIndices = skinned ? [.. Enumerable.Range(0, boneIndices.Count).Select(i => (ushort)apex.U32(boneIndices.At + i * 2L))] : [],
            BoneWeights = skinned ? [.. Enumerable.Range(0, boneWeights.Count).Select(i => apex.F32(boneWeights.At + i * 4L))] : [],
            MaxDistance = maxDistance,
            BackstopRadius = backstopRadius,
            BackstopDistance = backstopDistance,
            Indices = [.. Enumerable.Range(0, indices.Count - indices.Count % 3).Select(i => (int)apex.U32(indices.At + i * 4L)).Select(i => i < vertices ? i : 0)],
            AverageEdgeLength = apex.F32(mesh + 228),
        };
    }

    /// <summary>
    /// A cooked fabric (<c>ClothingCookedPhysX3Param</c> 0.3): its physical mesh, submesh and particle count, then
    /// arrays of rest lengths (at 16), the pairs they keep apart (40), the sets' ends (136, 24 bytes each, its end first),
    /// the phases (160, 12 bytes: kind and set), the tethers' anchors (184) and lengths (208) and the particles'
    /// inverse masses (232).
    /// </summary>
    private static ClothFabric? Fabric(ApexAsset apex, long cooked)
    {
        int particles = (int)apex.U32(cooked + 8);
        var rest = apex.Items(cooked + 16, 4);
        var pairs = apex.Items(cooked + 40, 4);
        var sets = apex.Items(cooked + 136, 24);
        var phases = apex.Items(cooked + 160, 12);
        var anchors = apex.Items(cooked + 184, 4);
        var lengths = apex.Items(cooked + 208, 4);
        var masses = apex.Items(cooked + 232, 4);
        if (particles <= 0 || rest.Count == 0 || pairs.Count != rest.Count * 2 || masses.Count != particles || sets.Count == 0 || phases.Count == 0) return null;
        var setStarts = new int[sets.Count + 1];
        for (int s = 0; s < sets.Count; s++) setStarts[s + 1] = (int)apex.U32(sets.At + s * 24L);
        if (setStarts[^1] != rest.Count || setStarts.Zip(setStarts.Skip(1)).Any(p => p.Second < p.First)) return null;
        var phaseSets = Enumerable.Range(0, phases.Count).Select(p => (int)apex.U32(phases.At + p * 12L + 4)).ToArray();
        if (phaseSets.Any(s => s >= sets.Count)) return null;
        bool tethered = anchors.Count > 0 && anchors.Count % particles == 0 && lengths.Count == anchors.Count;
        return new ClothFabric
        {
            Particles = particles,
            PhaseTypes = [.. Enumerable.Range(0, phases.Count).Select(p => (int)apex.U32(phases.At + p * 12L))],
            PhaseSets = phaseSets,
            SetStarts = setStarts,
            RestLengths = [.. Enumerable.Range(0, rest.Count).Select(i => apex.F32(rest.At + i * 4L))],
            Pairs = [.. Enumerable.Range(0, pairs.Count).Select(i => (int)apex.U32(pairs.At + i * 4L))],
            TetherAnchors = tethered ? [.. Enumerable.Range(0, anchors.Count).Select(i => (int)apex.U32(anchors.At + i * 4L))] : [],
            TetherLengths = tethered ? [.. Enumerable.Range(0, lengths.Count).Select(i => apex.F32(lengths.At + i * 4L))] : [],
            InverseMasses = [.. Enumerable.Range(0, particles).Select(i => apex.F32(masses.At + i * 4L))],
        };
    }
}
