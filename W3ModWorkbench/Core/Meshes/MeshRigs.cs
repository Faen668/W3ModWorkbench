using System.IO;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>Finding the rigs a skinned mesh was made for, when there's no entity to say: by which have its bones.</summary>
public static class MeshRigs
{
    /// <summary>The base characters' rigs, for meshes nothing names a rig for: the one most of their bones are in is used.</summary>
    public static readonly string[] BaseRigs =
    [
        @"characters\base_entities\man_base\man_base.w2rig", @"characters\base_entities\woman_base\woman_base.w2rig",
        @"characters\base_entities\child_base\child_base.w2rig", @"characters\base_entities\dwarf_base\dwarf_base.w2rig",
    ];

    /// <summary>
    /// The rigs that have the mesh's bones, best first: the one with the most of them, then any that have some of the
    /// rest, as a head's face bones are in its face (.w3fac) and its neck in the body's rig. They're looked for in the
    /// mesh's folder and each folder above it (not their other subfolders, where other characters' are), nearest first,
    /// which wins a tie, then in the base characters'. Empty when none has any, or the mesh has no bones.
    /// </summary>
    public static List<(Skeleton Rig, string Path)> Find(MeshFile mesh, string meshPath, ResourceLocator locator)
    {
        var found = new List<(Skeleton, string)>();
        if (mesh.BoneNames.Length == 0) return found;
        var near = new List<string>();
        var rigs = locator.AllPaths(".w2rig");
        var byFolder = rigs.Concat(locator.AllPaths(".w3fac")).ToLookup(r => Path.GetDirectoryName(r) ?? "", StringComparer.OrdinalIgnoreCase);
        for (string? folder = Path.GetDirectoryName(meshPath); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            near.AddRange(byFolder[folder]);

        // the people's base rigs share most of their bones' names, so the one the mesh's path names comes first and wins
        // a tie; then the base rig of any other creature the path names (gryphon_base for a gryphon's mesh, horse_base for
        // horse_items), as other creatures' rigs share bones' names with people too (the bear berserker's has a face)
        string lower = meshPath.ToLowerInvariant();
        int kind = lower.Contains("woman") ? 1 : lower.Contains("child") ? 2 : lower.Contains("dwarf") || lower.Contains("halfling") ? 3 : 0;
        var bases = BaseRigs.Skip(kind).Take(1).Concat(BaseRigs)
            .Concat(rigs.Where(r => CreatureOf(r) is { Length: > 2 } creature && lower.Contains(creature)));

        var candidates = new List<(Skeleton Rig, string Path, HashSet<string> Names)>();
        foreach (string path in near.Concat(bases).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (locator.ReadFile(path) is not { } data) continue;
                var rig = path.EndsWith(".w3fac", StringComparison.OrdinalIgnoreCase) ? MimicFace.Read(data).Skeleton : Skeleton.Read(data);
                candidates.Add((rig, path, new HashSet<string>(rig.Names, StringComparer.OrdinalIgnoreCase)));
            }
            catch (InvalidDataException)
            {
            }
        }

        var left = new HashSet<string>(mesh.BoneNames, StringComparer.OrdinalIgnoreCase);
        while (left.Count > 0)
        {
            var best = candidates.Select(c => (c, Named: left.Count(c.Names.Contains))).Where(c => c.Named > 0).OrderByDescending(c => c.Named).FirstOrDefault();
            if (best.Named == 0) break;
            found.Add((best.c.Rig, best.c.Path));
            left.ExceptWith(best.c.Names);
            candidates.Remove(best.c);
        }
        return found;
    }

    /// <summary>The creature a base entity's rig is for, from its folder (base_entities\gryphon_base\… is gryphon), else null.</summary>
    private static string? CreatureOf(string rigPath)
    {
        var parts = rigPath.ToLowerInvariant().Split('\\');
        int at = Array.IndexOf(parts, "base_entities");
        return at >= 0 && at + 2 < parts.Length ? parts[at + 1].Replace("_base", "") : null;
    }
}
