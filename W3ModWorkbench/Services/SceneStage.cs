using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Scenes;

namespace W3ModWorkbench;

/// <summary>
/// One of a scene's actors put together to be drawn: the meshes of its entity in the appearance the scene gives it,
/// skinned to its body's rig, with the animations the scene plays on it read from its animation sets.
/// </summary>
public sealed class StageActor
{
    public required string Name { get; init; }
    public required EntityFile Entity { get; init; }
    public required string? Appearance { get; init; }
    public required List<(MeshFile Mesh, Matrix4x4 Place)> Parts { get; init; }
    public required List<EntityComponent> DrawnBy { get; init; }
    public required List<(EntityFile Entity, string? Appearance)> ColoredBy { get; init; }
    public required List<(int Bone, Matrix4x4 Offset)> AttachedTo { get; init; }
    public required Skeleton Rig { get; init; }
    public required Matrix4x4[] Rest { get; init; }
    public required List<(int Bone, bool ByName)[]> Maps { get; init; }

    /// <summary>The animations the scene plays on it that were found, by name.</summary>
    public required Dictionary<string, AnimationClip> Clips { get; init; }

    /// <summary>How those of its animations that walk or turn it move it as a whole, by name.</summary>
    public Dictionary<string, MotionExtraction> Motions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Its face, when it has one: its bones are on <see cref="Rig"/> after the body's (<see cref="FaceIndex"/> says
    /// where each is), and <see cref="FaceClips"/> are the face animations the scene plays on it, by name.
    /// </summary>
    public MimicFace? Face { get; init; }
    public int[] FaceIndex { get; init; } = [];
    public Dictionary<string, AnimationClip> FaceClips { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An actor's face at a time: how far each of its controls is on, and how its face animations turn its neck and head (a
/// nod, a look up), on top of what its body does.
/// </summary>
public sealed record FaceState(float[] Weights, Quaternion Neck, Quaternion Head);

/// <summary>How an actor's face is set: the eyes, pose and idle animation the game layers on it, and how much of the pose.</summary>
public readonly record struct FaceLayers(string? Eyes, string? Pose, string? Animation, float PoseWeight)
{
    /// <summary>These layers with what another sets: an empty one keeps this one's.</summary>
    public FaceLayers With(string? eyes, string? pose, string? animation, float? poseWeight) =>
        new(Set(eyes) ?? Eyes, Set(pose) ?? Pose, Set(animation) ?? Animation, poseWeight ?? PoseWeight);

    private static string? Set(string? value) => value is { Length: > 0 } and not "None" ? value : null;

    /// <summary>The face animations the layers play: neutral_eyes_face, contempt_pose_face, neutral_anim_face…</summary>
    public IEnumerable<string> Clips()
    {
        if (Eyes is { } e) yield return $"{e.ToLowerInvariant()}_eyes_face";
        if (Pose is { } p) yield return $"{p.ToLowerInvariant()}_pose_face";
        if (Animation is { } a) yield return $"{a.ToLowerInvariant()}_anim_face";
    }
}

/// <summary>
/// Something a scene puts in the set or in an actor's hand: one of the props it defines (a chess piece, a seal), or an
/// item an actor takes up (a broom, Geralt's sword), put together from its entity to be drawn rigid.
/// </summary>
public sealed class StageProp
{
    /// <summary>Its id in the scene's events: the prop's own, or item:name for an item.</summary>
    public required string Id { get; init; }
    public required EntityFile Entity { get; init; }
    public required string? Appearance { get; init; }
    public required List<(MeshFile Mesh, Matrix4x4 Place)> Parts { get; init; }
    public required List<EntityComponent> DrawnBy { get; init; }
}

/// <summary>
/// Something a section does with a prop at a time: places it in the set, attaches it to an actor's slot (a bone, with
/// an offset), or shows or hides it. An equipped item is attached to the hand it's taken up in, and an empty hand
/// leaves (hides) the one it held.
/// </summary>
public sealed record PropEvent(float Start, string Prop, Matrix4x4? Place, string? Actor, string? Slot, Matrix4x4 Offset, bool Shown);

/// <summary>How an actor stands in a scene, as the game names its idles: High or Low, Determined or another mood, Standing or another pose.</summary>
public readonly record struct ActorStance(string Status, string Emotion, string Pose)
{
    public static readonly ActorStance Default = new("High", "Determined", "Standing");

    /// <summary>This stance with what another sets: an empty part keeps this one's.</summary>
    public ActorStance With(string? status, string? emotion, string? pose) =>
        new(Set(status) ?? Status, Set(emotion) ?? Emotion, Set(pose) ?? Pose);

    private static string? Set(string? value) => value is { Length: > 0 } and not "None" ? value : null;
}

/// <summary>
/// What an actor does in a section: where its dialog set puts it, how it stands to begin with, when it changes how it
/// stands, and the animations played on it, each at its time.
/// </summary>
public sealed class ActorTrack
{
    public required string Actor { get; init; }
    public required Matrix4x4 Place { get; init; }
    public required ActorStance Stance { get; init; }
    public required List<(float Start, float Duration, string? Transition, float ClipFront, float ClipEnd, ActorStance Then)> Changes { get; init; }
    public required List<(float Start, float Duration, string Animation, float ClipFront, float ClipEnd, float BlendIn, float BlendOut, float Weight)> Animations { get; init; }

    /// <summary>
    /// Whom or what it looks at from a time on, until the next: another actor, a point in the scene, or nothing (a look-at
    /// turned off); with how much of it turns (its body, its head or only its eyes) and how quickly.
    /// </summary>
    public required List<(float Start, string? Target, Vector3? Point, string Level, float Speed, bool Instant, bool Enabled)> LookAts { get; init; }

    /// <summary>How its face is set to begin with, and from when the face events change it.</summary>
    public required FaceLayers Face { get; init; }
    public required List<(float Start, FaceLayers Then)> FaceChanges { get; init; }

    /// <summary>The face animations played on it (a raised brow, a nod), each at its time.</summary>
    public required List<(float Start, float Duration, string Animation, float ClipFront, float ClipEnd, float BlendIn, float BlendOut, float Weight)> FaceAnimations { get; init; }

    /// <summary>The lines it says, for their lip sync.</summary>
    public required List<(float Start, float Duration, uint StringId)> Lines { get; init; }

    /// <summary>
    /// Where the section moves it from a time on (its placement events), and the placements it blends between: each a
    /// list of placements' ids, in time.
    /// </summary>
    public required List<(float Start, Vector3 Position, float Yaw, Guid Id)> Placements { get; init; }
    public required List<List<Guid>> PlacementBlends { get; init; }

    /// <summary>When it's shown or hidden.</summary>
    public required List<(float Start, bool Shown)> Visibility { get; init; }

    /// <summary>
    /// The animations added on top of what it plays (a hand flick, a shrug), each at its time, with the bones it moves
    /// and how much of each (all of them, when none are listed).
    /// </summary>
    public required List<(float Start, float Duration, string Animation, float ClipFront, float ClipEnd, float BlendIn, float BlendOut, float Weight, int[] Bones, float[] BoneWeights)> Additives { get; init; }

    /// <summary>Whether it's shown at a time.</summary>
    public bool ShownAt(float time) => Visibility.FindLastIndex(v => v.Start <= time + 1e-4f) is int at and >= 0 ? Visibility[at].Shown : true;

    /// <summary>Where it stands at a time: its dialog set's place, as its placement events move it, blended where they blend.</summary>
    public Matrix4x4 PlaceAt(float time)
    {
        if (Placements.Count == 0) return Place;
        var (position, yaw) = (Place.Translation, MathF.Atan2(Place.M12, Place.M11));
        foreach (var p in Placements)
            if (p.Start <= time + 1e-4f) (position, yaw) = (p.Position, p.Yaw);
        foreach (var blend in PlacementBlends)
        {
            var keys = blend.Select(id => Placements.FirstOrDefault(p => p.Id == id)).Where(p => p.Id != Guid.Empty).OrderBy(p => p.Start).ToList();
            for (int k = 0; k + 1 < keys.Count; k++)
            {
                var (a, b) = (keys[k], keys[k + 1]);
                if (time < a.Start || time >= b.Start || b.Start - a.Start < 1e-3f) continue;
                float t = (time - a.Start) / (b.Start - a.Start);
                t = t * t * (3 - 2 * t);
                position = Vector3.Lerp(a.Position, b.Position, t);
                float turn = MathF.IEEERemainder(b.Yaw - a.Yaw, MathF.Tau);
                yaw = a.Yaw + turn * t;
            }
        }
        return Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(position);
    }
}

/// <summary>Where a scene camera is and what it sees.</summary>
public readonly record struct StageCamera(string Name, Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView);

/// <summary>
/// A light a scene sets up (a campfire, a candle, a key or rim spot), as its last light event by a time leaves it:
/// where it is and, for a spot, which way it shines and how wide; its colour, brightness and how far it reaches.
/// </summary>
public readonly record struct StageLight(string Id, bool Spot, Vector3 Position, Vector3 Direction, System.Windows.Media.Color Color,
    float Brightness, float Radius, float Attenuation, float InnerAngle, float OuterAngle);

/// <summary>
/// A scene put on a stage: its actors put together and stood where each section's dialog set puts them, standing in the
/// idle the game picks for how they stand (from its scene_body_animations tables), with the section's body animations
/// and changes of stance played on them at their times, seen through the section's cameras. Faces, look-ats and
/// additive animations aren't played.
/// </summary>
public static class SceneStage
{
    /// <summary>
    /// The idle for each stance, from the game's scene_body_animations tables (the base game's, then each DLC's), where
    /// a row leaves blank what the row above it says.
    /// </summary>
    public static Dictionary<ActorStance, string> Idles(ResourceLocator locator)
    {
        var idles = new Dictionary<ActorStance, string>();
        var tables = locator.AllPaths(".csv").Where(p => p.EndsWith("scene_body_animations.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.StartsWith("dlc", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var path in tables)
        {
            if (locator.ReadFile(path) is not { } data) continue;
            string status = "", emotion = "", pose = "";
            foreach (var line in Encoding.UTF8.GetString(data).Split('\n').Skip(1))
            {
                var cells = line.TrimEnd('\r').Split(';');
                if (cells.Length < 6) continue;
                if (cells[0].Trim() is { Length: > 0 } s) status = s;
                if (cells[1].Trim() is { Length: > 0 } e) emotion = e;
                if (cells[2].Trim() is { Length: > 0 } p) pose = p;
                if (cells[5].Contains("Idle", StringComparison.OrdinalIgnoreCase) && cells[4].Trim() is { Length: > 0 } name)
                    idles.TryAdd(Key(new ActorStance(status, emotion, pose)), name);
            }
        }
        return idles;
    }

    /// <summary>A stance as the tables are looked up by: without case or spaces, as scenes write "Standing2" for the tables' "Standing2" and "Standing 2".</summary>
    public static ActorStance Key(ActorStance stance) =>
        new(Norm(stance.Status), Norm(stance.Emotion), Norm(stance.Pose));

    private static string Norm(string text) => text.Replace(" ", "").ToLowerInvariant();

    /// <summary>The idle a stance stands in, or null when the tables have none.</summary>
    public static string? IdleFor(Dictionary<ActorStance, string> idles, ActorStance stance) => idles.GetValueOrDefault(Key(stance));

    /// <summary>How the slot an actor stands in says it stands to begin with.</summary>
    public static ActorStance StanceOf(SceneDialogsetSlot slot) =>
        ActorStance.Default.With(slot.Source["actorStatus"] as string, slot.Source["actorEmotionalState"] as string, slot.Source["actorPoseName"] as string);

    /// <summary>
    /// The dialog set each section is played in: the one it changes to, else the one the flow brings it in with, else the
    /// scene's first.
    /// </summary>
    public static Dictionary<SceneNode, SceneDialogset?> DialogsetsBySection(SceneFile scene, List<SceneDialogset> sets)
    {
        var result = new Dictionary<SceneNode, SceneDialogset?>();
        var queue = new Queue<(SceneNode Node, SceneDialogset? Set)>(scene.Nodes.Where(n => n.Kind == SceneNodeKind.Input).Select(n => (n, (SceneDialogset?)null)));
        var seen = new HashSet<SceneNode>();
        while (queue.Count > 0)
        {
            var (node, set) = queue.Dequeue();
            if (!seen.Add(node)) continue;
            if (scene.File.Object(node.Export)["dialogsetChangeTo"] is string change && change.Length > 0 && change != "None")
                set = sets.FirstOrDefault(s => s.Name.Equals(change, StringComparison.OrdinalIgnoreCase)) ?? set;
            if (node.Kind is SceneNodeKind.Section or SceneNodeKind.Cutscene) result[node] = set ?? sets.FirstOrDefault();
            foreach (var link in node.Next)
                if (link.Target is { } next) queue.Enqueue((next, set));
        }
        foreach (var node in scene.Nodes.Where(n => n.Kind == SceneNodeKind.Section && !result.ContainsKey(n)))
            result[node] = scene.File.Object(node.Export)["dialogsetChangeTo"] is string change && sets.FirstOrDefault(s => s.Name == change) is { } named ? named : sets.FirstOrDefault();
        return result;
    }

    /// <summary>What each actor in a section's dialog set does in it.</summary>
    public static List<ActorTrack> Tracks(SceneSectionTimeline timeline, SceneDialogset? set)
    {
        var tracks = new List<ActorTrack>();
        if (set is null) return tracks;
        foreach (var slot in set.Slots.Where(s => s.Actor is { Length: > 0 }))
        {
            string actor = slot.Actor!;
            float yaw = slot.Rotation.Z * MathF.PI / 180;
            var stance = StanceOf(slot);
            var changes = new List<(float, float, string?, float, float, ActorStance)>();
            var animations = new List<(float, float, string, float, float, float, float, float)>();
            var lookAts = new List<(float, string?, Vector3?, string, float, bool, bool)>();
            var faceAnimations = new List<(float, float, string, float, float, float, float, float)>();
            var additives = new List<(float, float, string, float, float, float, float, float, int[], float[])>();
            var faceChanges = new List<(float, FaceLayers)>();
            var s = slot.Source;
            var face = new FaceLayers(null, null, null, 1).With(Named(s["actorMimicsLayer_Eyes"]), Named(s["actorMimicsLayer_Pose"]), Named(s["actorMimicsLayer_Animation"]),
                s["actorMimicsLayer_Pose_Weight"] is { } pw ? Convert.ToSingle(pw) : null);
            var faceNow = face;
            var current = stance;
            // its placement events name it as their actorName
            var placements = timeline.Events.Where(e => e.ClassName == "CStorySceneEventOverridePlacement" && e.Source["isMuted"] is not true
                    && string.Equals(e.Source["actorName"] as string, actor, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Start, SceneStaging.Transform(e.Source["placement"]), e.Source["GUID"] is Guid g ? g : Guid.NewGuid()))
                .Select(p => (p.Start, p.Item2.Position, p.Item2.Rotation.Z * MathF.PI / 180, p.Item3)).ToList();
            foreach (var e in timeline.Events.Where(e => e.Actor == actor && e.Source["isMuted"] is not true).OrderBy(e => e.Start))
            {
                var o = e.Source;
                if (e.ClassName == "CStorySceneEventAdditiveAnimation" && Named(o["animationName"]) is { } additive && e.Duration > 0)
                    additives.Add((e.Start, e.Duration, additive, o.Float("clipFront"), o.Float("clipEnd"), o.Float("blendIn"), o.Float("blendOut"), o.Float("weight", 1),
                        o.Array("bonesIdx").Select(Convert.ToInt32).ToArray(), o.Array("bonesWeight").Select(Convert.ToSingle).ToArray()));
                else if (e.ClassName == "CStorySceneEventLookAtDuration")
                {
                    // a look for a while, then back to what it looked at before
                    Vector3? point = o["type"] as string == "DLT_StaticPoint" && o["bodyStaticPointWS"] is RedObject p ? new Vector3(p.Float("X"), p.Float("Y"), p.Float("Z")) : null;
                    bool enabled = o["bodyEnabled"] is not false || o["eyesEnabled"] is true;
                    lookAts.Add((e.Start, Named(o["bodyTarget"]) ?? Named(o["eyesTarget"]), point, o["level"] as string ?? "LL_Body", o.Float("speed", 1), o["bodyInstant"] is true, enabled));
                    if (e.Duration > 0)
                    {
                        var before = lookAts.Count > 1 ? lookAts[^2] : (e.Start, null, null, "LL_Head", 1f, false, false);
                        lookAts.Add((e.Start + e.Duration, before.Item2, before.Item3, before.Item4, 1f, false, before.Item7));
                    }
                }
                else if (e.ClassName == "CStorySceneEventMimicsAnim" && Named(o["animationName"]) is { } faceAnimation && e.Duration > 0)
                    faceAnimations.Add((e.Start, e.Duration, faceAnimation, o.Float("clipFront"), o.Float("clipEnd"), o.Float("blendIn"), o.Float("blendOut"), o.Float("weight", 1)));
                else if (e.ClassName == "CStorySceneEventMimics")
                {
                    faceNow = faceNow.With(Named(o["mimicsLayer_Eyes"]), Named(o["mimicsLayer_Pose"]), Named(o["mimicsLayer_Animation"]), o["mimicsPoseWeight"] is { } w ? Convert.ToSingle(w) : null);
                    faceChanges.Add((e.Start, faceNow));
                }
                else if (e.ClassName == "CStorySceneEventLookAt")
                {
                    Vector3? point = o["type"] as string == "DLT_StaticPoint" && o["staticPoint"] is RedObject p ? new Vector3(p.Float("X"), p.Float("Y"), p.Float("Z")) : null;
                    lookAts.Add((e.Start, Named(o["target"]), point, o["level"] as string ?? "LL_Body", o.Float("speed", 1), o["instant"] is true, o["enabled"] is not false));
                }
                else if (e.ClassName == "CStorySceneEventChangePose")
                {
                    current = current.With(o["status"] as string, o["emotionalState"] as string, o["poseName"] as string);
                    changes.Add((e.Start, Math.Max(e.Duration, 0.01f), Named(o["transitionAnimation"]), o.Float("clipFront"), o.Float("clipEnd"), current));
                }
                else if (e.ClassName == "CStorySceneEventAnimation" && Named(o["animationName"]) is { } name && e.Duration > 0)
                    animations.Add((e.Start, e.Duration, name, o.Float("clipFront"), o.Float("clipEnd"), o.Float("blendIn"), o.Float("blendOut"), o.Float("weight", 1)));
            }
            tracks.Add(new ActorTrack
            {
                Actor = actor,
                Place = Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(slot.Position),
                Stance = stance,
                Changes = changes,
                Animations = animations,
                LookAts = lookAts,
                Face = face,
                FaceChanges = faceChanges,
                FaceAnimations = faceAnimations,
                Lines = timeline.Elements.Where(l => l.Kind == SceneElementKind.Line && l.StringId is not null && string.Equals(l.Speaker, actor, StringComparison.OrdinalIgnoreCase))
                    .Select(l => (l.Start, l.Duration, l.StringId!.Value)).ToList(),
                Placements = placements,
                PlacementBlends = timeline.Events.Where(e => e.ClassName == "CStorySceneEventPlacementInterpolation")
                    .Select(e => e.Source.Array("keyGuids").OfType<Guid>().ToList()).Where(k => k.Any(id => placements.Any(p => p.Item4 == id))).ToList(),
                Visibility = timeline.Events.Where(e => e.ClassName == "CStorySceneEventVisibility" && e.Actor == actor && e.Source["isMuted"] is not true)
                    .Select(e => (e.Start, e.Source["showHideFlag"] is not false)).ToList(),
                Additives = additives,
            });
        }
        return tracks;
    }

    private static string? Named(object? value) => value as string is { Length: > 0 } s && s != "None" ? s : null;

    /// <summary>
    /// Puts together each actor the scene defines that its dialog sets stand somewhere, and reads the animations
    /// <paramref name="wanted"/> names for it (its idles, changes of stance and body animations) from its animation
    /// sets. One that can't be put together is left out, with why in <paramref name="notes"/>. Reads from disk, so it
    /// belongs off the UI thread.
    /// </summary>
    public static List<StageActor> Load(SceneFile scene, Dictionary<string, HashSet<string>> wanted, ResourceLocator locator, List<string> notes, CancellationToken token)
    {
        var cast = new List<StageActor>();
        var sets = new Dictionary<string, Cr2wFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var export in scene.File.Exports.Where(x => x.ClassName == "CStorySceneActor"))
        {
            token.ThrowIfCancellationRequested();
            var definition = scene.File.Object(export.Index);
            string name = definition["id"] as string ?? "";
            if (!wanted.TryGetValue(name, out var names)) continue;
            if ((definition["entityTemplate"] is RedHandle h ? scene.File.Import(h)?.Path : null) is not { } template || locator.ReadFile(template) is not { } data)
            {
                notes.Add($"{name} isn't drawn: its entity template wasn't found.");
                continue;
            }
            EntityFile entity;
            try
            {
                entity = EntityFile.Read(data);
                // the colours of an appearance it takes from a template it includes are kept in that template
                entity.FindOrigins(locator.ReadFile);
            }
            catch (InvalidDataException ex)
            {
                notes.Add($"{name} isn't drawn: its entity could not be read ({ex.Message}).");
                continue;
            }
            // the appearance the scene asks for, else the entity's default one
            var filter = definition.Array("appearanceFilter").OfType<string>().ToList();
            var appearance = entity.Appearances.FirstOrDefault(a => filter.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) ?? Appearance(entity, name, locator);
            var look = entity.Look(appearance, locator.ReadFile);
            var (parts, drawnBy, _, missing) = EntityWindow.ReadMeshes(look, locator, token);
            if (BodyRig(look, parts, locator) is not { } body)
            {
                notes.Add($"{name} isn't drawn: its body's rig wasn't found.");
                continue;
            }
            // its face's bones go on the body's rig, for the head to be skinned to them
            var face = Face(look, CutsceneCast.IsPlayer(template), locator);
            var (rig, faceIndex) = face is null ? (body, []) : Merge(body, face.Skeleton);
            // and the small rigs hung on it, for what's skinned to them (the player's scabbards)
            rig = WithCarriedRigs(rig, look, locator);
            var coloredBy = parts.Select(_ => (entity, appearance?.Name)).ToList();
            var attachedTo = CutsceneCast.AttachToRig(look, parts, drawnBy, rig);
            if (CutsceneCast.IsPlayer(template) && CutsceneCast.Dress(entity, look, rig, locator, parts, drawnBy, coloredBy, attachedTo, token))
                notes.Add($"{name} is the player, whom the game dresses in what's equipped: shown in Geralt's default gear.");
            if (parts.Count == 0)
            {
                notes.Add($"{name} isn't drawn: " + (missing > 0 ? "its meshes could not be read." : "its entity has no meshes."));
                continue;
            }
            var rest = new Matrix4x4[rig.Count];
            rig.ToModel(rig.Pose, rest);
            var animationSets = AnimationSets(template, locator);
            var bodyNames = names.Where(n => !n.EndsWith("_face", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var faceNames = names.Where(n => n.EndsWith("_face", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var motions = new Dictionary<string, MotionExtraction>(StringComparer.OrdinalIgnoreCase);
            var clips = Clips(animationSets.Where(p => !IsFaceSet(p)).ToList(), bodyNames, rig, locator, sets, token, motions);
            var faceClips = face is null ? new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase)
                : Clips(animationSets.Where(IsFaceSet).ToList(), faceNames, null, locator, sets, token);
            var notFound = bodyNames.Where(n => !clips.ContainsKey(n)).ToList();
            if (notFound.Count > 0) notes.Add($"{name}: {Format.Count(notFound.Count, "animation")} not found in its animation sets ({string.Join(", ", notFound.Take(3))}{(notFound.Count > 3 ? "…" : "")}).");
            cast.Add(new StageActor
            {
                Name = name,
                Entity = entity,
                Appearance = appearance?.Name,
                Parts = parts,
                DrawnBy = drawnBy,
                ColoredBy = coloredBy,
                AttachedTo = attachedTo,
                Rig = rig,
                Rest = rest,
                Maps = parts.Select(p => Skinning.MapBones(p.Mesh, rig, rest)).ToList(),
                Clips = clips,
                Motions = motions,
                Face = face,
                FaceIndex = faceIndex,
                FaceClips = faceClips,
            });
        }
        return cast;
    }

    private static bool IsFaceSet(string path) => path.Contains(@"\mimics\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An actor's face: the one its mimic component names, or for the player, whose head is put on with his gear,
    /// Geralt's. Null when it has none or it can't be read.
    /// </summary>
    /// <summary>
    /// The appearance an actor is seen in when nothing asks for one: the one the entity gives its voice tag (a scene
    /// actor's id is its voice tag; a crowd entity maps each voice tag it speaks with to an appearance, and its first
    /// appearance may be a headless one made for a scene that hides the face), else the default one, else, when that has
    /// no head, the first that has.
    /// </summary>
    public static EntityAppearance? Appearance(EntityFile entity, string? voiceTag, ResourceLocator locator)
    {
        if (voiceTag is { Length: > 0 } && VoiceTagAppearance(entity, voiceTag, locator) is { } named
            && entity.Appearances.FirstOrDefault(a => a.Name.Equals(named, StringComparison.OrdinalIgnoreCase)) is { } found)
            return found;
        var appearance = entity.DefaultAppearance;
        if (appearance is null || HasHead(appearance)) return appearance;
        return entity.Appearances.FirstOrDefault(a => HasHead(a)) ?? appearance;
    }

    /// <summary>The appearance a template (or one it includes) gives a voice tag in its voicetagAppearances, or null.</summary>
    private static string? VoiceTagAppearance(EntityFile entity, string voiceTag, ResourceLocator locator)
    {
        string? From(RedObject template) => template.Array("voicetagAppearances").OfType<RedObject>()
            .FirstOrDefault(v => string.Equals(v["voicetag"] as string, voiceTag, StringComparison.OrdinalIgnoreCase))?["appearance"] as string;
        if (From(entity.Template) is { } own) return own;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(entity.Includes);
        while (queue.Count > 0 && seen.Count < 16)
        {
            string path = queue.Dequeue();
            if (!seen.Add(path)) continue;
            try
            {
                if (locator.ReadFile(path) is not { } data) continue;
                var included = EntityFile.Read(data);
                if (From(included.Template) is { } named) return named;
                foreach (var next in included.Includes) queue.Enqueue(next);
            }
            catch (InvalidDataException)
            {
                // one that can't be read is passed over
            }
        }
        return null;
    }

    /// <summary>Whether an appearance puts a head on: one of its templates is a head (heads\h_…) or has a face.</summary>
    private static bool HasHead(EntityAppearance appearance) =>
        appearance.Templates.Any(t => t.Contains(@"\heads\", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(t).StartsWith("h_", StringComparison.OrdinalIgnoreCase));

    public static MimicFace? Face(EntityLook look, bool player, ResourceLocator locator)
    {
        string? path = look.Components.SelectMany(c => c.Resources).FirstOrDefault(r => r.EndsWith(".w3fac", StringComparison.OrdinalIgnoreCase))
            ?? (player ? @"characters\models\geralt\head\model\h_01_mg__geralt.w3fac" : null);
        try
        {
            return path is not null && locator.ReadFile(path) is { } data ? MimicFace.Read(data) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The body's rig with a face's bones after its own: a bone the body has (the neck, the head) is the body's, and the
    /// rest hang where the face has them. Says where each of the face's bones is on it.
    /// </summary>
    /// <summary>
    /// The rig with the small rigs the look hangs on its bones added (the player's scabbards_skeleton on torso3, which his
    /// scabbards are skinned to): each one's bones carried by the bone it hangs on, where its component puts it there.
    /// Bones the rig has already are left as they are.
    /// </summary>
    public static Skeleton WithCarriedRigs(Skeleton rig, EntityLook look, ResourceLocator locator)
    {
        foreach (var link in look.Links.Where(l => l.Kind == "Hard" && l.Slot is not null && l.Child.ClassName == "CAnimatedComponent"))
        {
            int carrier = Bone(rig, link.Slot!);
            string? path = link.Child.Resources.FirstOrDefault(r => r.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase));
            if (carrier < 0 || path is null) continue;
            Skeleton small;
            try
            {
                if (locator.ReadFile(path) is not { } data) continue;
                small = Skeleton.Read(data);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (small.Names.Any(n => Bone(rig, n) >= 0 && !n.Equals("Root", StringComparison.OrdinalIgnoreCase))) continue;
            var names = rig.Names.ToList();
            var parents = rig.Parents.ToList();
            var pose = rig.Pose.ToList();
            var index = new int[small.Count];
            // the small rig's root is where the component puts it on the carrier: its children hang from the carrier
            var onCarrier = link.Relative * link.Child.Local;
            for (int i = 0; i < small.Count; i++)
            {
                if (small.Parents[i] < 0)
                {
                    index[i] = carrier;
                    continue;
                }
                var local = small.Pose[i];
                int parent = small.Parents[i];
                if (small.Parents[parent] < 0)
                {
                    var root = small.Pose[parent];
                    var m = Matrix4x4.CreateScale(local.Scale) * Matrix4x4.CreateFromQuaternion(local.Rotation) * Matrix4x4.CreateTranslation(local.Position)
                        * Matrix4x4.CreateScale(root.Scale) * Matrix4x4.CreateFromQuaternion(root.Rotation) * Matrix4x4.CreateTranslation(root.Position) * onCarrier;
                    if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var position)) local = new BoneTransform(position, rotation, scale);
                }
                names.Add(small.Names[i]);
                parents.Add((short)index[parent]);
                pose.Add(local);
                index[i] = names.Count - 1;
            }
            rig = new Skeleton { Names = [.. names], Parents = [.. parents], Pose = [.. pose] };
        }
        return rig;
    }

    public static (Skeleton Rig, int[] FaceIndex) Merge(Skeleton body, Skeleton face)
    {
        var names = body.Names.ToList();
        var parents = body.Parents.ToList();
        var pose = body.Pose.ToList();
        var index = new int[face.Count];
        for (int i = 0; i < face.Count; i++)
        {
            int own = Array.FindIndex(body.Names, n => n.Equals(face.Names[i], StringComparison.OrdinalIgnoreCase));
            if (own >= 0)
            {
                index[i] = own;
                continue;
            }
            names.Add(face.Names[i]);
            parents.Add((short)(face.Parents[i] >= 0 ? index[face.Parents[i]] : -1));
            pose.Add(face.Pose[i]);
            index[i] = names.Count - 1;
        }
        return (new Skeleton { Names = [.. names], Parents = [.. parents], Pose = [.. pose] }, index);
    }

    /// <summary>
    /// How far each of an actor's face controls is on at a time: the face layers it's set to (its eyes, its pose, as
    /// far as its weight says, and its idle animation, which blinks), the face animations playing then, and the lip
    /// sync of the line it's saying. Null without a face.
    /// </summary>
    public static FaceState? FaceWeights(StageActor actor, ActorTrack track, float time, Func<uint, LipSync?> lipSyncOf)
    {
        if (actor.Face is not { } face) return null;
        var weights = new float[face.Controls.Length];
        Quaternion neck = Quaternion.Identity, head = Quaternion.Identity;
        var turns = new BoneTransform[2];
        void Add(AnimationClip clip, float at, float weight)
        {
            for (int c = 0; c < weights.Length && c < clip.TrackCount; c++) weights[c] += clip.Track(c, at) * weight;
            // a face animation's two bones are the neck and head, as turns from where they stand
            if (clip.BoneCount < 2) return;
            turns[0] = turns[1] = BoneTransform.Identity;
            clip.Sample(at, turns);
            neck = Quaternion.Normalize(neck * Quaternion.Slerp(Quaternion.Identity, turns[0].Rotation, weight));
            head = Quaternion.Normalize(head * Quaternion.Slerp(Quaternion.Identity, turns[1].Rotation, weight));
        }
        var layers = track.Face;
        foreach (var change in track.FaceChanges)
            if (change.Start <= time + 1e-4f) layers = change.Then;
        if (layers.Eyes is { } eyes && actor.FaceClips.TryGetValue($"{eyes.ToLowerInvariant()}_eyes_face", out var e)) Add(e, Loop(e, time), 1);
        if (layers.Pose is { } pose && actor.FaceClips.TryGetValue($"{pose.ToLowerInvariant()}_pose_face", out var p)) Add(p, 0, Math.Clamp(layers.PoseWeight, 0, 1));
        if (layers.Animation is { } idle && actor.FaceClips.TryGetValue($"{idle.ToLowerInvariant()}_anim_face", out var a)) Add(a, Loop(a, time), 1);
        foreach (var f in track.FaceAnimations)
        {
            if (time < f.Start || time > f.Start + f.Duration || !actor.FaceClips.TryGetValue(f.Animation, out var clip)) continue;
            float weight = Math.Clamp(f.Weight, 0, 1);
            if (f.BlendIn > 0) weight *= Math.Clamp((time - f.Start) / f.BlendIn, 0, 1);
            if (f.BlendOut > 0) weight *= Math.Clamp((f.Start + f.Duration - time) / f.BlendOut, 0, 1);
            if (weight > 0) Add(clip, Local(clip, time, f.Start, f.Duration, f.ClipFront, f.ClipEnd), weight);
        }
        foreach (var line in track.Lines)
            if (time >= line.Start && time <= line.Start + line.Duration + 0.5f && lipSyncOf(line.StringId) is { } lips && time - line.Start <= lips.Duration)
                for (int c = 0; c < weights.Length && c < lips.TrackCount; c++) weights[c] += lips.Value(c, time - line.Start);
        for (int c = 0; c < weights.Length; c++) weights[c] = Math.Clamp(weights[c], -1, 1);
        return new FaceState(weights, neck, head);
    }

    private static float Loop(AnimationClip clip, float time) => clip.Duration > 0 ? time % clip.Duration : 0;

    /// <summary>
    /// Puts together the props a scene defines, and the items its sections have actors take up (<paramref name="items"/>,
    /// by name), from their entities. One that can't be put together is left out, with why in <paramref name="notes"/>.
    /// </summary>
    public static List<StageProp> LoadProps(SceneFile scene, IEnumerable<string> items, ResourceLocator locator, List<string> notes, CancellationToken token)
    {
        var wanted = scene.File.Exports.Where(x => x.ClassName == "CStorySceneProp").Select(x => scene.File.Object(x.Index))
            .Select(o => (Id: o["id"] as string ?? "", Template: o["entityTemplate"] is RedHandle h ? scene.File.Import(h)?.Path : null))
            .Concat(items.Distinct(StringComparer.OrdinalIgnoreCase).Select(i => (Id: "item:" + i, Template: PlayerGear.Find(locator, i))))
            .Where(p => p.Id.Length > 0).ToList();
        var props = new List<StageProp>();
        foreach (var (id, template) in wanted)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (template is null || locator.ReadFile(template) is not { } data)
                {
                    notes.Add($"The prop {id} isn't drawn: its entity wasn't found.");
                    continue;
                }
                var entity = EntityFile.Read(data);
                entity.FindOrigins(locator.ReadFile);
                var appearance = entity.DefaultAppearance;
                var look = entity.Look(appearance, locator.ReadFile);
                var (parts, drawnBy, _, _) = EntityWindow.ReadMeshes(look, locator, token);
                if (parts.Count == 0) notes.Add($"The prop {id} isn't drawn: its entity has no meshes (an effect, a light).");
                else props.Add(new StageProp { Id = id, Entity = entity, Appearance = appearance?.Name, Parts = parts, DrawnBy = drawnBy });
            }
            catch (InvalidDataException ex)
            {
                notes.Add($"The prop {id} isn't drawn: {ex.Message}");
            }
        }
        return props;
    }

    /// <summary>The items a section's actors take up, by name.</summary>
    public static IEnumerable<string> ItemsTaken(SceneSectionTimeline timeline) =>
        timeline.Events.Where(e => e.ClassName == "CStorySceneEventEquipItem")
            .SelectMany(e => new[] { Named(e.Source["rightItem"]), Named(e.Source["leftItem"]) }).OfType<string>();

    /// <summary>What a section does with its props and its actors' items, in time.</summary>
    public static List<PropEvent> PropEvents(SceneSectionTimeline timeline)
    {
        var events = new List<PropEvent>();
        foreach (var e in timeline.Events.Where(e => e.Source["isMuted"] is not true).OrderBy(e => e.Start))
        {
            var o = e.Source;
            Matrix4x4 TransformOf(object? raw)
            {
                var (position, rotation) = SceneStaging.Transform(raw);
                return Matrix4x4.CreateRotationZ(rotation.Z * MathF.PI / 180) * Matrix4x4.CreateTranslation(position);
            }
            switch (e.ClassName)
            {
                case "CStorySceneEventScenePropPlacement" when Named(o["propId"]) is { } prop:
                    events.Add(new PropEvent(e.Start, prop, TransformOf(o["placement"]), null, null, Matrix4x4.Identity, o["showHide"] is not false));
                    break;
                case "CStorySceneEventAttachPropToSlot" when Named(o["propId"]) is { } prop:
                    // let go (activate false) leaves it where it is
                    events.Add(o["activate"] is false
                        ? new PropEvent(e.Start, prop, null, null, null, Matrix4x4.Identity, o["showHide"] is not false)
                        : new PropEvent(e.Start, prop, null, o["actorName"] as string, Named(o["slotName"]), TransformOf(o["offset"]), o["showHide"] is not false));
                    break;
                case "CStorySceneEventPropVisibility" when Named(o["propID"]) is { } prop:
                    events.Add(new PropEvent(e.Start, prop, null, null, null, Matrix4x4.Identity, o["showHide"] is not false && o["visible"] is not false));
                    break;
                case "CStorySceneEventEquipItem" when e.Actor is { } actor:
                    foreach (var (item, hand) in new[] { (Named(o["rightItem"]), "r_weapon"), (Named(o["leftItem"]), "l_weapon") })
                    {
                        // the item taken up goes in the hand; what the hand held before is put away
                        foreach (var held in events.Where(x => x.Actor == actor && x.Slot == hand).Select(x => x.Prop).Distinct().ToList())
                            if (held != "item:" + item) events.Add(new PropEvent(e.Start, held, null, null, null, Matrix4x4.Identity, false));
                        if (item is not null) events.Add(new PropEvent(e.Start, "item:" + item, null, actor, hand, Matrix4x4.Identity, true));
                    }
                    break;
            }
        }
        return events;
    }

    /// <summary>
    /// Where a prop is at a time, or null when it isn't shown: as its last placement put it, or on the actor's slot it
    /// was last attached to as that actor's bones are then (<paramref name="bonesOf"/>). One the section hasn't shown yet isn't.
    /// </summary>
    public static Matrix4x4? PropPlace(StageProp prop, List<PropEvent> events, float time, Func<string, (StageActor Actor, Matrix4x4[] Bones)?> bonesOf)
    {
        Matrix4x4? world = null;
        (string Actor, string? Slot, Matrix4x4 Offset)? held = null;
        bool shown = false;
        foreach (var e in events)
        {
            if (e.Start > time + 1e-4f) break;
            if (!e.Prop.Equals(prop.Id, StringComparison.OrdinalIgnoreCase)) continue;
            shown = e.Shown;
            if (e.Place is { } place) (world, held) = (place, null);
            else if (e.Actor is { } actor) held = (actor, e.Slot, e.Offset);
            else if (held is { } letGo && bonesOf(letGo.Actor) is { } holder && Slot(holder.Actor, letGo.Slot) is int bone) (world, held) = (letGo.Offset * holder.Bones[bone], null);
        }
        if (!shown) return null;
        if (held is { } h)
            return bonesOf(h.Actor) is { } on && Slot(on.Actor, h.Slot) is int b ? h.Offset * on.Bones[b] : null;
        return world;
    }

    /// <summary>The bone a slot is on: the entity's slot of that name, else the rig's bone of that name (r_weapon, head), else its nearest, without _effect.</summary>
    private static int? Slot(StageActor actor, string? slot)
    {
        if (slot is null) return null;
        var named = actor.Entity.Slots.FirstOrDefault(s => s.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
        foreach (var name in new[] { named?.Bone, slot, slot.Replace("_effect", ""), slot.EndsWith("_slot", StringComparison.OrdinalIgnoreCase) ? slot[..^5] : null })
            if (name is { Length: > 0 } && Bone(actor.Rig, name) is int b and >= 0) return b;
        return null;
    }

    /// <summary>
    /// The body's rig: of the rigs the look's components use (not a dangle's, which is a hood's or hair's), the one most
    /// of its skinned meshes' bones are named in.
    /// </summary>
    private static Skeleton? BodyRig(EntityLook look, List<(MeshFile Mesh, Matrix4x4 Place)> parts, ResourceLocator locator)
    {
        static bool IsRig(string r) => r.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase);
        var rigs = look.Components.Where(c => !c.ClassName.Contains("Dangle", StringComparison.Ordinal)).SelectMany(c => c.Resources).Where(IsRig)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Skeleton? best = null;
        int bestNamed = -1;
        foreach (var path in rigs)
        {
            try
            {
                if (locator.ReadFile(path) is not { } data) continue;
                var rig = Skeleton.Read(data);
                var names = new HashSet<string>(rig.Names, StringComparer.OrdinalIgnoreCase);
                int named = parts.Sum(p => p.Mesh.BoneNames.Count(names.Contains));
                if (named > bestNamed) (best, bestNamed) = (rig, named);
            }
            catch (InvalidDataException)
            {
                // not a rig that can be read; another may do
            }
        }
        return best;
    }

    /// <summary>
    /// The animation sets an entity template names, and those of the templates it includes: the dialog sets first, as a
    /// scene's animations are mostly in them. A face's sets are left out, as they move the face's own rig.
    /// </summary>
    private static List<string> AnimationSets(string template, ResourceLocator locator)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string path, int depth)
        {
            if (depth > 3 || !seen.Add(path)) return;
            Cr2wFile file;
            try
            {
                if (locator.ReadFile(path) is not { } data) return;
                file = Cr2wFile.Read(data);
            }
            catch (InvalidDataException)
            {
                return;
            }
            found.AddRange(file.Imports.Where(i => i.ClassName == "CSkeletalAnimationSet").Select(i => i.Path));
            foreach (var include in file.Imports.Where(i => i.ClassName == "CEntityTemplate")) Walk(include.Path, depth + 1);
        }
        Walk(template, 0);
        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p).Contains("dialog", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }

    /// <summary>Reads the animations named from the sets, stopping once all are found. One made for a bigger rig is left out.</summary>
    private static Dictionary<string, AnimationClip> Clips(List<string> setPaths, HashSet<string> names, Skeleton? rig, ResourceLocator locator,
        Dictionary<string, Cr2wFile?> sets, CancellationToken token, Dictionary<string, MotionExtraction>? motions = null)
    {
        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in setPaths)
        {
            if (clips.Count == names.Count) break;
            token.ThrowIfCancellationRequested();
            if (!sets.TryGetValue(path, out var set))
            {
                try { set = locator.ReadFile(path) is { } data ? Cr2wFile.Read(data) : null; }
                catch (InvalidDataException) { set = null; }
                sets[path] = set;
            }
            if (set is null) continue;
            var archive = locator.Find(path)?.Archive;
            foreach (var x in set.Exports.Where(x => x.ClassName == "CSkeletalAnimation"))
            {
                var animation = set.Object(x.Index);
                if (animation["name"] is not string name || !names.Contains(name) || clips.ContainsKey(name)) continue;
                try
                {
                    if (AnimationClip.Read(set, animation, n => locator.ReadBuffer(path, n, archive)) is { } clip && (rig is null || clip.BoneCount <= rig.Count))
                    {
                        clips[name] = clip;
                        if (motions is not null && rig is not null && MotionExtraction.Read(set, animation) is { } motion && StaysInPlace(rig, clip, motion)) motions[name] = motion;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
                {
                    // one that can't be read is left out
                }
            }
        }
        return clips;
    }

    /// <summary>
    /// Where an actor's bones are at a time in a section: in the idle of how it stands then (or its change of stance
    /// while one plays), with the body animations playing then blended over it by their weights and blends, stood where
    /// its dialog set puts it.
    /// </summary>
    public static Matrix4x4[] Bones(StageActor actor, ActorTrack track, Dictionary<ActorStance, string> idles, float time, FaceState? face = null)
    {
        var rig = actor.Rig;
        var pose = rig.Pose.ToArray();
        var place = Walked(actor, track, time) * track.PlaceAt(time);
        // how it stands at the time, and since when
        var stance = track.Stance;
        float since = 0;
        (float Start, float Duration, string? Transition, float ClipFront, float ClipEnd, ActorStance Then)? changing = null;
        foreach (var change in track.Changes)
        {
            if (time >= change.Start + change.Duration) (stance, since) = (change.Then, change.Start + change.Duration);
            else if (time >= change.Start) changing = change;
        }
        if (changing is { Transition: { } transition } c && actor.Clips.TryGetValue(transition, out var moving))
        {
            moving.Sample(Local(moving, time, c.Start, c.Duration, c.ClipFront, c.ClipEnd), pose);
            Settle(rig, pose, actor.Motions.ContainsKey(transition) ? moving : null);
        }
        else if (IdleFor(idles, stance) is { } idle && actor.Clips.TryGetValue(idle, out var standing) && standing.Duration > 0)
        {
            standing.Sample((time - since) % standing.Duration, pose);
            Settle(rig, pose, actor.Motions.ContainsKey(idle) ? standing : null);
        }
        else Settle(rig, pose, null);

        var layer = new BoneTransform[pose.Length];
        foreach (var a in track.Animations)
        {
            if (time < a.Start || time > a.Start + a.Duration || !actor.Clips.TryGetValue(a.Animation, out var clip)) continue;
            float weight = Math.Clamp(a.Weight, 0, 1);
            if (a.BlendIn > 0) weight *= Math.Clamp((time - a.Start) / a.BlendIn, 0, 1);
            if (a.BlendOut > 0) weight *= Math.Clamp((a.Start + a.Duration - time) / a.BlendOut, 0, 1);
            if (weight <= 0) continue;
            pose.CopyTo(layer, 0);
            clip.Sample(Local(clip, time, a.Start, a.Duration, a.ClipFront, a.ClipEnd), layer);
            Settle(rig, layer, actor.Motions.ContainsKey(a.Animation) ? clip : null);
            for (int b = 0; b < pose.Length; b++)
                pose[b] = new BoneTransform(Vector3.Lerp(pose[b].Position, layer[b].Position, weight),
                    Quaternion.Slerp(pose[b].Rotation, layer[b].Rotation, weight), Vector3.Lerp(pose[b].Scale, layer[b].Scale, weight));
        }
        foreach (var a in track.Additives)
        {
            if (time < a.Start || time > a.Start + a.Duration || !actor.Clips.TryGetValue(a.Animation, out var clip)) continue;
            float weight = Math.Clamp(a.Weight, 0, 1);
            if (a.BlendIn > 0) weight *= Math.Clamp((time - a.Start) / a.BlendIn, 0, 1);
            if (a.BlendOut > 0) weight *= Math.Clamp((a.Start + a.Duration - time) / a.BlendOut, 0, 1);
            if (weight > 0) Add(rig, pose, clip, Local(clip, time, a.Start, a.Duration, a.ClipFront, a.ClipEnd), a.ClipFront, weight, a.Bones, a.BoneWeights);
        }
        if (face is not null)
        {
            actor.Face?.Apply(face.Weights, pose, actor.FaceIndex);
            // its face animations' nods and looks, on the bone's own
            foreach (var (name, turn) in new[] { ("neck", face.Neck), ("head", face.Head) })
                if (Bone(rig, name) is int b and >= 0 && b < pose.Length) pose[b] = pose[b] with { Rotation = Quaternion.Normalize(pose[b].Rotation * turn) };
        }
        var model = new Matrix4x4[rig.Count];
        rig.ToModel(pose, model);
        for (int i = 0; i < model.Length; i++) model[i] *= place;
        return model;
    }

    /// <summary>
    /// Whether an animation with motion extraction keeps its body in place for the game to move the actor along it:
    /// its root walks back as far as its body walks on (an exit passing to one side). Some keep the walk in the body
    /// instead, their root still (an exit that turns and walks away), and moving the actor as well would walk it twice
    /// as far. Told by where the pelvis gets to by the end, with the root as animated, against how far the extraction goes.
    /// </summary>
    private static bool StaysInPlace(Skeleton rig, AnimationClip clip, MotionExtraction motion)
    {
        int pelvis = Bone(rig, "pelvis");
        if (pelvis < 0 || clip.BoneCount <= pelvis) return false;
        Vector3 Pelvis(float t)
        {
            var pose = rig.Pose.ToArray();
            clip.Sample(t, pose);
            var model = new Matrix4x4[rig.Count];
            rig.ToModel(pose, model);
            return model[pelvis].Translation;
        }
        var bodyWent = Pelvis(clip.Duration) - Pelvis(0);
        var extracted = motion.At(clip.Duration).Position - motion.At(0).Position;
        float far = new Vector2(extracted.X, extracted.Y).Length();
        // a turn on the spot moves little either way: the extraction turns the actor, and the body is taken as turning with it
        if (far < 0.25f) return true;
        return new Vector2(bodyWent.X, bodyWent.Y).Length() < far * 0.5f;
    }

    /// <summary>
    /// How far the actor's walks and turns have taken it from where it was last placed, by the time given: each body
    /// animation or change of stance since that moves it (by its motion extraction) carries it on from where the one
    /// before left it, from where the event starts its clip to where it has got to. Put before where it stands.
    /// </summary>
    private static Matrix4x4 Walked(StageActor actor, ActorTrack track, float time)
    {
        if (actor.Motions.Count == 0) return Matrix4x4.Identity;
        float since = track.Placements.Where(p => p.Start <= time + 1e-4f).Select(p => p.Start).DefaultIfEmpty(float.MinValue).Max();
        var moves = track.Animations.Select(a => (a.Start, a.Duration, Name: a.Animation, a.ClipFront, a.ClipEnd))
            .Concat(track.Changes.Where(c => c.Transition is not null).Select(c => (c.Start, c.Duration, Name: c.Transition!, c.ClipFront, c.ClipEnd)))
            .Where(m => m.Start >= since - 1e-4f && m.Start <= time && actor.Motions.ContainsKey(m.Name) && actor.Clips.ContainsKey(m.Name))
            .OrderBy(m => m.Start);
        var walked = Matrix4x4.Identity;
        foreach (var m in moves)
        {
            var clip = actor.Clips[m.Name];
            float from = Local(clip, m.Start, m.Start, m.Duration, m.ClipFront, m.ClipEnd), to = Local(clip, Math.Min(time, m.Start + m.Duration), m.Start, m.Duration, m.ClipFront, m.ClipEnd);
            walked = actor.Motions[m.Name].Between(from, to) * walked;
        }
        return walked;
    }

    /// <summary>
    /// Adds an animation on top of a pose, as far as a weight says: what it does to each bone, from where it starts (one
    /// saved as only what it adds, with its bones near nowhere, is that; one saved as whole poses is taken from its own
    /// frame where the event starts it), turned and moved in on the bone's own. Only the bones listed, as much as each says, when it lists them; never the root or trajectory.
    /// </summary>
    private static void Add(Skeleton rig, BoneTransform[] pose, AnimationClip clip, float at, float from, float weight, int[] bones, float[] boneWeights)
    {
        var sampled = rig.Pose.ToArray();
        clip.Sample(at, sampled);
        int count = Math.Min(Math.Min(clip.BoneCount, pose.Length), sampled.Length);
        // saved as what it adds when its bones sit at about nothing where the rig's stand away from their parents
        float own = 0, rigs = 0;
        for (int b = 1; b < count; b++) (own, rigs) = (own + sampled[b].Position.Length(), rigs + rig.Pose[b].Position.Length());
        BoneTransform[]? reference = null;
        if (own > 0.2f * rigs)
        {
            reference = rig.Pose.ToArray();
            clip.Sample(from, reference);
        }
        float[]? only = null;
        if (bones.Length > 0)
        {
            only = new float[count];
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] >= 0 && bones[i] < count) only[bones[i]] = i < boneWeights.Length ? boneWeights[i] : 1;
        }
        int trajectory = Bone(rig, "Trajectory");
        for (int b = 1; b < count; b++)
        {
            if (b == trajectory) continue;
            float w = weight * (only?[b] ?? 1);
            if (w <= 0) continue;
            var turn = reference is null ? sampled[b].Rotation : Quaternion.Inverse(reference[b].Rotation) * sampled[b].Rotation;
            var move = reference is null ? sampled[b].Position : sampled[b].Position - reference[b].Position;
            pose[b] = pose[b] with
            {
                Rotation = Quaternion.Normalize(pose[b].Rotation * Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(turn), w)),
                Position = pose[b].Position + move * w,
            };
        }
    }

    /// <summary>
    /// Settles an animation's root as the game has it, before it's blended with the others. The root is put back
    /// upright, keeping only the turn round the upright it makes: a standing idle has its root lying on its back, which
    /// is the export's and not a turn, while a kneeling one has its root turned round, its body made facing the other way
    /// to match. One that walks or turns the actor (<paramref name="walking"/>, with motion extraction) also keeps how
    /// its root has gone since the clip began, over the ground and round the upright: it walks back as far as the body
    /// walks on, so the body stays where the actor is, and <see cref="Walked"/> moves the actor instead. The trajectory
    /// (the engine's handle for where the actor goes) is always as the rig has it.
    /// </summary>
    private static void Settle(Skeleton rig, BoneTransform[] pose, AnimationClip? walking)
    {
        if (rig.Count == 0 || rig.Parents[0] >= 0) return;
        var root = pose[0];
        var settled = Upright(rig, walking is null ? root : FirstRoot(rig, walking));
        if (walking is not null)
        {
            var first = FirstRoot(rig, walking);
            // how the root has turned since the clip began, round the upright only, and gone over the ground
            var turned = root.Rotation * Quaternion.Inverse(first.Rotation);
            var ahead = Vector3.Transform(Vector3.UnitY, turned);
            float yaw = ahead.X * ahead.X + ahead.Y * ahead.Y > 1e-6f ? MathF.Atan2(-ahead.X, ahead.Y) : 0;
            var gone = root.Position - first.Position;
            settled = settled with
            {
                Rotation = Quaternion.Concatenate(settled.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw)),
                Position = settled.Position + new Vector3(gone.X, gone.Y, 0),
            };
        }
        pose[0] = settled;
        int trajectory = Array.FindIndex(rig.Names, n => n.Equals("Trajectory", StringComparison.OrdinalIgnoreCase));
        if (trajectory > 0) pose[trajectory] = rig.Pose[trajectory];
    }

    /// <summary>The rig's root, turned round the upright as a root turns it when it stands upright, and not otherwise.</summary>
    private static BoneTransform Upright(Skeleton rig, BoneTransform root)
    {
        var turn = Quaternion.Identity;
        var up = Vector3.Transform(Vector3.UnitZ, root.Rotation);
        var ahead = Vector3.Transform(Vector3.UnitY, root.Rotation);
        if (up.Z > 0.7f && ahead.X * ahead.X + ahead.Y * ahead.Y > 1e-6f)
            turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(-ahead.X, ahead.Y));
        return rig.Pose[0] with { Rotation = Quaternion.Concatenate(rig.Pose[0].Rotation, turn) };
    }

    // each walking clip's root where it begins, read once
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnimationClip, StrongBox<BoneTransform>> FirstRoots = [];

    private static BoneTransform FirstRoot(Skeleton rig, AnimationClip clip) => FirstRoots.GetValue(clip, c =>
    {
        var pose = rig.Pose.ToArray();
        c.Sample(0, pose);
        return new StrongBox<BoneTransform>(pose[0]);
    }).Value;

    /// <summary>
    /// Turns an actor to look at what its look-at then says, from what it looked at before: its head and neck, and for
    /// a body look-at its upper back too, each taking a share of the turn, eased in at its speed (or at once). The turn
    /// is kept to 80 degrees, so it doesn't look behind itself. <paramref name="headOf"/> says where another actor's head is.
    /// </summary>
    public static void LookAt(StageActor actor, ActorTrack track, Matrix4x4[] model, float time, Func<string, Vector3?> headOf)
    {
        int now = track.LookAts.FindLastIndex(l => l.Start <= time + 1e-4f);
        var rig = actor.Rig;
        int head = Bone(rig, "head");
        if (now < 0 || head < 0) return;
        var look = track.LookAts[now];
        var from = model[head].Translation;
        // which way the head faces now: the way it faces in the rig's pose (as the actor does, along Y), as the animation turns it
        var ahead = Matrix4x4.Invert(actor.Rest[head], out var back)
            ? Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitY, back), model[head]))
            : Vector3.UnitY;
        Vector3 Toward((float Start, string? Target, Vector3? Point, string Level, float Speed, bool Instant, bool Enabled) l) =>
            !l.Enabled ? ahead
            : (l.Point ?? (l.Target is { } t ? headOf(t) : null)) is { } at && Vector3.DistanceSquared(at, from) > 1e-4f ? Vector3.Normalize(at - from) : ahead;
        float eased = look.Instant ? 1 : Math.Clamp((time - look.Start) * Math.Max(look.Speed, 0.1f) * 1.5f, 0, 1);
        eased = eased * eased * (3 - 2 * eased);
        var before = now > 0 ? Toward(track.LookAts[now - 1]) : ahead;
        // the eyes alone, or the head (and body) first and then the eyes
        if (look.Level != "LL_Eyes") TurnHead(rig, model, look.Level, ahead, Vector3.Lerp(before, Toward(look), eased));
        Vector3? Point((float Start, string? Target, Vector3? Point, string Level, float Speed, bool Instant, bool Enabled) l) =>
            !l.Enabled ? null : l.Point ?? (l.Target is { } t && headOf(t) is { } h ? h + EyesAboveHead : null);
        TurnEyes(actor, model, now > 0 ? Point(track.LookAts[now - 1]) : null, Point(look), eased);
    }

    // where the eyes are above the head bone (at the top of the neck), near enough to look into
    private static readonly Vector3 EyesAboveHead = new(0, 0, 0.08f);

    /// <summary>Turns the head (and for a body look-at the chest) from where it faces toward where it's to look, shared out down the spine.</summary>
    private static void TurnHead(Skeleton rig, Matrix4x4[] model, string level, Vector3 ahead, Vector3 want)
    {
        if (want.LengthSquared() < 1e-6f) return;
        want = Vector3.Normalize(want);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(ahead, want), -1, 1));
        var axis = Vector3.Cross(ahead, want);
        if (angle < 1e-3f || axis.LengthSquared() < 1e-8f) return;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Math.Min(angle, 80 * MathF.PI / 180));
        (string Bone, float Share)[] chain = level == "LL_Head"
            ? [("neck", 0.4f), ("head", 0.6f)]
            : [("torso2", 0.15f), ("torso3", 0.2f), ("neck", 0.25f), ("head", 0.4f)];
        foreach (var (name, share) in chain)
        {
            int bone = Bone(rig, name);
            if (bone < 0) continue;
            var pivot = model[bone].Translation;
            var turning = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(Quaternion.Identity, turn, share)) * Matrix4x4.CreateTranslation(pivot);
            // the bone and everything it carries; a rig lists a bone after the one it hangs from
            var carried = new bool[rig.Count];
            for (int b = bone; b < rig.Count; b++)
            {
                carried[b] = b == bone || (rig.Parents[b] >= 0 && carried[rig.Parents[b]]);
                if (carried[b]) model[b] *= turning;
            }
        }
    }

    /// <summary>
    /// Turns each eye from where the face has it toward a point (from the one looked at before, as far as
    /// <paramref name="eased"/>), no further than eyes turn. None to look at leaves the eyes as the face has them.
    /// </summary>
    private static void TurnEyes(StageActor actor, Matrix4x4[] model, Vector3? before, Vector3? now, float eased)
    {
        foreach (var name in new[] { "left_eye", "right_eye" })
        {
            int eye = Bone(actor.Rig, name);
            if (eye < 0 || !Matrix4x4.Invert(actor.Rest[eye], out var back)) continue;
            var pivot = model[eye].Translation;
            // where the eye looks now: where it looks in the rig's pose (as the actor faces, along Y), as the face turns it
            var ahead = Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitY, back), model[eye]);
            if (ahead.LengthSquared() < 1e-8f) continue;
            ahead = Vector3.Normalize(ahead);
            Vector3 Toward(Vector3? at) => at is { } p && Vector3.DistanceSquared(p, pivot) > 1e-4f ? Vector3.Normalize(p - pivot) : ahead;
            var want = Vector3.Lerp(Toward(before), Toward(now), eased);
            if (want.LengthSquared() < 1e-6f) continue;
            want = Vector3.Normalize(want);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(ahead, want), -1, 1));
            var axis = Vector3.Cross(ahead, want);
            if (angle < 1e-3f || axis.LengthSquared() < 1e-8f) continue;
            var turn = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), Math.Min(angle, 30 * MathF.PI / 180));
            model[eye] = model[eye] * Matrix4x4.CreateTranslation(-pivot) * turn * Matrix4x4.CreateTranslation(pivot);
        }
    }

    /// <summary>A bone of a rig by name, or -1.</summary>
    public static int Bone(Skeleton rig, string name) => Array.FindIndex(rig.Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The time in an animation an event plays at: its clip (from its front to its end) stretched over the event's length.</summary>
    private static float Local(AnimationClip clip, float time, float start, float duration, float clipFront, float clipEnd)
    {
        float end = clipEnd > clipFront ? clipEnd : clip.Duration;
        float t = duration > 0 ? (time - start) / duration : 0;
        return Math.Clamp(clipFront + Math.Clamp(t, 0, 1) * (end - clipFront), 0, clip.Duration);
    }

    /// <summary>Where every vertex of an actor's meshes is with its bones where <paramref name="model"/> has them.</summary>
    public static Func<int, MeshChunk, Vector3[]?> Pose(StageActor actor, Matrix4x4[] model)
    {
        var bones = actor.Parts.Select((p, i) => Skinning.BoneMatrices(p.Mesh, actor.Maps[i], actor.Rest, model)).ToList();
        var carried = Matrix4x4.Invert(actor.Rest[0], out var back) ? back * model[0] : Matrix4x4.Identity;
        return (part, chunk) =>
        {
            if (part >= bones.Count) return null;
            var (hung, offset) = actor.AttachedTo[part];
            if (hung < 0 && Skinning.Pose(chunk, bones[part]) is { } skinned) return skinned;
            var placed = actor.Parts[part].Place * (hung >= 0 ? offset * model[hung] : carried);
            var positions = new Vector3[chunk.Positions.Length];
            for (int v = 0; v < positions.Length; v++) positions[v] = Vector3.Transform(chunk.Positions[v], placed);
            return positions;
        };
    }

    /// <summary>The lights a scene defines, by id, and whether each is a spot (else it shines all round, as a point light).</summary>
    public static Dictionary<string, bool> LightKinds(Cr2wFile file) =>
        file.Exports.Where(x => x.ClassName == "CStorySceneLight").Select(x => file.Object(x.Index))
            .Select(o => (Id: Named(o["id"]), Spot: Named(o["type"]) == "LT_SpotLight")).Where(l => l.Id is not null)
            .GroupBy(l => l.Id!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Spot, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scene's lights that are on in a section at a time: each as its last light event by then sets it (one turned
    /// off, or set nowhere yet, isn't). A light's colour is its own, or for one that takes the environment's, a warm white.
    /// </summary>
    public static List<StageLight> LightsAt(SceneSectionTimeline timeline, IReadOnlyDictionary<string, bool> kinds, float time)
    {
        var last = new Dictionary<string, SceneTimelineEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in timeline.Events.Where(e => e.ClassName == "CStorySceneEventLightProperties" && e.Source["isMuted"] is not true && e.Start <= time + 1e-4f).OrderBy(e => e.Start))
            if (Named(e.Source["lightId"]) is { } id) last[id] = e;
        var lights = new List<StageLight>();
        foreach (var (id, e) in last)
        {
            var o = e.Source;
            if (o["enabled"] is false) continue;
            var (position, rotation) = SceneStaging.Transform(o["placement"]);
            float pitch = rotation.Y * MathF.PI / 180, yaw = rotation.Z * MathF.PI / 180;
            var direction = new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
            var color = System.Windows.Media.Color.FromRgb(255, 236, 210);
            if (Named(o["lightColorSource"]) is null or "ELCS_CustomLightColor" && o["color"] is RedObject c)
                color = System.Windows.Media.Color.FromRgb(Convert.ToByte(c["Red"] ?? 0), Convert.ToByte(c["Green"] ?? 0), Convert.ToByte(c["Blue"] ?? 0));
            var spot = o["spotLightProperties"] as RedObject;
            lights.Add(new StageLight(id, kinds.GetValueOrDefault(id), position, direction, color,
                CurveValue(o["brightness"], 10), CurveValue(o["radius"], 5), CurveValue(o["attenuation"], 0.5f),
                spot?["innerAngle"] is { } inner ? Convert.ToSingle(inner) : 30, spot?["outerAngle"] is { } outer ? Convert.ToSingle(outer) : 45));
        }
        return lights;
    }

    /// <summary>A light's setting kept as a curve (SSimpleCurve): its first key's value, or the default when it has none.</summary>
    private static float CurveValue(object? curve, float otherwise)
    {
        if (curve is not RedObject c || c.Array("dataCurveValues").OfType<RedObject>().FirstOrDefault() is not { } key) return otherwise;
        // the key's value: named "value" in the source files, cut short in some cooked ones
        return key.Properties.FirstOrDefault(p => p.Name.EndsWith("lue", StringComparison.Ordinal)) is { Value: { } v } ? Convert.ToSingle(v) : otherwise;
    }

    /// <summary>
    /// Reads the camera animations a scene's sections play on top of their cameras (<c>medium_shake</c>, the hand-held
    /// feel most dialogues have, <c>drunk_shake</c>, a hit's <c>camera_shake_hit_lvl1_1</c>), by name, from the game's
    /// camera animation sets (<c>animations\camera\*.w2anims</c>, the expansions' too).
    /// </summary>
    public static Dictionary<string, AnimationClip> LoadCameraAnimations(IEnumerable<SceneSectionTimeline> timelines, ResourceLocator locator, CancellationToken token)
    {
        var names = timelines.SelectMany(t => t.Events).Where(e => e.ClassName == "CStorySceneEventCameraAnim")
            .Select(e => Named(e.Source["animationName"])).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        var sets = locator.AllPaths(".w2anims").Where(p => p.Contains(@"animations\camera\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)).ToList();
        return Clips(sets, names, null, locator, [], token);
    }

    /// <summary>
    /// A camera with the section's camera animations at a time played on top: each moves and turns the camera a little
    /// in its own frame (the animation's camera bone, the last; X right, Y ahead, Z up), as far as its weight and its
    /// blend in and out say. An idle one loops from its start to the section's end; the rest play their clip over their length.
    /// </summary>
    public static StageCamera Shaken(StageCamera camera, SceneSectionTimeline timeline, IReadOnlyDictionary<string, AnimationClip> animations, float time)
    {
        if (animations.Count == 0) return camera;
        var right = Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up));
        var up = Vector3.Normalize(Vector3.Cross(right, camera.Forward));
        var frame = new Matrix4x4(right.X, right.Y, right.Z, 0, camera.Forward.X, camera.Forward.Y, camera.Forward.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        var (offset, turned) = (Vector3.Zero, Quaternion.Identity);
        foreach (var e in timeline.Events.Where(e => e.ClassName == "CStorySceneEventCameraAnim" && e.Source["isMuted"] is not true && e.Start <= time))
        {
            if (Named(e.Source["animationName"]) is not { } name || !animations.TryGetValue(name, out var clip) || clip.BoneCount == 0 || clip.Duration <= 0) continue;
            var o = e.Source;
            bool idle = o["isIdle"] is true;
            float length = idle || e.Duration <= 0 ? timeline.Duration - e.Start : e.Duration, into = time - e.Start;
            if (into > length) continue;
            float blendIn = o.Float("blendIn"), blendOut = o.Float("blendOut");
            float weight = (o["weight"] is null ? 1 : o.Float("weight"))
                * (blendIn > 0 ? Math.Clamp(into / blendIn, 0, 1) : 1) * (blendOut > 0 && !idle ? Math.Clamp((length - into) / blendOut, 0, 1) : 1);
            if (weight <= 0) continue;
            float front = o.Float("clipFront");
            float at = idle ? (front + into * Math.Max(o["stretch"] is null ? 1 : o.Float("stretch"), 0.01f)) % clip.Duration
                : Local(clip, time, e.Start, e.Duration, front, o.Float("clipEnd"));
            var pose = new BoneTransform[clip.BoneCount];
            Array.Fill(pose, BoneTransform.Identity);
            clip.Sample(at, pose);
            var shake = pose[^1];
            offset += shake.Position * weight;
            turned = Quaternion.Concatenate(turned, Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(shake.Rotation), weight));
        }
        if (offset == Vector3.Zero && turned == Quaternion.Identity) return camera;
        return camera with
        {
            Position = camera.Position + Vector3.Transform(offset, frame),
            Forward = Vector3.Normalize(Vector3.Transform(Vector3.Transform(Vector3.UnitY, turned), frame)),
            Up = Vector3.Normalize(Vector3.Transform(Vector3.Transform(Vector3.UnitZ, turned), frame)),
        };
    }

    /// <summary>
    /// The camera a section is seen through at a time: its last camera cut by then, to one of the scene's cameras by
    /// name or to a camera of its own. Null before its first.
    /// </summary>
    public static StageCamera? CameraAt(SceneSectionTimeline timeline, List<SceneCameraDefinition> cameras, float time, Cr2wFile? file = null)
    {
        // between two keys of a camera blend, along its curves from one to the other
        foreach (var blend in timeline.Events.Where(e => e.ClassName == "CStorySceneEventCameraInterpolation" && e.Source["isMuted"] is not true))
        {
            var order = blend.Source.Array("keyGuids").OfType<Guid>().ToList();
            var curves = blend.Source.Array("keys").OfType<RedObject>().ToList();
            var keys = order.Select((id, i) => (Event: timeline.Events.FirstOrDefault(e => e.Source["GUID"] is Guid g && g == id),
                    Handles: file is not null && i < curves.Count && curves[i]["bezierHandles"] is byte[] raw ? Handles(raw, file.Names) : []))
                .Where(k => k.Event is not null).OrderBy(k => k.Event!.Start).ToList();
            for (int k = 0; k + 1 < keys.Count; k++)
            {
                float t0 = keys[k].Event!.Start, t1 = keys[k + 1].Event!.Start;
                if (time < t0 || time >= t1 || t1 - t0 < 1e-3f) continue;
                if (Raw(keys[k].Event!, cameras) is not { } a || Raw(keys[k + 1].Event!, cameras) is not { } b) break;
                var (leaving, arriving) = (keys[k].Handles, keys[k + 1].Handles);
                float length = t1 - t0;
                (Vector2 In, Vector2 Out) At((Vector2 In, Vector2 Out)[] handles, int channel) =>
                    channel < handles.Length ? handles[channel] : (new Vector2(length / 3, 0), new Vector2(length / 3, 0));
                // how far along the curve the time is: its handles' lengths shape the timing (a third of the way each, by default, is even)
                float s = Along(time, t0, t0 + Math.Clamp(At(leaving, 0).Out.X, 0, length), t1 - Math.Clamp(At(arriving, 0).In.X, 0, length), t1);
                float Value(int channel, float from, float to) => Bezier(s, from, from + At(leaving, channel).Out.Y, to - At(arriving, channel).In.Y, to);
                // the angles go the shortest way round (across 174 blends that's what keeps the actors in the picture)
                float pitch = a.Pitch + MathF.IEEERemainder(b.Pitch - a.Pitch, 360), yaw = a.Yaw + MathF.IEEERemainder(b.Yaw - a.Yaw, 360);
                var position = new Vector3(Value(0, a.Position.X, b.Position.X), Value(1, a.Position.Y, b.Position.Y), Value(2, a.Position.Z, b.Position.Z));
                return Camera($"{a.Name} to {b.Name}", position, Value(3, a.Pitch, pitch), Value(5, a.Yaw, yaw), Value(6, a.FieldOfView, b.FieldOfView));
            }
        }
        foreach (var e in timeline.Events.Where(e => e.Start <= time + 1e-3f && e.Source["isMuted"] is not true).OrderByDescending(e => e.Start))
            if (CameraOf(e, cameras) is { } camera) return camera;
        return null;
    }

    private static float Bezier(float s, float p0, float p1, float p2, float p3)
    {
        float u = 1 - s;
        return u * u * u * p0 + 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s * p3;
    }

    /// <summary>Where along a curve whose time runs through these four points a time falls, from 0 to 1.</summary>
    private static float Along(float time, float x0, float x1, float x2, float x3)
    {
        float low = 0, high = 1;
        for (int i = 0; i < 24; i++)
        {
            float mid = (low + high) / 2;
            if (Bezier(mid, x0, x1, x2, x3) < time) low = mid;
            else high = mid;
        }
        return (low + high) / 2;
    }

    /// <summary>
    /// A camera blend key's curve handles, one for each of the camera's channels (its position X, Y and Z, pitch, roll,
    /// yaw, field of view, then its depth of field): each handle's tangent coming in and going out, how long (in
    /// seconds) and how far. Saved as a fixed array of Bezier2dHandle structs, each property by property. Empty when
    /// they can't be read.
    /// </summary>
    private static (Vector2 In, Vector2 Out)[] Handles(byte[] raw, IReadOnlyList<string> names)
    {
        try
        {
            int at = 0, count = BitConverter.ToInt32(raw, at);
            at += 4;
            if (count is < 0 or > 64) return [];
            var handles = new (Vector2, Vector2)[count];
            for (int h = 0; h < count; h++)
            {
                if (raw[at] == 0) at++; // a struct starts with a zero byte
                Vector2 incoming = default, outgoing = default;
                while (true)
                {
                    int name = BitConverter.ToUInt16(raw, at);
                    at += 2;
                    if (name == 0) break;
                    int size = BitConverter.ToInt32(raw, at + 2);
                    int inner = at + 6;
                    if (raw[inner] == 0) inner++;
                    float x = 0, y = 0;
                    while (true)
                    {
                        int field = BitConverter.ToUInt16(raw, inner);
                        inner += 2;
                        if (field == 0) break;
                        int fieldSize = BitConverter.ToInt32(raw, inner + 2);
                        float value = BitConverter.ToSingle(raw, inner + 6);
                        if (names[field] == "X") x = value;
                        else if (names[field] == "Y") y = value;
                        inner += 2 + fieldSize;
                    }
                    if (names[name] == "incomingTangent") incoming = new Vector2(x, y);
                    else if (names[name] == "outgoingTangent") outgoing = new Vector2(x, y);
                    at += 2 + size;
                }
                handles[h] = (incoming, outgoing);
            }
            return handles;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>A camera event's camera as its numbers: where it stands, its pitch and yaw in degrees, and its lens.</summary>
    private static (string Name, Vector3 Position, float Pitch, float Yaw, float FieldOfView)? Raw(SceneTimelineEvent e, List<SceneCameraDefinition> cameras)
    {
        var o = e.Source;
        if (e.ClassName == "CStorySceneEventCustomCameraInstance" && Named(o["customCameraName"]) is { } name
            && cameras.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } defined)
            return (name, defined.Position, defined.Rotation.Y, defined.Rotation.Z, defined.FieldOfView);
        if (e.ClassName == "CStorySceneEventCustomCamera" && o["cameraTranslation"] is RedObject at && o["cameraRotation"] is RedObject turn)
            return (e.Name, new Vector3(at.Float("X"), at.Float("Y"), at.Float("Z")), turn.Float("Pitch"), turn.Float("Yaw"), o.Float("cameraFov", 35));
        return null;
    }

    /// <summary>The camera a camera event cuts to, or null for another kind of event.</summary>
    private static StageCamera? CameraOf(SceneTimelineEvent e, List<SceneCameraDefinition> cameras) =>
        Raw(e, cameras) is { } c ? Camera(c.Name, c.Position, c.Pitch, c.Yaw, c.FieldOfView) : null;

    /// <summary>
    /// How far the picture is faded at a time, and to what colour: a fade out goes to its colour over its length and
    /// stays there, a fade in comes back from it. Null when it isn't faded.
    /// </summary>
    public static (System.Windows.Media.Color Color, double Amount)? FadeAt(SceneSectionTimeline timeline, float time)
    {
        var fade = timeline.Events.LastOrDefault(e => e.ClassName == "CStorySceneEventFade" && e.Start <= time + 1e-4f && e.Source["isMuted"] is not true);
        if (fade is null) return null;
        float t = fade.Duration > 0 ? Math.Clamp((time - fade.Start) / fade.Duration, 0, 1) : 1;
        // a fade is a fade in unless it says otherwise: cooking leaves out the default, so only a fade out keeps in=false
        double amount = fade.Source["in"] is not false ? 1 - t : t;
        var c = fade.Source["color"] as RedObject;
        byte Part(string name) => c?[name] is { } v ? Convert.ToByte(v) : (byte)0;
        return amount <= 0.001 ? null : (System.Windows.Media.Color.FromRgb(Part("Red"), Part("Green"), Part("Blue")), amount);
    }

    /// <summary>A camera from where it stands and its angles in degrees: yaw turns it round the upright from looking along Y, pitch tilts it up.</summary>
    private static StageCamera Camera(string name, Vector3 position, float pitch, float yaw, float fov)
    {
        float p = pitch * MathF.PI / 180, y = yaw * MathF.PI / 180;
        var forward = new Vector3(-MathF.Sin(y) * MathF.Cos(p), MathF.Cos(y) * MathF.Cos(p), MathF.Sin(p));
        return new StageCamera(name, position, Vector3.Normalize(forward), Vector3.UnitZ, fov > 0 ? fov : 35);
    }
}
