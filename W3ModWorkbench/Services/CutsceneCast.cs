using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// One of a cutscene's actors put together to be drawn: the meshes of its entity in the appearance the cutscene gives it,
/// skinned to the rig its body animation moves, so it plays where the cutscene puts it.
/// </summary>
public sealed class CastMember
{
    public required PlayedActor Played { get; init; }
    public required EntityFile Entity { get; init; }
    public required string? Appearance { get; init; }

    /// <summary>The meshes, each with where its component puts it in the entity, and the component that draws it.</summary>
    public required List<(MeshFile Mesh, Matrix4x4 Place)> Parts { get; init; }
    public required List<EntityComponent> DrawnBy { get; init; }

    /// <summary>For each mesh, the entity and appearance whose colour entries tint it: the actor's own, or a stand-in piece's.</summary>
    public required List<(EntityFile Entity, string? Appearance)> ColoredBy { get; init; }

    /// <summary>The rig in its own pose, and each mesh's bones found in it.</summary>
    public required Matrix4x4[] Rest { get; init; }
    public required List<(int Bone, bool ByName)[]> Maps { get; init; }

    /// <summary>The bone a mesh that isn't skinned (a prop's) moves with.</summary>
    public required int Carrier { get; init; }

    /// <summary>
    /// For each mesh, the body's bone it hangs from and where on it (a sword on its slot on the back), or bone -1 for one
    /// that's skinned or carried.
    /// </summary>
    public required List<(int Bone, Matrix4x4 Offset)> AttachedTo { get; init; }

    public string Name => Played.Name;
}

/// <summary>
/// A cutscene's actors put together from their entities, to draw them as they are in the game and play their body
/// animations on them. Their faces aren't played: a head moves with the body.
/// </summary>
public static class CutsceneCast
{
    /// <summary>Whether an entity template is the player's, which is put together from what's equipped.</summary>
    public static bool IsPlayer(string template) =>
        template.EndsWith(@"\player.w2ent", StringComparison.OrdinalIgnoreCase) || template.EndsWith(@"\geralt_player.w2ent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads each played actor's entity and meshes. An actor without a rig, an entity or meshes is left out, with why in
    /// <paramref name="notes"/>. Reads from disk, so it belongs off the UI thread.
    /// </summary>
    public static List<CastMember> Load(CutsceneFile cutscene, CutscenePlayback playback, ResourceLocator locator, List<string> notes, CancellationToken token)
    {
        var cast = new List<CastMember>();
        foreach (var played in playback.Actors)
        {
            token.ThrowIfCancellationRequested();
            var actor = cutscene.Actors.FirstOrDefault(a => a.Name == played.Name);
            if (played.Rig is not { } rig)
            {
                notes.Add($"{played.Name} isn't drawn: {played.RigNote}");
                continue;
            }
            if (actor?.Template is not { } template || locator.ReadFile(template) is not { } data)
            {
                notes.Add($"{played.Name} isn't drawn: its entity template wasn't found.");
                continue;
            }
            EntityFile entity;
            try
            {
                entity = EntityFile.Read(data);
            }
            catch (InvalidDataException ex)
            {
                notes.Add($"{played.Name} isn't drawn: its entity could not be read ({ex.Message}).");
                continue;
            }
            // the appearance the cutscene names, else the entity's default one
            var appearance = actor.Appearance is { } wanted
                ? entity.Appearances.FirstOrDefault(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? entity.DefaultAppearance
                : entity.DefaultAppearance;
            var look = entity.Look(appearance, locator.ReadFile);
            var (parts, drawnBy, _, missing) = EntityWindow.ReadMeshes(look, locator, token);
            var coloredBy = parts.Select(_ => (entity, appearance?.Name)).ToList();
            var attachedTo = parts.Select(_ => (-1, Matrix4x4.Identity)).ToList();
            if (IsPlayer(template))
            {
                // the game dresses the player in what's equipped; drawn here in what his default inventory gives him
                int before = parts.Count;
                foreach (var piece in PlayerGear.Default(locator))
                {
                    try
                    {
                        if (locator.ReadFile(piece.Template) is not { } itemData) continue;
                        var itemEntity = EntityFile.Read(itemData);
                        // a dyeable piece has an appearance for each dye: undyed, as it starts, is dye_default
                        var itemAppearance = itemEntity.DefaultAppearance;
                        var itemLook = itemEntity.Look(itemAppearance, locator.ReadFile);
                        var (itemParts, itemDrawnBy, _, _) = EntityWindow.ReadMeshes(itemLook, locator, token);
                        var hung = piece.Slot is { } slot ? SlotPlace(entity, look, slot, rig, locator) : (-1, Matrix4x4.Identity);
                        parts.AddRange(itemParts);
                        drawnBy.AddRange(itemDrawnBy);
                        coloredBy.AddRange(itemParts.Select(_ => (itemEntity, itemAppearance?.Name)));
                        attachedTo.AddRange(itemParts.Select(_ => hung));
                    }
                    catch (InvalidDataException)
                    {
                        // a piece that can't be read is left off
                    }
                }
                if (parts.Count > before) notes.Add($"{played.Name} is the player, whom the game dresses in what's equipped: shown here with Geralt's default gear (his starting armour, swords and hairstyle).");
            }
            if (parts.Count == 0)
            {
                notes.Add($"{played.Name} isn't drawn: " + (missing > 0 ? "its meshes could not be read." : "its entity has no meshes."));
                continue;
            }
            if (missing > 0) notes.Add($"{played.Name}: {Format.Count(missing, "mesh", "meshes")} not found or not read.");
            var rest = new Matrix4x4[rig.Count];
            rig.ToModel(rig.Pose, rest);
            cast.Add(new CastMember
            {
                Played = played,
                Entity = entity,
                Appearance = appearance?.Name,
                Parts = parts,
                DrawnBy = drawnBy,
                ColoredBy = coloredBy,
                Rest = rest,
                Maps = parts.Select(p => Skinning.MapBones(p.Mesh, rig, rest)).ToList(),
                Carrier = Carrier(played, rig),
                AttachedTo = attachedTo,
            });
        }
        return cast;
    }

    /// <summary>
    /// Where a slot is on the body: the body's bone it moves with and where it is on that bone. A slot on the body's own
    /// rig is at its bone; one on a component of its own (the player's swords hang on scabbards_skeleton, a small rig
    /// fastened to the body's torso3) is where its bone stands in that rig, carried by the bone the component hangs on.
    /// Bone -1 when it can't be found.
    /// </summary>
    private static (int Bone, Matrix4x4 Offset) SlotPlace(EntityFile entity, EntityLook look, string slot, Skeleton rig, ResourceLocator locator)
    {
        var found = entity.Template.Array("slots").OfType<RedObject>().FirstOrDefault(s => string.Equals(s["name"] as string, slot, StringComparison.OrdinalIgnoreCase));
        string bone = found?["boneName"] as string ?? (slot.EndsWith("_slot", StringComparison.OrdinalIgnoreCase) ? slot[..^5] : slot);
        int own = Array.FindIndex(rig.Names, n => n.Equals(bone, StringComparison.OrdinalIgnoreCase));
        if (own >= 0) return (own, Matrix4x4.Identity);
        // on a component of its own: its rig, and what it hangs on
        if (found?["componentName"] is not string name || look.Components.FirstOrDefault(c => c.Name == name) is not { } component) return (-1, Matrix4x4.Identity);
        var link = look.Links.FirstOrDefault(l => l.Child == component && l.Slot is not null);
        int carrier = link?.Slot is { } on ? Array.FindIndex(rig.Names, n => n.Equals(on, StringComparison.OrdinalIgnoreCase)) : -1;
        string? rigPath = component.Resources.FirstOrDefault(r => r.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase));
        if (carrier < 0 || rigPath is null) return (-1, Matrix4x4.Identity);
        try
        {
            if (locator.ReadFile(rigPath) is not { } data) return (-1, Matrix4x4.Identity);
            var small = Skeleton.Read(data);
            int at = Array.FindIndex(small.Names, n => n.Equals(bone, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return (-1, Matrix4x4.Identity);
            var rest = new Matrix4x4[small.Count];
            small.ToModel(small.Pose, rest);
            return (carrier, rest[at] * component.Local);
        }
        catch (InvalidDataException)
        {
            return (-1, Matrix4x4.Identity);
        }
    }

    /// <summary>
    /// The bone a prop's meshes move with: of the rig's first few bones, the one the animation carries furthest, as a
    /// prop's rig is a root and a bone or two that the animation moves about.
    /// </summary>
    private static int Carrier(PlayedActor played, Skeleton rig)
    {
        int best = 0;
        float furthest = -1;
        var pose = new BoneTransform[rig.Count];
        var model = new Matrix4x4[rig.Count];
        var first = new Vector3[Math.Min(4, rig.Count)];
        var travel = new float[first.Length];
        int steps = 12;
        for (int s = 0; s <= steps; s++)
        {
            rig.Pose.CopyTo(pose, 0);
            played.Clip.Sample(played.Clip.Duration * s / steps, pose);
            rig.ToModel(pose, model);
            for (int b = 0; b < first.Length; b++)
            {
                if (s == 0) first[b] = model[b].Translation;
                else travel[b] = Math.Max(travel[b], Vector3.Distance(first[b], model[b].Translation));
            }
        }
        for (int b = 0; b < travel.Length; b++)
            if (travel[b] > furthest + 1e-4f) (best, furthest) = (b, travel[b]);
        return best;
    }

    /// <summary>
    /// Where every vertex of every chunk of a cast member's meshes is at a time, in the cutscene's space: skinned chunks
    /// bent by the bones, the rest carried by <see cref="CastMember.Carrier"/> from where their component puts them.
    /// </summary>
    public static Func<int, MeshChunk, Vector3[]?> Pose(CastMember member, float time)
    {
        var rig = member.Played.Rig!;
        var pose = rig.Pose.ToArray();
        member.Played.Clip.Sample(time, pose);
        var model = new Matrix4x4[rig.Count];
        rig.ToModel(pose, model);
        var bones = member.Parts.Select((p, i) => Skinning.BoneMatrices(p.Mesh, member.Maps[i], member.Rest, model)).ToList();
        var carried = Matrix4x4.Invert(member.Rest[member.Carrier], out var back) ? back * model[member.Carrier] : Matrix4x4.Identity;
        return (part, chunk) =>
        {
            if (part >= bones.Count) return null;
            var (hung, offset) = member.AttachedTo[part];
            if (hung < 0 && Skinning.Pose(chunk, bones[part]) is { } skinned) return skinned;
            // a piece on a slot goes where the slot is on its bone; a prop's mesh moves with the bone that carries it
            var placed = member.Parts[part].Place * (hung >= 0 ? offset * model[hung] : carried);
            var positions = new Vector3[chunk.Positions.Length];
            for (int v = 0; v < positions.Length; v++) positions[v] = Vector3.Transform(chunk.Positions[v], placed);
            return positions;
        };
    }
}
