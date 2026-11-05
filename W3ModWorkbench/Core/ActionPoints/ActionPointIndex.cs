using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench.Core.ActionPoints;

/// <summary>
/// An action point's job tree (.w2job): the categories its root node accepts, which is all a selector asks an action
/// point for (CActionPointComponent::GetActionCategories reads only the root), and the animations its actions play.
/// </summary>
public sealed record JobTreeInfo(IReadOnlyList<string> Categories, IReadOnlyList<string> Animations);

/// <summary>
/// An action point the worlds place: one CActionPointComponent of an entity in a layer (an entity can have several).
/// Where it stands and which way it faces (radians), its entity's tags (what selectors look for, with the job tree's
/// categories), its job tree (null for none), its component's name, and the template it's from when the layer keeps
/// one (cooking flattens most into the layer). Mod is the installed mod that adds it, null for the game's own;
/// ModChanged the mod that moved one of the game's points or changed its job tree.
/// </summary>
public sealed record ActionPoint(string World, string Layer, Vector3 Position, float Yaw, IReadOnlyList<string> Tags, string? JobTree, string Component, string? Template)
{
    public string? Mod { get; init; }
    public string? ModChanged { get; init; }
}

/// <summary>
/// Every action point the game's worlds place, from each world's layers (DLC's and mods' too), with the categories and
/// animations of their job trees. The game's nine worlds place about 50,000, read in a few seconds.
/// </summary>
public sealed class ActionPointIndex
{
    public required IReadOnlyList<ActionPoint> Points { get; init; }
    public required IReadOnlyDictionary<string, JobTreeInfo> JobTrees { get; init; }
    public required IReadOnlyList<string> Worlds { get; init; }

    /// <summary>The quest or story character each of their action points is placed for (the rest, the open world's, have none).</summary>
    public required IReadOnlyDictionary<ActionPoint, QuestLink> Quests { get; init; }

    public JobTreeInfo? JobTreeOf(ActionPoint p) => p.JobTree is { } path && JobTrees.TryGetValue(path, out var info) ? info : null;

    /// <summary>The quest an action point is placed for, or null for the open world's.</summary>
    public QuestLink? QuestOf(ActionPoint p) => Quests.TryGetValue(p, out var quest) ? quest : null;

    /// <summary>The categories an action point offers: its job tree's root node's.</summary>
    public IReadOnlyList<string> CategoriesOf(ActionPoint p) => JobTreeOf(p)?.Categories ?? [];

    private sealed record TemplatePart(string Name, string? JobTree, Matrix4x4 Place);

    private sealed record TemplatePoints(IReadOnlyList<string> Tags, IReadOnlyList<TemplatePart> Parts);

    /// <summary>
    /// Reads every world's action points. With <paramref name="vanilla"/>, a locator that reads no mod, a point in a
    /// layer a mod brings is the mod's when the game's own copy of the layer hasn't one within a metre of it: a mod that
    /// adds a few points to a town's layer marks only those, not the whole town. One the game has where it stands but
    /// with another job tree, or with its job tree within a metre, the mod changed (nudging the game's points is common).
    /// </summary>
    public static ActionPointIndex Build(ResourceLocator locator, IProgress<string>? progress, CancellationToken token, ResourceLocator? vanilla = null)
    {
        var worlds = locator.AllPaths(".w2w").Where(w => !w.StartsWith(@"engine\", StringComparison.OrdinalIgnoreCase)).ToList();
        var layers = locator.AllPaths(".w2l");
        var templates = new ConcurrentDictionary<string, TemplatePoints?>(StringComparer.OrdinalIgnoreCase);
        var vanillaTemplates = new ConcurrentDictionary<string, TemplatePoints?>(StringComparer.OrdinalIgnoreCase);
        var points = new ConcurrentBag<ActionPoint>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token };
        foreach (string worldPath in worlds)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(worldPath);
            WorldFile world;
            try { world = WorldFile.Read(locator.ReadFile(worldPath) ?? throw new FileNotFoundException(worldPath), worldPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            Parallel.ForEach(world.GameLayers(layers), options, layer =>
            {
                var found = PointsIn(locator, templates, worldPath, layer);
                if (found.Count == 0) return;
                if (vanilla is not null && locator.ModNameOf(layer) is { } mod)
                {
                    // the game's own points in the layer; none for a layer the mod adds
                    var game = PointsIn(vanilla, vanillaTemplates, worldPath, layer);
                    var same = game.Select(SameSpot).ToHashSet();
                    found = [.. found.Select(p => same.Contains(SameSpot(p)) ? p
                        : game.Any(g => Vector3.DistanceSquared(g.Position, p.Position) < 1) ? p with { ModChanged = mod }
                        : p with { Mod = mod })];
                }
                foreach (var p in found) points.Add(p);
            });
        }

        progress?.Report("job trees"); // loc-ignore: a progress step's name, shown inside a translated "Reading {what}…"
        var jobTrees = new ConcurrentDictionary<string, JobTreeInfo>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(points.Select(p => p.JobTree).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(), options, path =>
        {
            if (ReadJobTree(locator, path) is { } info) jobTrees[path] = info;
        });

        var quests = new Dictionary<ActionPoint, QuestLink>(ReferenceEqualityComparer.Instance);
        foreach (var p in points)
            if (QuestPoints.Of(p) is { } quest) quests[p] = quest;
        // then the open world's layers named for a character their world has (a main character's name over a secondary's)
        var characters = quests.Where(q => q.Value.IsCharacter).GroupBy(q => (q.Key.World.ToLowerInvariant(), q.Value.Code))
            .ToDictionary(g => g.Key, g => g.Min(q => q.Value.Kind));
        foreach (var p in points)
            if (!quests.ContainsKey(p) && QuestPoints.NamedFor(p, characters) is { } named) quests[p] = named;
        return new ActionPointIndex
        {
            Points = [.. points.OrderBy(p => p.World, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Layer, StringComparer.OrdinalIgnoreCase)],
            JobTrees = new Dictionary<string, JobTreeInfo>(jobTrees, StringComparer.OrdinalIgnoreCase),
            Worlds = worlds,
            Quests = quests,
        };
    }

    /// <summary>A layer's action points, as a locator reads it: none when it can't be read or has none.</summary>
    private static List<ActionPoint> PointsIn(ResourceLocator locator, ConcurrentDictionary<string, TemplatePoints?> templates, string worldPath, string layer)
    {
        var points = new List<ActionPoint>();
        Cr2wFile file;
        try { file = Cr2wFile.Read(locator.ReadFile(layer) ?? []); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return points; }
        // most layers have none: skip reading their entities
        if (!file.Exports.Any(x => x.ClassName is "CActionPointComponent" or "CActionPoint")) return points;
        foreach (var e in WorldFile.Entities(file))
        {
            // the layer's tags when it keeps them (cooking writes them in), else the template's
            var own = e.Object["tags"] is byte[] raw ? file.Tags(raw) : null;
            var inLayer = e.Components.Where(c => c.ClassName == "CActionPointComponent").ToList();
            if (inLayer.Count > 0)
            {
                foreach (var c in inLayer)
                    points.Add(new ActionPoint(worldPath, layer, c.Place.Translation, e.Yaw, own ?? [], c.Resource, c.Name, e.Template));
                continue;
            }
            // a layer that keeps the template has the action point in the template's own components
            if (e.ClassName != "CActionPoint" || e.Template is not { } template) continue;
            if (templates.GetOrAdd(template, t => ReadTemplate(locator, t)) is not { } parts) continue;
            var place = e.Object["transform"] is byte[] t ? EntityFile.Transform(t) : Matrix4x4.CreateTranslation(e.Position);
            foreach (var part in parts.Parts)
                points.Add(new ActionPoint(worldPath, layer, (part.Place * place).Translation, e.Yaw, own ?? parts.Tags, part.JobTree, part.Name, template));
        }
        return points;
    }

    /// <summary>Where a point stands (to the centimetre) and its job tree: the same in a mod's copy of a layer as in the game's when the mod leaves it be.</summary>
    private static (int, int, int, string) SameSpot(ActionPoint p) =>
        ((int)MathF.Round(p.Position.X * 100), (int)MathF.Round(p.Position.Y * 100), (int)MathF.Round(p.Position.Z * 100), p.JobTree?.ToLowerInvariant() ?? "");

    /// <summary>
    /// A template's action points: its entity's tags and its CActionPointComponents, each with its job tree and where it
    /// stands on the entity. A cooked template keeps them in its flat data; one that isn't, in the file itself.
    /// </summary>
    private static TemplatePoints? ReadTemplate(ResourceLocator locator, string template)
    {
        Cr2wFile file;
        try { file = Cr2wFile.Read(locator.ReadFile(template) ?? []); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
        if (file.First("CEntityTemplate") is not { } t) return null;
        var source = file;
        if (t.Bytes("flatCompiledData") is { Length: > 0 } flat)
            try { source = Cr2wFile.Read(flat); } catch (InvalidDataException) { }
        var entity = source == file ? (t["entityObject"] is RedHandle h ? file.Object(h) : null) : source.Exports.Count > 0 ? source.Object(0) : null;
        var tags = entity?["tags"] is byte[] raw ? source.Tags(raw) : [];
        var parts = new List<TemplatePart>();
        for (int i = 0; i < source.Exports.Count; i++)
        {
            if (source.Exports[i].ClassName != "CActionPointComponent") continue;
            var c = source.Object(i);
            parts.Add(new TemplatePart(c["name"] as string ?? "", c["jobTreeRes"] is RedHandle j ? source.PathOf(j) : null,
                c["transform"] is byte[] place ? EntityFile.Transform(place) : Matrix4x4.Identity));
        }
        return parts.Count > 0 ? new TemplatePoints(tags, parts) : null;
    }

    /// <summary>A job tree's root categories and the animations its actions play, or null when it can't be read.</summary>
    private static JobTreeInfo? ReadJobTree(ResourceLocator locator, string path)
    {
        Cr2wFile file;
        try { file = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
        if (file.First("CJobTree")?["jobTreeRootNode"] is not RedHandle h || file.Object(h) is not { } root) return null;
        var animations = new List<string>();
        for (int i = 0; i < file.Exports.Count; i++)
            if (file.Exports[i].ClassName == "CJobAction" && file.Object(i)["animName"]?.ToString() is { Length: > 0 } anim) animations.Add(anim);
        return new JobTreeInfo([.. root.Array("validCategories").Select(c => c?.ToString() ?? "").Where(c => c.Length > 0)],
            [.. animations.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }
}

/// <summary>
/// The action points the rain reaction sends townsfolk to (CRainActionPointSelector): the closest within 30 metres that
/// is tagged with the NPC's shelter category and whose job tree accepts that same category. Which category an NPC asks
/// for goes by its body: a woman's, a child's, a dwarf's, else a man's.
/// </summary>
public static class RainShelters
{
    public const string Man = "rain_shelter_man", Woman = "rain_shelter_woman", Child = "rain_shelter_child", Dwarf = "rain_shelter_dwarf";

    public static readonly string[] Categories = [Man, Woman, Child, Dwarf];

    /// <summary>The shelter categories an action point is tagged with, in the order of <see cref="Categories"/>.</summary>
    public static IEnumerable<string> TaggedFor(ActionPoint p) => Categories.Where(c => p.Tags.Contains(c, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether an action point is tagged for the rain at all (with "rain_shelter", which no selector asks for, too).</summary>
    public static bool IsTagged(ActionPoint p) => p.Tags.Any(t => t.StartsWith("rain_shelter", StringComparison.OrdinalIgnoreCase));

    /// <summary>The shelter categories the rain reaction can send an NPC to this action point for: tagged with one and its job tree accepting it.</summary>
    public static IEnumerable<string> UsableFor(ActionPoint p, IReadOnlyList<string> categories) =>
        TaggedFor(p).Where(c => categories.Contains(c, StringComparer.OrdinalIgnoreCase));
}
