using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Cutscenes;

/// <summary>An actor that can be played: its body animation and the rig it moves, when one was found.</summary>
public sealed class PlayedActor
{
    public required string Name { get; init; }
    public required AnimationClip Clip { get; init; }
    public Skeleton? Rig { get; init; }

    /// <summary>The rig's file, or why there's none.</summary>
    public required string RigNote { get; init; }

    /// <summary>
    /// Where each bone is in the cutscene at a time, or null without a rig. The bones the animation doesn't move keep
    /// the rig's pose.
    /// </summary>
    public Vector3[]? Bones(float time)
    {
        if (Rig is null) return null;
        var pose = Rig.Pose.ToArray();
        Clip.Sample(time, pose);
        var model = new Matrix4x4[Rig.Count];
        Rig.ToModel(pose, model);
        return model.Select(m => m.Translation).ToArray();
    }
}

/// <summary>Where the cutscene's camera is and what it sees, at a time.</summary>
public readonly record struct CameraPose(Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView);

/// <summary>
/// What's needed to play a cutscene back as figures: each actor's body animation and rig, and the camera's path. The
/// rigs are found through the actors' entity templates. Faces aren't played, nor anything that isn't a bone.
/// </summary>
public sealed class CutscenePlayback
{
    public required float Duration { get; init; }
    public required List<PlayedActor> Actors { get; init; }

    /// <summary>The camera's animation and rig, or null when the cutscene has no camera animation.</summary>
    public PlayedActor? Camera { get; init; }

    /// <summary>Animations that couldn't be read, and why.</summary>
    public required List<string> Problems { get; init; }

    /// <summary>
    /// Reads the animations of a cutscene and finds its actors' rigs. <paramref name="path"/> is the cutscene's game
    /// path and <paramref name="owner"/> the archive it's in, for an animation that streams its data from a buffer file.
    /// </summary>
    public static CutscenePlayback Load(CutsceneFile cutscene, ResourceLocator locator, string path, IArchive? owner)
    {
        var file = cutscene.File;
        var actors = new List<PlayedActor>();
        var problems = new List<string>();
        PlayedActor? camera = null;
        var rigs = new Dictionary<string, Skeleton?>(StringComparer.OrdinalIgnoreCase);

        foreach (var export in file.Exports.Where(e => e.ClassName == "CSkeletalAnimation"))
        {
            var animation = file.Object(export.Index);
            var parts = (animation["name"] as string ?? "").Split(':', 3);
            if (parts.Length != 3 || !parts[1].Equals("Root", StringComparison.OrdinalIgnoreCase)) continue; // faces have rigs of their own
            string name = parts[0];
            AnimationClip? clip;
            try
            {
                clip = AnimationClip.Read(file, animation, n => locator.ReadBuffer(path, n, owner));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentOutOfRangeException)
            {
                problems.Add($"{name}: {ex.Message}");
                continue;
            }
            if (clip is null)
            {
                problems.Add($"{name}: its kind of animation buffer isn't read.");
                continue;
            }
            var actor = cutscene.Actors.FirstOrDefault(a => a.Name == name);
            var (rig, note) = FindRig(actor?.Template, clip.BoneCount, locator, rigs);
            var played = new PlayedActor { Name = name, Clip = clip, Rig = rig, RigNote = note };
            if (actor?.Type == "Camera" || name == "camera") camera ??= played;
            else actors.Add(played);
        }
        return new CutscenePlayback { Duration = cutscene.Duration, Actors = actors, Camera = camera, Problems = problems };
    }

    /// <summary>
    /// The rig an entity template moves: of the skeletons it and the templates it includes refer to, the one with as
    /// many bones as the animation has tracks for, else the first with at least as many.
    /// </summary>
    private static (Skeleton? Rig, string Note) FindRig(string? template, int bones, ResourceLocator locator, Dictionary<string, Skeleton?> rigs)
    {
        if (template is null) return (null, "It has no entity template, so no rig.");
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(template, 0);

        void Collect(string entity, int depth)
        {
            if (depth > 2 || !seen.Add(entity) || ReadCr2w(locator, entity) is not { } file) return;
            var files = new List<Cr2wFile> { file };
            // a cooked template keeps its components in a CR2W file of their own
            if (file.First("CEntityTemplate")?.Bytes("flatCompiledData") is { Length: > 0 } flat)
                try { files.Add(Cr2wFile.Read(flat)); }
                catch (InvalidDataException) { }
            foreach (var f in files)
                candidates.AddRange(f.Imports.Where(i => i.ClassName == "CSkeleton").Select(i => i.Path));
            foreach (var include in file.Imports.Where(i => i.ClassName == "CEntityTemplate"))
                Collect(include.Path, depth + 1);
        }

        Skeleton? fallback = null;
        string? fallbackPath = null;
        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!rigs.TryGetValue(path, out var rig))
            {
                try { rig = locator.ReadFile(path) is { } data ? Skeleton.Read(data) : null; }
                catch (InvalidDataException) { rig = null; }
                rigs[path] = rig;
            }
            if (rig is null) continue;
            if (rig.Count == bones) return (rig, path);
            if (fallback is null && rig.Count >= bones)
            {
                fallback = rig;
                fallbackPath = path;
            }
        }
        return fallback is null ? (null, candidates.Count == 0 ? "Its entity has no rig." : $"None of its entity's rigs has {bones} bones.") : (fallback, fallbackPath!);
    }

    private static Cr2wFile? ReadCr2w(ResourceLocator locator, string path)
    {
        try { return locator.ReadFile(path) is { } data ? Cr2wFile.Read(data) : null; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// The camera at a time: its last bone is the lens, which looks along its Y with Z up. Its first float track is the
    /// field of view. Null without a camera or its rig.
    /// </summary>
    public CameraPose? CameraAt(float time)
    {
        if (Camera?.Rig is not { } rig) return null;
        var pose = rig.Pose.ToArray();
        Camera.Clip.Sample(time, pose);
        var model = new Matrix4x4[rig.Count];
        rig.ToModel(pose, model);
        var lens = model[^1];
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, lens));
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, lens));
        float fov = Camera.Clip.Track(0, time, 60);
        return new CameraPose(lens.Translation, forward, up, fov is > 1 and < 170 ? fov : 60);
    }
}
