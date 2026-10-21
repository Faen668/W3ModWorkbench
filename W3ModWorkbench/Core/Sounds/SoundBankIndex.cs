using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// The game's sound banks read once, in the background: which banks play each sound (through a sound object or a music
/// track), which bank keeps each sound that's kept in a bank (a bank's sounds are often kept in another, a media bank),
/// and the events the game plays sounds by (a footstep's <c>g_footstep_walk</c>), each with the sounds it can play.
/// </summary>
public sealed class SoundBankIndex
{
    /// <summary>The banks that play a sound, by the sound's ID.</summary>
    public required IReadOnlyDictionary<uint, List<(string Bank, BankSound Sound)>> PlayedBy { get; init; }

    /// <summary>The bank that keeps a sound, by the sound's ID.</summary>
    public required IReadOnlyDictionary<uint, string> KeptIn { get; init; }

    /// <summary>Each event, by its ID: the bank it's in and the objects its actions act on (what it plays, stops, sets…).</summary>
    public required IReadOnlyDictionary<uint, (string Bank, List<(ushort Action, uint Target)> Actions)> Events { get; init; }

    /// <summary>The objects under each object (a container's sounds), from each one's parent.</summary>
    public required IReadOnlyDictionary<uint, List<uint>> Children { get; init; }

    /// <summary>The sounds each sound object or music track plays, by its ID, with its bank.</summary>
    public required IReadOnlyDictionary<uint, List<(string Bank, BankSound Sound)>> SourcesOf { get; init; }

    private static readonly Dictionary<string, Task<SoundBankIndex>> Known = new(StringComparer.OrdinalIgnoreCase);

    // the objects a sound hierarchy is made of, each with a parent
    private static readonly HashSet<byte> NodeTypes = [2, 5, 6, 7, 9, 10, 11, 12, 13];

    /// <summary>
    /// An event's ID from its name, as Wwise makes it: the lowercase name's 32-bit FNV-1 hash (each byte multiplied in,
    /// then XORed).
    /// </summary>
    public static uint Hash(string name)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
        {
            hash *= 16777619;
            hash ^= b;
        }
        return hash;
    }

    public static Task<SoundBankIndex> ForGame(string game)
    {
        lock (Known)
        {
            if (!Known.TryGetValue(game, out var task) || task.IsFaulted)
                Known[game] = task = Task.Run(async () => Build(await SoundCache.ForGame(game)));
            return task;
        }
    }

    private static SoundBankIndex Build(IReadOnlyList<SoundCache> caches)
    {
        var playedBy = new Dictionary<uint, List<(string, BankSound)>>();
        var keptIn = new Dictionary<uint, string>();
        var sourcesOf = new Dictionary<uint, List<(string, BankSound)>>();
        var events = new Dictionary<uint, (string, List<(ushort, uint)>)>();
        var actions = new Dictionary<uint, (ushort Type, uint Target)>();
        var nodes = new List<(uint Id, byte[] Head)>();
        foreach (var cache in caches)
            foreach (var (name, _) in cache.Files.Where(f => f.Name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)))
            {
                SoundBank bank;
                try
                {
                    if (cache.Read(name) is not { } data) continue;
                    bank = SoundBank.Read(data);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { continue; }
                foreach (var id in bank.Embedded.Keys) keptIn.TryAdd(id, name);
                foreach (var sound in bank.Sounds)
                {
                    if (!playedBy.TryGetValue(sound.SourceId, out var list)) playedBy[sound.SourceId] = list = [];
                    if (!list.Any(l => l.Item1 == name && l.Item2.ObjectId == sound.ObjectId)) list.Add((name, sound));
                    if (!sourcesOf.TryGetValue(sound.ObjectId, out var sources)) sourcesOf[sound.ObjectId] = sources = [];
                    sources.Add((name, sound));
                }
                var d = bank.Data;
                foreach (var (type, id, offset, length) in bank.Hierarchy)
                {
                    if (type == 4 && length >= 1)
                    {
                        // an event: a byte's count of actions, then their IDs
                        int count = d[offset];
                        var ids = new List<uint>();
                        for (int i = 0; i < count && 1 + (i + 1) * 4 <= length; i++) ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset + 1 + i * 4)));
                        events.TryAdd(id, (name, ids.Select(a => ((ushort)0, a)).ToList()));
                    }
                    else if (type == 3 && length >= 6)
                        // an action: its type (0x0403 plays), then the ID of what it acts on
                        actions.TryAdd(id, (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset + 2))));
                    else if (NodeTypes.Contains(type))
                        // only its start is kept, where its parent is, not the whole bank
                        nodes.Add((id, d.AsSpan(offset, Math.Min(length, 96)).ToArray()));
                }
            }
        // each event's actions with their types and targets
        foreach (var id in events.Keys.ToList())
        {
            var (bank, list) = events[id];
            events[id] = (bank, list.Where(a => actions.ContainsKey(a.Item2)).Select(a => actions[a.Item2]).Select(a => (a.Type, a.Target)).ToList());
        }
        // each node's parent: the first ID of another node near its start (after its own sources, for a sound), the
        // parent its base parameters keep
        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        var children = new Dictionary<uint, List<uint>>();
        foreach (var (id, head) in nodes)
            for (int i = 0; i + 4 <= head.Length; i++)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(i));
                if (value == id || value == 0 || !nodeIds.Contains(value)) continue;
                if (!children.TryGetValue(value, out var under)) children[value] = under = [];
                under.Add(id);
                break;
            }
        return new SoundBankIndex { PlayedBy = playedBy, KeptIn = keptIn, Events = events, Children = children, SourcesOf = sourcesOf };
    }

    /// <summary>Whether the game has an event of this name.</summary>
    public bool HasEvent(string name) => Events.ContainsKey(Hash(name));

    /// <summary>
    /// The sounds an event can play: those of the objects its play actions play, and of everything under them (a
    /// random container's choices, a switch's cases). Empty when there's no such event or it plays nothing.
    /// </summary>
    public List<(string Bank, BankSound Sound)> SoundsOf(string eventName)
    {
        var found = new List<(string, BankSound)>();
        if (!Events.TryGetValue(Hash(eventName), out var e)) return found;
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>(e.Actions.Where(a => a.Action is 0x0403 or 0x0405 or 0x0406).Select(a => a.Target));
        while (stack.Count > 0)
        {
            uint node = stack.Pop();
            if (!seen.Add(node) || seen.Count > 20000) continue;
            if (SourcesOf.TryGetValue(node, out var sources)) found.AddRange(sources);
            if (Children.TryGetValue(node, out var under)) foreach (var child in under) stack.Push(child);
        }
        return found.DistinctBy(f => f.Item2.SourceId).ToList();
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
