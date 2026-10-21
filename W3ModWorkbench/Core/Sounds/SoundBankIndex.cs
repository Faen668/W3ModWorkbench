using System.IO;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// Which banks play each sound, by the sound's ID: every bank of the game's sound caches read once, in the background,
/// for the sounds its sound objects and music tracks play.
/// </summary>
public static class SoundBankIndex
{
    private static readonly Dictionary<string, Task<Dictionary<uint, List<(string Bank, BankSound Sound)>>>> Known = new(StringComparer.OrdinalIgnoreCase);

    public static Task<Dictionary<uint, List<(string Bank, BankSound Sound)>>> ForGame(string game)
    {
        lock (Known)
        {
            if (!Known.TryGetValue(game, out var task) || task.IsFaulted)
                Known[game] = task = Task.Run(async () =>
                {
                    var index = new Dictionary<uint, List<(string, BankSound)>>();
                    foreach (var cache in await SoundCache.ForGame(game))
                        foreach (var (name, _) in cache.Files.Where(f => f.Name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                if (cache.Read(name) is not { } data) continue;
                                foreach (var sound in SoundBank.Read(data).Sounds)
                                {
                                    if (!index.TryGetValue(sound.SourceId, out var list)) index[sound.SourceId] = list = [];
                                    if (!list.Any(l => l.Item1 == name && l.Item2.ObjectId == sound.ObjectId)) list.Add((name, sound));
                                }
                            }
                            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
                        }
                    return index;
                });
            return task;
        }
    }
}
