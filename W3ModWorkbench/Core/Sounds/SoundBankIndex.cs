using System.IO;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// The game's sound banks read once, in the background: which banks play each sound (through a sound object or a music
/// track), and which bank keeps each sound that's kept in a bank, as a bank's sounds are often kept in another (a media
/// bank) rather than in itself.
/// </summary>
public sealed class SoundBankIndex
{
    /// <summary>The banks that play a sound, by the sound's ID.</summary>
    public required IReadOnlyDictionary<uint, List<(string Bank, BankSound Sound)>> PlayedBy { get; init; }

    /// <summary>The bank that keeps a sound, by the sound's ID.</summary>
    public required IReadOnlyDictionary<uint, string> KeptIn { get; init; }

    private static readonly Dictionary<string, Task<SoundBankIndex>> Known = new(StringComparer.OrdinalIgnoreCase);

    public static Task<SoundBankIndex> ForGame(string game)
    {
        lock (Known)
        {
            if (!Known.TryGetValue(game, out var task) || task.IsFaulted)
                Known[game] = task = Task.Run(async () =>
                {
                    var playedBy = new Dictionary<uint, List<(string, BankSound)>>();
                    var keptIn = new Dictionary<uint, string>();
                    foreach (var cache in await SoundCache.ForGame(game))
                        foreach (var (name, _) in cache.Files.Where(f => f.Name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                if (cache.Read(name) is not { } data) continue;
                                var bank = SoundBank.Read(data);
                                foreach (var id in bank.Embedded.Keys) keptIn.TryAdd(id, name);
                                foreach (var sound in bank.Sounds)
                                {
                                    if (!playedBy.TryGetValue(sound.SourceId, out var list)) playedBy[sound.SourceId] = list = [];
                                    if (!list.Any(l => l.Item1 == name && l.Item2.ObjectId == sound.ObjectId)) list.Add((name, sound));
                                }
                            }
                            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
                        }
                    return new SoundBankIndex { PlayedBy = playedBy, KeptIn = keptIn };
                });
            return task;
        }
    }

    /// <summary>
    /// A sound wherever the game keeps it: streamed in a sound cache, or kept in a bank. Null when it's in neither.
    /// Reads the game's files, so it belongs off the UI thread.
    /// </summary>
    public byte[]? Sound(IReadOnlyList<SoundCache> caches, uint id)
    {
        string streamed = $"{id}.wem";
        if (caches.FirstOrDefault(c => c.Has(streamed)) is { } cache) return cache.Read(streamed);
        if (!KeptIn.TryGetValue(id, out var bankName) || caches.FirstOrDefault(c => c.Has(bankName))?.Read(bankName) is not { } bank) return null;
        return SoundBank.Read(bank).EmbeddedSound(id);
    }
}
