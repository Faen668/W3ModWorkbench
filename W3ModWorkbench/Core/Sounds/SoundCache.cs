using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// A sound cache (<c>soundspc.cache</c>): the game's Wwise sound banks (<c>.bnk</c>) and streamed sounds (<c>.wem</c>,
/// named by their ID), one after another. The file starts "CS3W" and its version (2), then eight bytes, where the list
/// of files is, how many there are, where their names are and how long the names are. Each file in the list is
/// twenty-four bytes: where its name starts in the names, where the file is and how long.
/// </summary>
public sealed class SoundCache
{
    private const uint Magic = 0x57335343; // "CS3W"

    public string Path { get; }
    private readonly Dictionary<string, (long Offset, long Size)> _files;

    private SoundCache(string path, Dictionary<string, (long, long)> files)
    {
        Path = path;
        _files = files;
    }

    public bool Has(string name) => _files.ContainsKey(name);

    /// <summary>Reads a cache's list of files; the files are read when they're asked for.</summary>
    public static SoundCache Open(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[48];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic) throw new InvalidDataException("This isn't a sound cache.");
        long list = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(16)), count = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(24));
        long namesAt = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(32));
        int namesSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(40));
        if (count < 0 || count > 1_000_000 || namesSize < 0 || namesAt + namesSize > fs.Length || list + count * 24 > fs.Length)
            throw new InvalidDataException("The sound cache's list makes no sense.");
        var names = new byte[namesSize];
        fs.Position = namesAt;
        fs.ReadExactly(names);
        var table = new byte[count * 24];
        fs.Position = list;
        fs.ReadExactly(table);
        var files = new Dictionary<string, (long, long)>((int)count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * 24, 24);
            long name = BinaryPrimitives.ReadInt64LittleEndian(entry), offset = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]), size = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (name < 0 || name >= names.Length) continue;
            int end = Array.IndexOf(names, (byte)0, (int)name);
            if (end < 0) end = names.Length;
            if (offset > 0 && size > 0 && size < 512 << 20) files.TryAdd(Encoding.ASCII.GetString(names, (int)name, end - (int)name), (offset, size));
        }
        return new SoundCache(path, files);
    }

    /// <summary>A file in the cache, or null when it hasn't got it.</summary>
    public byte[]? Read(string name)
    {
        if (!_files.TryGetValue(name, out var file)) return null;
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = file.Offset;
        var data = new byte[file.Size];
        fs.ReadExactly(data);
        return data;
    }

    private static readonly Dictionary<string, Task<List<SoundCache>>> Caches = [];

    /// <summary>
    /// The game's sound caches, the mods' and DLCs' first (as the game lets them replace its own sounds), then the
    /// game's. Read once, in the background.
    /// </summary>
    public static Task<List<SoundCache>> ForGame(string gameFolder)
    {
        lock (Caches)
        {
            if (!Caches.TryGetValue(gameFolder, out var task) || task.IsFaulted)
                Caches[gameFolder] = task = Task.Run(() =>
                {
                    var paths = new List<string>();
                    foreach (string folder in new[] { "mods", "dlc" })
                    {
                        string root = System.IO.Path.Combine(gameFolder, folder);
                        if (!Directory.Exists(root)) continue;
                        foreach (string mod in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                            paths.Add(System.IO.Path.Combine(mod, "content", "soundspc.cache"));
                    }
                    paths.Add(System.IO.Path.Combine(gameFolder, "content", "content0", "soundspc.cache"));
                    var caches = new List<SoundCache>();
                    foreach (string path in paths.Where(File.Exists))
                        try { caches.Add(Open(path)); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
                    return caches;
                });
            return task;
        }
    }

    /// <summary>
    /// The sound a cutscene's audio track plays (its <c>burnedAudioTrackName</c>): the bank of that name, and the
    /// sounds in it, each from its streamed <c>.wem</c> or from the bank itself, mixed together from the start. Null
    /// when the bank isn't found or has no sound the tool can play (only the Remastered's Opus sounds are read).
    /// </summary>
    public static (short[] Samples, int Channels)? Track(IReadOnlyList<SoundCache> caches, string name)
    {
        byte[]? Find(string file) => caches.FirstOrDefault(c => c.Has(file))?.Read(file);
        if (Find(name + ".bnk") is not { } bank) return null;
        var (embedded, sounds) = Bank(bank);
        var decoded = new List<(short[] Samples, int Channels)>();
        foreach (uint source in sounds.Distinct())
        {
            var riff = Find($"{source}.wem") ?? (embedded.TryGetValue(source, out var inside) ? inside : null);
            if (riff is null || riff.Length < 22 || BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(20)) != 0x3041) continue;
            try
            {
                var sound = WwiseOpus.Decode(riff);
                if (sound.Channels > 0 && sound.Samples.Length > 0) decoded.Add(sound);
            }
            catch (InvalidDataException)
            {
                // a sound that can't be decoded is left out
            }
        }
        if (decoded.Count == 0) return null;
        if (decoded.Count == 1) return decoded[0];
        // several at once: mixed, in stereo
        var mix = new int[decoded.Max(d => d.Samples.Length / d.Channels) * 2];
        foreach (var (samples, channels) in decoded)
            for (int i = 0; i < samples.Length / channels; i++)
            {
                mix[i * 2] += samples[i * channels];
                mix[i * 2 + 1] += samples[i * channels + (channels > 1 ? 1 : 0)];
            }
        return (mix.Select(s => (short)Math.Clamp(s, short.MinValue, short.MaxValue)).ToArray(), 2);
    }

    /// <summary>
    /// A sound bank's sounds: those it carries itself (DIDX lists them, DATA has them), by ID, and the source ID of each
    /// sound object in its hierarchy (HIRC objects of type 2: four bytes of codec, a byte saying whether it's streamed,
    /// then the source's ID).
    /// </summary>
    private static (Dictionary<uint, byte[]> Embedded, List<uint> Sounds) Bank(byte[] bank)
    {
        var embedded = new Dictionary<uint, byte[]>();
        var sounds = new List<uint>();
        var index = new List<(uint Id, int Offset, int Size)>();
        int data = -1;
        for (int at = 0; at + 8 <= bank.Length;)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at));
            int size = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 4));
            int body = at + 8;
            if (size < 0 || body + size > bank.Length) break;
            switch (tag)
            {
                case 0x58444944: // DIDX
                    for (int i = body; i + 12 <= body + size; i += 12)
                        index.Add((BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(i)), BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 4)), BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 8))));
                    break;
                case 0x41544144: // DATA
                    data = body;
                    break;
                case 0x43524948: // HIRC
                    int count = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(body));
                    for (int i = body + 4, n = 0; n < count && i + 9 <= body + size; n++)
                    {
                        byte type = bank[i];
                        int length = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 1));
                        if (type == 2 && i + 5 + 13 <= bank.Length) sounds.Add(BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(i + 9 + 5)));
                        i += 5 + length;
                    }
                    break;
            }
            at = body + size;
        }
        if (data >= 0)
            foreach (var (id, offset, size) in index)
                if (offset >= 0 && size > 0 && data + offset + size <= bank.Length) embedded.TryAdd(id, bank[(data + offset)..(data + offset + size)]);
        return (embedded, sounds);
    }
}
