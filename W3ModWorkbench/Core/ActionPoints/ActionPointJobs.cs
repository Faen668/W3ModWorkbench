using System.IO;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.ActionPoints;

/// <summary>
/// One job NPCs do at action points, whatever body does it: the job trees that differ only by who they're for
/// (stand_m_sweeping_floor, stand_w_sweeping_floor), the DLC folder they're in, or a numbered or left/right variant
/// playing the same animations. Its name's parts from the trees' file names: the pose (stand, sit, kneel, lie, lean,
/// or null for none), the words after it, and whether it's a rain shelter's; who its trees are for (m, w, d, c, in
/// that order), and what its animations show (their names without the body, start, stop and loop numbers), the most
/// played first.
/// </summary>
public sealed record ActionPointJob(string Key, string? Pose, string Words, bool Rain, IReadOnlyList<string> JobTrees, string Bodies, IReadOnlyList<string> Activities);

/// <summary>
/// The job trees put together into jobs. The game names a job tree pose_bodies_what_jt (sit_mw_ground_hopeless_jt,
/// rain_stand_m_spit_jt), the bodies a run of m (men), w (women), d (dwarves) and c (children); trees whose names are
/// the same without it are one job. Names that differ by a number, a side or "dynamic" too (kneel_playing_stick_01 and
/// _02, lie_sleep_bed_l and _r) are one job only when they play an animation in common: sit_ground_01 (a broken leg)
/// and sit_ground_02 (sewing sails) are two.
/// </summary>
public static partial class ActionPointJobs
{
    public static readonly string[] Poses = ["stand", "sit", "kneel", "lie", "lean"];
    private const string BodyOrder = "mwdc";

    [GeneratedRegex("^[mwdc]{1,4}$")]
    private static partial Regex BodyToken();

    [GeneratedRegex(@"^(\d+|l|r|left|right|dynamic)$")]
    private static partial Regex VariantToken();

    // what an animation's name has besides what it shows: whose it is, its part (start, loop…), its side (a bed's left
    // and right are one job) and its number
    [GeneratedRegex(@"^(man|woman|child|kid|dwarf|work|quest|noble|nobel|peasant|vanilla|npc|female|male|start\d*|strat\d*|stat|stop|end|begin|sitdown|standup|loop\d*|idle\d*|jt|left|right|l|r|lf|rf|lh|rh|[mwdc]{1,4}|\d+)$")]
    private static partial Regex AnimationNoise();

    [GeneratedRegex(@"[_\s()]+")]
    private static partial Regex Separators();

    private sealed record Tree(string Path, string Exact, string Loose, string? Pose, string Words, string ExactWords, bool Rain, string Bodies, IReadOnlyList<string> Stems);

    /// <summary>The jobs the job trees make, each with its trees in the order given (most used first, say).</summary>
    public static IReadOnlyList<ActionPointJob> Build(IEnumerable<string> jobTrees, IReadOnlyDictionary<string, JobTreeInfo> infos)
    {
        var trees = jobTrees.Select(p => Parse(p, infos.GetValueOrDefault(p))).ToList();
        var parent = Enumerable.Range(0, trees.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        foreach (var same in Enumerable.Range(0, trees.Count).GroupBy(i => trees[i].Exact))
            foreach (int i in same.Skip(1)) Union(same.First(), i);
        foreach (var near in Enumerable.Range(0, trees.Count).GroupBy(i => trees[i].Loose))
        {
            var members = near.ToList();
            for (int a = 0; a < members.Count; a++)
                for (int b = a + 1; b < members.Count; b++)
                    if (trees[members[a]].Stems.Intersect(trees[members[b]].Stems).Any()) Union(members[a], members[b]);
        }
        // a pose's trees that play just the same animations, whatever their names say (stand_m_work_with_vine, stand_w_work_with_wine);
        // not the trees with no pose, an animal's or a monster's, whose animations are all "eating"
        foreach (var same in Enumerable.Range(0, trees.Count).Where(i => trees[i].Pose is not null && trees[i].Stems.Count > 0)
            .GroupBy(i => (trees[i].Pose, trees[i].Rain, string.Join("|", trees[i].Stems.Distinct().Order(StringComparer.Ordinal)))))
            foreach (int i in same.Skip(1)) Union(same.First(), i);

        var groups = Enumerable.Range(0, trees.Count).GroupBy(Find).Select(g => g.Select(i => trees[i]).ToList()).ToList();
        // two jobs with the same name without their numbers keep their numbers in the key, and a numbered one says what its
        // animations show in the name (sit_ground, and sit_ground_01 and _02: "ground, broken leg" and "ground, sew sails")
        var sharedLoose = groups.GroupBy(g => g[0].Loose).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return [.. groups.Select(g =>
        {
            var first = g[0];
            bool shared = sharedLoose.Contains(first.Loose);
            string bodies = new([.. BodyOrder.Where(c => g.Any(t => t.Bodies.Contains(c)))]);
            var activities = g.SelectMany(t => t.Stems).GroupBy(s => s).OrderByDescending(s => s.Count()).Select(s => s.Key.Replace('_', ' ')).ToList();
            string words = !shared || first.Exact == first.Loose ? first.Words
                : activities.FirstOrDefault() is { } activity ? (first.Words.Length > 0 ? $"{first.Words}, {activity}" : activity)
                : first.ExactWords;
            return new ActionPointJob(shared ? first.Exact : first.Loose, first.Pose, words, first.Rain, [.. g.Select(t => t.Path)], bodies, activities);
        })];
    }

    private static Tree Parse(string path, JobTreeInfo? info)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.EndsWith("_jt", StringComparison.Ordinal)) name = name[..^3];
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool rain = tokens.Count > 1 && tokens[0] == "rain";
        if (rain) tokens.RemoveAt(0);
        string bodies = string.Concat(tokens.Where(t => BodyToken().IsMatch(t)));
        var rest = tokens.Where(t => !BodyToken().IsMatch(t)).ToList();
        var loose = rest.Where(t => !VariantToken().IsMatch(t)).ToList();
        if (loose.Count == 0) loose = rest;
        string? pose = rest.Count > 0 && Poses.Contains(rest[0]) ? rest[0] : null;
        string prefix = rain ? "rain:" : ""; // loc-ignore: a key
        var stems = (info?.Animations ?? []).Select(Stem).Where(s => s.Length > 0).ToList();
        return new Tree(path, prefix + string.Join("_", rest), prefix + string.Join("_", loose), pose,
            string.Join(" ", pose is null ? loose : loose.Skip(1)), string.Join(" ", pose is null ? rest : rest.Skip(1)), rain, bodies, stems);
    }

    /// <summary>What an animation shows: its name without whose it is, its part and its number (man_work_sit_chair_01 is sit_chair).</summary>
    public static string Stem(string animation) =>
        string.Join("_", Separators().Split(animation.ToLowerInvariant()).Where(t => t.Length > 0 && !AnimationNoise().IsMatch(t)));
}
