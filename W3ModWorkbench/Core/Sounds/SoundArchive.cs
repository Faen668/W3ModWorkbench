using System.Buffers.Binary;
using System.IO;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// A sound in a sound cache or a speech file, read from where it lies in the file. A voice line's sound is kept after
/// its length, which is left off.
/// </summary>
public sealed class SoundEntry : ArchiveEntry
{
    public required long Offset { get; init; }

    /// <summary>Whether it's a voice line, kept after its length.</summary>
    public bool Speech { get; init; }

    public override long SortKey => Offset;

    public override string FormatText => System.IO.Path.GetExtension(Path).Equals(".bnk", StringComparison.OrdinalIgnoreCase) ? "Sound bank"
        : Speech ? "Voice line" : "Streamed sound";

    public override byte[] Read(Stream? archiveStream)
    {
        if (archiveStream is null) throw new InvalidOperationException("A sound is read from its file.");
        archiveStream.Position = Offset;
        var data = new byte[PackedSize];
        archiveStream.ReadExactly(data);
        if (!Speech) return data;
        int length = BinaryPrimitives.ReadInt32LittleEndian(data);
        return length > 0 && length <= data.Length - 4 ? data.AsSpan(4, length).ToArray() : data[4..];
    }
}

/// <summary>
/// A sound cache (<c>soundspc.cache</c>) to browse: its banks under <c>sounds\banks</c> and its streamed sounds, named by
/// their IDs, under <c>sounds\streamed</c>.
/// </summary>
public sealed class SoundCacheArchive : IArchive
{
    public const string Banks = @"sounds\banks\", Streamed = @"sounds\streamed\";

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => true;

    private SoundCacheArchive(string path, List<ArchiveEntry> entries)
    {
        FilePath = path;
        Owner = ArchiveEntry.OwnerOf(path);
        Entries = entries;
    }

    public static SoundCacheArchive Open(string path)
    {
        var cache = SoundCache.Open(path);
        var entries = new List<ArchiveEntry>();
        var archive = new SoundCacheArchive(path, entries);
        foreach (var (name, offset, size) in cache.Index.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            string folder = name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ? Banks : Streamed;
            entries.Add(new SoundEntry { Archive = archive, Path = folder + name, Offset = offset, Size = (int)size, PackedSize = (int)size });
        }
        return archive;
    }

    /// <summary>The name a cache keeps a sound under, from its path here: <c>sounds\streamed\123.wem</c> is <c>123.wem</c>.</summary>
    public static string CacheName(string path) =>
        path.StartsWith(Banks, StringComparison.OrdinalIgnoreCase) ? path[Banks.Length..]
        : path.StartsWith(Streamed, StringComparison.OrdinalIgnoreCase) ? path[Streamed.Length..] : System.IO.Path.GetFileName(path);
}

/// <summary>
/// A language's voice lines (<c>enpc.w3speech</c>) to browse: each line's sound under <c>speech\&lt;language&gt;</c>,
/// named by the ID of the string it says.
/// </summary>
public sealed class SpeechArchive : IArchive
{
    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => true;
    public W3Language Language { get; }

    private SpeechArchive(string path, W3Language language, List<ArchiveEntry> entries)
    {
        FilePath = path;
        Language = language;
        Owner = ArchiveEntry.OwnerOf(path);
        Entries = entries;
    }

    /// <summary>The language a speech file is for, by its name (<c>enpc.w3speech</c> is English), or null.</summary>
    public static W3Language? LanguageOf(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("pc", StringComparison.OrdinalIgnoreCase) ? W3Strings.Language(name[..^2]) : null;
    }

    public static SpeechArchive Open(string path)
    {
        var language = LanguageOf(path) ?? throw new InvalidDataException("The speech file's name doesn't say its language.");
        var speech = W3Speech.Open(path, language);
        var entries = new List<ArchiveEntry>(speech.Count);
        var archive = new SpeechArchive(path, language, entries);
        foreach (var (id, offset, size) in speech.Index.OrderBy(l => l.StringId))
            entries.Add(new SoundEntry { Archive = archive, Path = $@"speech\{language.Handle}\{id}.wem", Offset = offset, Size = (int)size, PackedSize = (int)size, Speech = true });
        return archive;
    }

    /// <summary>A voice line's language and string ID from its path here (<c>speech\en\1234.wem</c>), or null.</summary>
    public static (W3Language Language, uint StringId)? LineOf(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("speech", StringComparison.OrdinalIgnoreCase)) return null;
        if (W3Strings.Language(parts[1]) is not { } language || !uint.TryParse(System.IO.Path.GetFileNameWithoutExtension(parts[2]), out uint id)) return null;
        return (language, id);
    }
}
