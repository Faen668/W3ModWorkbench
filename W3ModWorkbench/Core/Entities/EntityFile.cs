using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Entities;

/// <summary>
/// One of an entity's components: a mesh, a skeleton, a sound emitter, a trigger… with its place in the editor's graph,
/// where it sits relative to the entity, and its properties. <see cref="File"/> is the CR2W file it's read from: the
/// template's own, its compiled components, or a streamed block of them.
/// </summary>
public sealed class EntityComponent
{
    public required string ClassName { get; init; }
    public required string Name { get; init; }
    public required RedObject Object { get; init; }
    public required Cr2wFile File { get; init; }
    public int GraphX { get; init; }
    public int GraphY { get; init; }

    /// <summary>Its transform relative to what it's attached to.</summary>
    public Matrix4x4 Local { get; init; } = Matrix4x4.Identity;

    /// <summary>Where it is in the entity, after the transforms of the components it hangs from.</summary>
    public Matrix4x4 Placed { get; internal set; } = Matrix4x4.Identity;

    /// <summary>The mesh it draws, for a mesh component.</summary>
    public string? Mesh { get; init; }

    /// <summary>The resources it refers to: a mesh, a rig, a behaviour graph, a particle system.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>Whether it's streamed in when the player comes near, rather than always there.</summary>
    public bool Streamed { get; init; }

    /// <summary>Whether its mesh is skinned to a skeleton, so its transform is the skeleton's.</summary>
    public bool Skinned { get; internal set; }

    /// <summary>
    /// The templates it comes from, outermost first ("npc_base_m.w2ent / man_base.w2ent"), when it isn't the
    /// entity's own: from a template the entity includes, or from one an appearance uses.
    /// </summary>
    public string? Origin { get; internal set; }

    /// <summary>Whether it comes from a template the entity includes.</summary>
    public bool Included { get; internal set; }

    /// <summary>Whether it comes from a template of the appearance shown.</summary>
    public bool FromAppearance { get; internal set; }

    /// <summary>Whether the entity changes some of its properties from what the template it comes from has.</summary>
    public bool Overridden { get; internal set; }

    public override string ToString() => $"{Name} ({ClassName})";
}

/// <summary>A component hung from another: rigidly (a hard attachment, maybe at a bone), or skinned to its skeleton.</summary>
public sealed record EntityLink(EntityComponent Parent, EntityComponent Child, string Kind, string? Slot);

/// <summary>An entity's components and attachments as they are in one of its appearances, or on their own.</summary>
public sealed record EntityLook(List<EntityComponent> Components, List<EntityLink> Links);

/// <summary>A look an entity can have: the templates (head, body, armour…) whose components make it up.</summary>
public sealed record EntityAppearance(string Name, string? VoiceTag, IReadOnlyList<string> Templates);

/// <summary>A named place on the entity to put things, on a component and maybe a bone.</summary>
public sealed record EntitySlot(string Name, string? Component, string? Bone);

/// <summary>
/// An entity template (.w2ent): the object the game spawns, its components and how they hang together, its
/// appearances, the templates it includes, its slots and effects. A cooked template keeps its components compiled
/// into a CR2W file of their own (<c>flatCompiledData</c>), and some of them streamed into further blocks;
/// an uncooked one keeps them in the template's file.
/// </summary>
public sealed class EntityFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Template { get; init; }

    /// <summary>The object the game spawns, with its properties; null when it isn't there.</summary>
    public RedObject? Entity { get; init; }
    public string? EntityClass { get; init; }
    public required List<EntityComponent> Components { get; init; }
    public required List<EntityLink> Links { get; init; }
    public required List<string> Includes { get; init; }
    public required List<EntityAppearance> Appearances { get; init; }
    public required List<EntitySlot> Slots { get; init; }
    public required List<string> Effects { get; init; }

    /// <summary>The components' properties the template changes from what its includes have, by component.</summary>
    public required List<(string Component, string Properties)> Overrides { get; init; }

    /// <summary>The mesh components that are seen: not those only there to cast a character's shadow.</summary>
    public IEnumerable<EntityComponent> Meshes => Components.Where(c => c.Mesh is not null && !ShadowOnly(c));

    private static bool ShadowOnly(EntityComponent c) =>
        (c.Object["drawableFlags"] as RedEnum)?.Values.Contains("DF_IsCharacterShadowFallback") == true
        || c.Mesh!.Contains("shadowmesh", StringComparison.OrdinalIgnoreCase);

    public static bool IsEntity(string name) => name.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase);

    public static EntityFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var template = file.First("CEntityTemplate") ?? throw new InvalidDataException("There's no entity in it: it has no CEntityTemplate.");

        // the components: compiled into a file of their own when cooked, else in the template's file
        var files = new List<(Cr2wFile File, bool Streamed)>();
        Cr2wFile? flat = null;
        if (template.Bytes("flatCompiledData") is { Length: > 0 } compiled)
            try { files.Add((flat = Cr2wFile.Read(compiled), false)); }
            catch (InvalidDataException) { }
        if (flat is null) files.Add((file, false));
        var main = files[0].File;
        var entity = main.Exports.Count > 0 && flat is not null ? main.Object(0)
            : template["entityObject"] is RedHandle h ? file.Object(h) : null;
        // components streamed in near the player are a CR2W file inside the entity, after its length
        if (entity?.Bytes("streamingDataBuffer") is { Length: > 8 } streamed && Cr2wFile.IsCr2w(streamed.AsSpan(4)))
            try { files.Add((Cr2wFile.Read(streamed[4..]), true)); }
            catch (InvalidDataException) { }

        var components = new List<EntityComponent>();
        var byObject = new Dictionary<(Cr2wFile, int), EntityComponent>();
        foreach (var (f, isStreamed) in files)
            foreach (var export in f.Exports)
            {
                var o = f.Object(export.Index);
                if (!IsComponent(o)) continue;
                var resources = o.Properties.Select(p => p.Value).OfType<RedHandle>().Select(f.Import).OfType<Cr2wImport>().Select(i => i.Path)
                    .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var c = new EntityComponent
                {
                    ClassName = export.ClassName,
                    Name = o["name"] as string ?? export.ClassName,
                    Object = o,
                    File = f,
                    GraphX = (int)o.Integer("graphPositionX"),
                    GraphY = (int)o.Integer("graphPositionY"),
                    Local = Transform(o.Bytes("transform")),
                    // a cloth component's shape is its cloth resource, which is drawn as a mesh
                    Mesh = o["mesh"] is RedHandle m ? f.Import(m)?.Path
                        : o.ClassName == "CClothComponent" && o["resource"] is RedHandle r ? f.Import(r)?.Path : null,
                    Resources = resources,
                    Streamed = isStreamed,
                };
                components.Add(c);
                byObject[(f, export.Index)] = c;
            }

        // attachments hang one component from another; the streamed ones are kept by name, each its own little file
        var links = new List<EntityLink>();
        foreach (var (f, _) in files)
            foreach (var export in f.Exports.Where(e => e.ClassName.EndsWith("Attachment", StringComparison.Ordinal)))
            {
                var a = f.Object(export.Index);
                if (a["parent"] is RedHandle { Export: int p } && a["child"] is RedHandle { Export: int c }
                    && byObject.TryGetValue((f, p), out var parent) && byObject.TryGetValue((f, c), out var child))
                    links.Add(new EntityLink(parent, child, Kind(export.ClassName), Name(a["parentSlotName"])));
            }
        foreach (var s in template.Array("streamedAttachments").OfType<RedObject>())
        {
            var parent = components.FirstOrDefault(c => c.Name == s["parentName"] as string);
            var child = components.FirstOrDefault(c => c.Name == s["childName"] as string);
            if (parent is null || child is null) continue;
            string kind = "Hard";
            string? slot = null;
            if (s.Bytes("data") is { Length: > 0 } block && Cr2wFile.IsCr2w(block))
                try
                {
                    var af = Cr2wFile.Read(block);
                    if (af.Exports.FirstOrDefault() is { } e)
                    {
                        kind = Kind(e.ClassName);
                        slot = Name(af.Object(e.Index)["parentSlotName"]);
                    }
                }
                catch (InvalidDataException) { }
            links.Add(new EntityLink(parent, child, kind, slot));
        }
        foreach (var link in links.Where(l => l.Kind == "Skinned")) link.Child.Skinned = true;
        Place(components, links);
        var overridden = template.Array("overrides").OfType<RedObject>().Select(o => o["componentName"] as string).OfType<string>().ToHashSet();
        foreach (var c in components.Where(c => overridden.Contains(c.Name))) c.Overridden = true;

        var appearances = template.Array("appearances").OfType<RedObject>().Select(a => new EntityAppearance(
            a["name"] as string ?? "",
            Name(a["voicetag"]),
            a.Array("includedTemplates").OfType<RedHandle>().Select(file.Import).OfType<Cr2wImport>().Select(i => i.Path).ToList())).ToList();

        return new EntityFile
        {
            File = file,
            Template = template,
            Entity = entity,
            EntityClass = Name(template["entityClass"]) ?? entity?.ClassName,
            Components = components,
            Links = links,
            Includes = template.Array("includes").OfType<RedHandle>().Select(file.Import).OfType<Cr2wImport>().Select(i => i.Path).ToList(),
            Appearances = appearances,
            Slots = template.Array("slots").OfType<RedObject>().Select(s => new EntitySlot(s["name"] as string ?? "", Name(s["componentName"]), Name(s["boneName"]))).ToList(),
            Effects = template.Array("cookedEffects").OfType<RedObject>().Select(e => e["name"] as string).OfType<string>()
                .Concat(template.Array("effects").OfType<RedHandle>().Select(file.Object).OfType<RedObject>().Select(e => e["name"] as string).OfType<string>())
                .Distinct().ToList(),
            Overrides = template.Array("overrides").OfType<RedObject>()
                .Select(o => (o["componentName"] as string ?? "", string.Join(", ", o.Array("overriddenProperties").OfType<string>()))).ToList(),
        };
    }

    /// <summary>
    /// Marks the components that come from the templates the entity includes with where they come from, reading
    /// those templates (and theirs) with <paramref name="read"/>. A cooked entity has its includes' components
    /// compiled into its own, so they're known by their names.
    /// </summary>
    public void FindOrigins(Func<string, byte[]?> read)
    {
        var cache = new Dictionary<string, EntityFile?>(StringComparer.OrdinalIgnoreCase);
        var origins = Origins(this, read, 0, cache);
        foreach (var c in Components)
            if (origins.TryGetValue(c.Name, out var chain))
            {
                c.Origin = chain;
                c.Included = true;
            }
        // what the includes have that isn't compiled in: their colour entries
        void Walk(EntityFile entity, string? from, int depth)
        {
            if (depth > 6) return;
            foreach (string include in entity.Includes)
            {
                if (Load(include, read, cache) is not { } included) continue;
                string chain = from is null ? System.IO.Path.GetFileName(include) : $"{from} / {System.IO.Path.GetFileName(include)}";
                foreach (var entry in included.Template.Array("coloringEntries").OfType<RedObject>()) IncludedColorings.Add((entry, included.File, chain));
                Walk(included, chain, depth + 1);
            }
        }
        Walk(this, null, 0);
    }

    /// <summary>The colour entries of the templates the entity includes, with the template each is in, once <see cref="FindOrigins"/> has looked.</summary>
    public List<(RedObject Entry, Cr2wFile File, string From)> IncludedColorings { get; } = [];

    /// <summary>Each component's name, with the chain of included templates it comes from.</summary>
    private static Dictionary<string, string> Origins(EntityFile entity, Func<string, byte[]?> read, int depth, Dictionary<string, EntityFile?> cache)
    {
        var origins = new Dictionary<string, string>();
        if (depth > 6) return origins;
        foreach (string include in entity.Includes)
        {
            if (Load(include, read, cache) is not { } included) continue;
            var deeper = Origins(included, read, depth + 1, cache);
            string name = System.IO.Path.GetFileName(include);
            foreach (var c in included.Components)
                origins.TryAdd(c.Name, deeper.TryGetValue(c.Name, out var chain) ? $"{name} / {chain}" : name);
        }
        return origins;
    }

    private static EntityFile? Load(string path, Func<string, byte[]?> read, Dictionary<string, EntityFile?> cache)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        EntityFile? file = null;
        try
        {
            if (read(path) is { } data) file = Read(data);
        }
        catch (InvalidDataException) { }
        return cache[path] = file;
    }

    /// <summary>
    /// The entity as it is in an appearance (or on its own, for null): its components, and those of the templates
    /// the appearance uses (and the templates they include), each marked with the template it comes from. A
    /// template's component of the same name as one the entity has is the entity's, so attachments to it join up.
    /// </summary>
    public EntityLook Look(EntityAppearance? appearance, Func<string, byte[]?> read)
    {
        var components = new List<EntityComponent>(Components);
        var links = new List<EntityLink>(Links);
        if (appearance is null) return new EntityLook(components, links);
        var byName = components.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        var cache = new Dictionary<string, EntityFile?>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string? from, int depth)
        {
            if (depth > 3 || !seen.Add(path) || Load(path, read, cache) is not { } part) return;
            string origin = from is null ? System.IO.Path.GetFileName(path) : $"{from} / {System.IO.Path.GetFileName(path)}";
            var mine = new Dictionary<EntityComponent, EntityComponent>();
            foreach (var c in part.Components)
            {
                if (byName.TryGetValue(c.Name, out var same))
                {
                    // two of the appearance's templates can have a component of the same name; the one with a mesh wins
                    if (same.FromAppearance && same.Mesh is null && c.Mesh is not null)
                    {
                        components[components.IndexOf(same)] = c;
                        c.Origin = origin;
                        c.FromAppearance = true;
                        byName[c.Name] = c;
                        for (int i = 0; i < links.Count; i++)
                            if (links[i].Parent == same || links[i].Child == same)
                                links[i] = links[i] with { Parent = links[i].Parent == same ? c : links[i].Parent, Child = links[i].Child == same ? c : links[i].Child };
                        mine[c] = c;
                        continue;
                    }
                    mine[c] = same;
                    continue;
                }
                c.Origin = origin;
                c.FromAppearance = true;
                components.Add(c);
                byName[c.Name] = c;
                mine[c] = c;
            }
            foreach (var link in part.Links)
                if (mine.TryGetValue(link.Parent, out var parent) && mine.TryGetValue(link.Child, out var child) && parent != child)
                    links.Add(link with { Parent = parent, Child = child });
            foreach (string include in part.Includes) Add(include, origin, depth + 1);
        }
        foreach (string template in appearance.Templates) Add(template, null, 0);
        return new EntityLook(components, links);
    }

    /// <summary>
    /// The meshes a look draws, each with where it's placed and the component that draws it: not those only there to
    /// cast a shadow, and a mesh at the same place in two templates once.
    /// </summary>
    public static List<(string Mesh, Matrix4x4 Placed, EntityComponent Component)> MeshesOf(EntityLook look) =>
        look.Components.Where(c => c.Mesh is not null && !ShadowOnly(c))
            .Select(c => (c.Mesh!, c.Skinned ? Matrix4x4.Identity : c.Placed, c))
            .DistinctBy(m => (m.Item1.ToLowerInvariant(), m.Item2)).ToList();

    /// <summary>Whether an object is a component: it has a name and a GUID, as every component does.</summary>
    private static bool IsComponent(RedObject o) =>
        o.ClassName.EndsWith("Component", StringComparison.Ordinal) || o["guid"] is Guid && o["name"] is string && !o.ClassName.EndsWith("Attachment", StringComparison.Ordinal);

    private static string Kind(string attachment) => attachment switch
    {
        "CMeshSkinningAttachment" or "CAnimatedAttachment" => "Skinned",
        "CHardAttachment" => "Hard",
        _ => Cutscenes.CutsceneFile.Humanize(attachment, "C").Replace(" attachment", "", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Each component's place in the entity: its own transform after those of the components it hangs from.</summary>
    private static void Place(List<EntityComponent> components, List<EntityLink> links)
    {
        var parentOf = new Dictionary<EntityComponent, EntityComponent>();
        foreach (var link in links.Where(l => l.Kind == "Hard")) parentOf.TryAdd(link.Child, link.Parent);
        foreach (var c in components)
        {
            var placed = c.Local;
            var at = c;
            for (int depth = 0; depth < 16 && parentOf.TryGetValue(at, out var parent); depth++)
            {
                placed *= parent.Local;
                at = parent;
            }
            c.Placed = placed;
        }
    }

    /// <summary>
    /// An EngineTransform as it's saved: a byte of flags, then the position (1), the rotation as roll, pitch and yaw in
    /// degrees (2) and the scale (4), each three floats, for the flags set.
    /// </summary>
    public static Matrix4x4 Transform(byte[] raw)
    {
        if (raw.Length < 1) return Matrix4x4.Identity;
        int flags = raw[0], at = 1;
        Vector3 Read3(Vector3 otherwise)
        {
            if (at + 12 > raw.Length) return otherwise;
            var v = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at + 8)));
            at += 12;
            return v;
        }
        var position = (flags & 1) != 0 ? Read3(Vector3.Zero) : Vector3.Zero;
        var rotation = (flags & 2) != 0 ? Read3(Vector3.Zero) : Vector3.Zero;
        var scale = (flags & 4) != 0 ? Read3(Vector3.One) : Vector3.One;
        return Matrix4x4.CreateScale(scale) * Rotation(rotation.X, rotation.Y, rotation.Z) * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>The engine's angles: yaw turns about Z (up), pitch about X, roll about Y, applied roll first.</summary>
    public static Matrix4x4 Rotation(float roll, float pitch, float yaw)
    {
        const float toRadians = MathF.PI / 180;
        return Matrix4x4.CreateRotationY(roll * toRadians) * Matrix4x4.CreateRotationX(pitch * toRadians) * Matrix4x4.CreateRotationZ(yaw * toRadians);
    }

    private static string? Name(object? value) => value is string s && s.Length > 0 && s != "None" ? s : null;
}
