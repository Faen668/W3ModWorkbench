using System.IO;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Quests;

/// <summary>
/// The quests the game runs: the main game's (game\witcher3.redgame names quests\witcher3_quest.w2quest) and each DLC's,
/// which its .reddlc names (the official DLC's and any mod installed as DLC). Every phase reachable from these through
/// phase blocks is one the game can run; the rest are tests and leftovers.
/// </summary>
public static class QuestRoots
{
    public const string MainGame = @"game\witcher3.redgame";

    /// <summary>The root quests' paths, the main game's first.</summary>
    public static List<string> Find(ResourceLocator locator)
    {
        var roots = new List<string>();
        foreach (string definition in locator.AllPaths(".reddlc").Prepend(MainGame))
        {
            if (locator.ReadFile(definition) is not { } data) continue;
            Cr2wFile file;
            try { file = Cr2wFile.Read(data); }
            catch (InvalidDataException) { continue; }
            foreach (var import in file.Imports)
                if (import.Path.EndsWith(".w2quest", StringComparison.OrdinalIgnoreCase) && !roots.Contains(import.Path, StringComparer.OrdinalIgnoreCase))
                    roots.Add(import.Path);
        }
        return roots;
    }

    /// <summary>
    /// Every quest file reachable from <paramref name="roots"/>, each with the phase blocks that run it (none for a
    /// root). <paramref name="read"/> reads a file, or returns null when it's missing or unreadable.
    /// </summary>
    public static Dictionary<string, List<QuestBlock>> Reachable(IEnumerable<string> roots, Func<string, QuestFile?> read)
    {
        var usedBy = new Dictionary<string, List<QuestBlock>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        foreach (string root in roots)
            if (usedBy.TryAdd(root, [])) pending.Push(root);
        while (pending.TryPop(out string? path))
        {
            if (read(path) is not { } file) continue;
            foreach (var block in file.AllBlocks())
            {
                if (block.ExternalPhase is not { } phase) continue;
                if (usedBy.TryGetValue(phase, out var list)) list.Add(block);
                else
                {
                    usedBy[phase] = [block];
                    pending.Push(phase);
                }
            }
        }
        return usedBy;
    }
}
