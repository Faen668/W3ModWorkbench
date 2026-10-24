using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Effects;

/// <summary>What a part of an effect does: the ones the 3D view plays, and the rest it lists.</summary>
public enum EffectPartKind
{
    Particles,
    Sound,
    Light,
    MaterialOverride,
    Flare,
    Parameter,
    Screen,
    Other,
}

/// <summary>
/// Where a part of an effect goes on the entity: on a component (CFXSpawnerComponent, maybe at a slot of it), at slots
/// or bones by name (CFXSimpleSpawner), or on the water's surface (CFXWaterSpawner); moved and turned from there.
/// </summary>
public sealed record EffectSpawner(string ClassName, string? Component, string? ParentSlot, IReadOnlyList<string> Slots,
    IReadOnlyList<string> Bones, Vector3 Position, Vector3 Rotation, bool Attach);

/// <summary>A part of an effect: what it is, when it starts and how long it lasts (seconds), and what it needs.</summary>
public sealed record EffectPart(EffectPartKind Kind, string ClassName, float Start, float Duration, RedObject Object)
{
    public string? ParticleSystem { get; init; }
    public EffectSpawner? Spawner { get; init; }
    public string? SoundEvent { get; init; }
    public string? Bone { get; init; }

    /// <summary>The component its track group is for, which it goes on when its spawner doesn't say.</summary>
    public string? GroupComponent { get; init; }

    /// <summary>For a light: its colour (0 to 1), radius in metres and brightness.</summary>
    public Vector3 LightColor { get; init; }
    public float LightRadius { get; init; }
    public float LightBrightness { get; init; }

    public float End => Start + Duration;
}

/// <summary>
/// An effect an entity can play (a CFXDefinition): its name, length and looping, and its parts. The game's entities keep
/// theirs cooked, each a CR2W file of its own in the template's cookedEffects; REDkit's source entities keep them as
/// objects of the template's file. An animation, an event or a script plays one by its name.
/// </summary>
public sealed record EntityEffect(string Name, float Length, bool Looped, float LoopStart, float LoopEnd, IReadOnlyList<EffectPart> Parts)
{
    /// <summary>The effect as the file keeps it, for its properties.</summary>
    public required RedObject Definition { get; init; }
    public required Cr2wFile File { get; init; }

    /// <summary>Whether it was read from REDkit's source copy of the entity rather than the game's cooked one.</summary>
    public bool FromRedkit { get; init; }

    /// <summary>Its parts with a particle system, which the 3D view plays.</summary>
    public IEnumerable<EffectPart> ParticleParts => Parts.Where(p => p.Kind == EffectPartKind.Particles && p.ParticleSystem is not null);
}

public static class EntityEffects
{
    /// <summary>
    /// The effects of an entity template: its own source effects when it has them (REDkit's copies do), else its cooked
    /// ones. An effect that can't be read is passed over.
    /// </summary>
    public static List<EntityEffect> Read(Cr2wFile file, RedObject template, bool fromRedkit = false)
    {
        var result = new List<EntityEffect>();
        foreach (var definition in template.Array("effects").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
            if (TryRead(file, definition, null, fromRedkit) is { } effect) result.Add(effect);
        if (result.Count > 0) return result;
        foreach (var cooked in template.Array("cookedEffects").OfType<RedObject>())
        {
            // the buffer is the effect's own CR2W file, after its length
            byte[] buffer = cooked.Bytes("buffer");
            int at = buffer.AsSpan().IndexOf("CR2W"u8);
            if (at < 0) continue;
            try
            {
                var inner = Cr2wFile.Read(buffer[at..]);
                if (inner.First("CFXDefinition") is { } definition && TryRead(inner, definition, cooked["name"] as string, fromRedkit) is { } effect)
                    result.Add(effect);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }
        return result;
    }

    private static EntityEffect? TryRead(Cr2wFile file, RedObject definition, string? name, bool fromRedkit)
    {
        var parts = new List<EffectPart>();
        foreach (var group in definition.Array("trackGroups").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
        {
            // a group switched off in the editor doesn't play
            if (group["isEnabled"] is false) continue;
            string? component = group["componentName"] as string is { Length: > 0 } c && c != "None" ? c : null;
            foreach (var track in group.Array("tracks").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                foreach (var item in track.Array("trackItems").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                    parts.Add(Part(file, item) with { GroupComponent = component });
        }
        name ??= definition["name"] as string;
        if (string.IsNullOrEmpty(name)) return null;
        return new EntityEffect(name, definition.Float("length"), definition["isLooped"] is true, definition.Float("loopStart"), definition.Float("loopEnd"),
            [.. parts.OrderBy(p => p.Start)]) { Definition = definition, File = file, FromRedkit = fromRedkit };
    }

    private static EffectPart Part(Cr2wFile file, RedObject item)
    {
        var kind = item.ClassName switch // loc-ignore-start
        {
            "CFXTrackItemParticles" => EffectPartKind.Particles,
            "CFXTrackItemSoundEvent" => EffectPartKind.Sound,
            "CFXTrackItemDynamicLight" => EffectPartKind.Light,
            "CFXTrackItemMaterialOverride" => EffectPartKind.MaterialOverride,
            "CFXTrackItemFlare" => EffectPartKind.Flare,
            "CFXTrackItemParameterFloat" or "CFXTrackItemParameterColor" or "CFXTrackItemSetDissolve" => EffectPartKind.Parameter,
            "CFXTrackItemRadialBlur" or "CFXTrackItemBrightnessTint" or "CFXTrackItemFullscreenBlur" or "CFXTrackItemDisableHDRAdaptation"
                or "CFXTrackItemEnvironmentModifier" => EffectPartKind.Screen,
            _ => EffectPartKind.Other,
        }; // loc-ignore-end
        var color = item.Struct("color");
        return new EffectPart(kind, item.ClassName, item.Float("timeBegin"), item.Float("timeDuration"), item)
        {
            ParticleSystem = item["particleSystem"] is RedHandle system ? file.Import(system)?.Path : null,
            Spawner = item["spawner"] is RedHandle spawner && file.Object(spawner) is { } s ? Spawner(s) : null,
            SoundEvent = item["soundEventName"] as string is { Length: > 0 } sound ? sound : null,
            Bone = item["boneName"] as string is { Length: > 0 } bone && bone != "None" ? bone : null,
            LightColor = color is null ? Vector3.One : new Vector3(color.Integer("Red") / 255f, color.Integer("Green") / 255f, color.Integer("Blue") / 255f),
            LightRadius = item.Float("radius"),
            LightBrightness = item.Float("brightness", 1),
        };
    }

    private static EffectSpawner Spawner(RedObject s)
    {
        static string? Name(object? value) => value as string is { Length: > 0 } text && text != "None" ? text : null;
        var position = s.Struct("relativePosition") ?? s.Struct("relativePos");
        var rotation = s.Struct("relativeRotation") ?? s.Struct("relativeRot");
        return new EffectSpawner(s.ClassName, Name(s["componentName"]), Name(s["parentSlotName"]),
            [.. s.Array("slotNames").Select(Name).OfType<string>()], [.. s.Array("boneNames").Select(Name).OfType<string>()],
            position is null ? Vector3.Zero : new Vector3(position.Float("X"), position.Float("Y"), position.Float("Z")),
            rotation is null ? Vector3.Zero : new Vector3(rotation.Float("Pitch"), rotation.Float("Yaw"), rotation.Float("Roll")),
            s["attach"] is not false);
    }
}
