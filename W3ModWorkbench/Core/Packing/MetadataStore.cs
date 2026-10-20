using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// Writes the <c>metadata.store</c> the game reads a folder of bundles by, as REDkit's <c>wcc_lite metadatastore</c>
/// does, byte for byte. It lists every bundle in the folder and every file in them, so the game finds a file without
/// opening the bundles.
/// <para>
/// The file is a header (its mark, version 7, the largest file packed and unpacked), then tables, each led by its count
/// as CR2W writes one (six bits, then seven a byte; an empty table is written 0x80):
/// the strings (bundle names, file paths, folder names); the files (path, sizes, how packed, which buffers are theirs);
/// where each file is (offset, packed size, bundle); the bundles (data size, where the data starts, name, first file,
/// file count); the buffers, by file, grouped by the file they belong to; the folders (name, parent); each file's
/// folder and name; and each path's 64-bit FNV-1a hash, sorted, which is what the game looks a path up by.
/// The files, the places and the bundles each start with an empty entry, so the first of each is number 1.
/// </para>
/// <para>
/// A file whose path, up to its first dot, ends "_censored" is left out, as wcc_lite leaves it out: the engine only
/// swaps those in for the censored version of the game.
/// </para>
/// </summary>
public static class MetadataStore
{
    private const uint Magic = 0x4D545603;
    private const int Version = 7;
    public const string FileName = "metadata.store";

    private sealed record Entry(string Path, long Offset, int Size, int PackedSize, int Compression);
    private sealed class Folder(string name, Folder? parent)
    {
        public string Name { get; } = name;
        public Folder? Parent { get; } = parent;
        public List<Folder> Children { get; } = [];
        public int Index { get; set; }
    }

    private sealed record BundleInfo(string Name, long DataStart, long AlignedSize, List<Entry> Files, bool Buffers);

    /// <summary>Writes <c>metadata.store</c> for the bundles in <paramref name="folder"/>, into it. Returns its path.</summary>
    public static string Write(string folder)
    {
        var bundles = Directory.GetFiles(folder, "*.bundle")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Read).ToList();
        string path = Path.Combine(folder, FileName);
        File.WriteAllBytes(path, Build(bundles));
        return path;
    }

    private static BundleInfo Read(string bundlePath)
    {
        using var fs = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[0x20];
        fs.ReadExactly(header);
        if (Encoding.ASCII.GetString(header, 0, 8) != "POTATO70") throw new InvalidDataException($"{Path.GetFileName(bundlePath)} isn't a bundle.");
        long aligned = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x08));
        int tocSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x10));
        var bundle = Bundle.Open(bundlePath);
        var files = bundle.Entries.Cast<BundleEntry>()
            .Select(e => new Entry(e.Path, e.Offset, e.Size, e.PackedSize, (int)e.Compression))
            .ToList();
        bool buffers = files.Count > 0 && files.All(f => f.Path.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase));
        return new BundleInfo(Path.GetFileName(bundlePath), 0x20 + tocSize, aligned, files, buffers);
    }

    private static bool IsCensored(string path)
    {
        int dot = path.IndexOf('.');
        return dot > 0 && path.AsSpan(0, dot).EndsWith("_censored", StringComparison.Ordinal);
    }

    private static byte[] Build(List<BundleInfo> bundles)
    {
        var strings = new MemoryStream();
        int String(string text)
        {
            int at = (int)strings.Length;
            var bytes = Encoding.ASCII.GetBytes(text);
            strings.Write(bytes);
            strings.WriteByte(0);
            return at;
        }
        String("");

        // the files, numbered from 1 in bundle order and, in a bundle, in the order they lie in it
        var files = new List<(Entry Entry, int PathOffset, int Bundle)>();
        var bundleRows = new List<(long DataSize, long DataStart, int Name, int First, int Count)>();
        for (int b = 0; b < bundles.Count; b++)
        {
            var bundle = bundles[b];
            int name = String(bundle.Name);
            int first = files.Count + 1;
            foreach (var entry in bundle.Files.Where(f => !IsCensored(f.Path)).OrderBy(f => f.Offset))
                files.Add((entry, String(entry.Path), b + 1));
            bundleRows.Add((bundle.Buffers ? 0 : bundle.AlignedSize - bundle.DataStart, bundle.DataStart, name, first, files.Count + 1 - first));
        }

        // the folders: a tree, each folder's folders in the order the files first reach them, listed depth first from the
        // root (which has no name). Buffers don't count: they're found by the file they belong to.
        var root = new Folder("", null);
        var fileFolder = new List<(int File, Folder Folder, int Name)>();
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i].Entry.Path;
            if (path.Contains(".buffer", StringComparison.Ordinal)) continue;
            var folder = root;
            int start = 0;
            for (int at = 0; at < path.Length; at++)
            {
                if (path[at] is not ('\\' or '/')) continue;
                string name = path[start..at];
                var child = folder.Children.Find(c => c.Name == name);
                if (child is null) folder.Children.Add(child = new Folder(name, folder));
                folder = child;
                start = at + 1;
            }
            fileFolder.Add((i + 1, folder, files[i].PathOffset + start));
        }
        var dirs = new List<Folder>();
        void List(Folder folder)
        {
            folder.Index = dirs.Count;
            dirs.Add(folder);
            foreach (var child in folder.Children) List(child);
        }
        List(root);
        int rootName = String("");
        var dirNames = dirs.Select((d, i) => i == 0 ? rootName : String(d.Name)).ToList();

        // the buffers each file has, "<file>.<n>.buffer" being buffer n of <file>: the files' in order, each one's by number
        var owned = new Dictionary<string, List<(int Number, int File)>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i].Entry.Path;
            if (!path.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = path[..^".buffer".Length];
            int dot = stem.LastIndexOf('.');
            if (dot < 0 || !int.TryParse(stem.AsSpan(dot + 1), out int number)) continue;
            string owner = stem[..dot];
            if (!owned.TryGetValue(owner, out var list)) owned[owner] = list = [];
            list.Add((number, i + 1));
        }
        var bufferTable = new List<int>();
        var fileBuffers = new Dictionary<int, (int First, int Count)>();
        for (int i = 0; i < files.Count; i++)
        {
            if (!owned.TryGetValue(files[i].Entry.Path, out var list)) continue;
            fileBuffers[i + 1] = (bufferTable.Count, list.Count);
            bufferTable.AddRange(list.OrderBy(b => b.Number).Select(b => b.File));
        }

        // each path's hash, for the files that aren't buffers, sorted; a second file with the same path is left out
        var hashes = new SortedDictionary<ulong, int>();
        foreach (var (file, _, _) in fileFolder) hashes.TryAdd(Fnv1a64(files[file - 1].Entry.Path), file);

        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write(Magic);
        w.Write(Version);
        w.Write(files.Count == 0 ? 0 : files.Max(f => f.Entry.PackedSize));
        w.Write(files.Count == 0 ? 0 : files.Max(f => f.Entry.Size));

        WriteCount(w, (int)strings.Length);
        w.Write(strings.GetBuffer(), 0, (int)strings.Length);

        WriteCount(w, files.Count + 1);
        w.Write(new byte[32]);
        for (int i = 0; i < files.Count; i++)
        {
            var (entry, pathOffset, _) = files[i];
            var (first, count) = fileBuffers.GetValueOrDefault(i + 1);
            w.Write(pathOffset);
            w.Write(0);
            w.Write(entry.PackedSize);
            w.Write(entry.Size);
            w.Write(i + 1);
            w.Write(first);
            w.Write(count);
            w.Write(entry.Compression);
        }

        WriteCount(w, files.Count + 1);
        w.Write(new byte[24]);
        for (int i = 0; i < files.Count; i++)
        {
            w.Write(files[i].Entry.Offset);
            w.Write(files[i].Entry.PackedSize);
            w.Write(0);
            w.Write(i + 1);
            w.Write(files[i].Bundle);
        }

        WriteCount(w, bundleRows.Count + 1);
        w.Write(new byte[24]);
        foreach (var (dataSize, dataStart, name, first, count) in bundleRows)
        {
            w.Write(dataSize);
            w.Write((int)dataStart);
            w.Write(name);
            w.Write(first);
            w.Write(count);
        }

        WriteCount(w, bufferTable.Count);
        foreach (int file in bufferTable) w.Write(file);

        WriteCount(w, dirs.Count);
        for (int i = 0; i < dirs.Count; i++) { w.Write(dirNames[i]); w.Write(dirs[i].Parent?.Index ?? 0); }

        WriteCount(w, fileFolder.Count);
        foreach (var (file, folder, name) in fileFolder) { w.Write(file); w.Write(folder.Index); w.Write(name); }

        // each entry is the hash, the file and four bytes of padding wcc_lite never sets: the first entry it adds keeps
        // the zero of new memory, the others whatever its stack held, which comes out 0 when the bundles have buffers
        // and -1 when they don't. The game doesn't read them; they're copied so a build matches REDkit's byte for byte.
        WriteCount(w, hashes.Count);
        int firstAdded = fileFolder.Count > 0 ? fileFolder[0].File : 0;
        foreach (var (hash, file) in hashes)
        {
            w.Write(hash);
            w.Write(file);
            w.Write(file == firstAdded || bufferTable.Count > 0 ? 0 : -1);
        }
        w.Flush();
        return output.ToArray();
    }

    /// <summary>A count as CR2W packs one: six bits in the first byte, seven in each after, 0x40/0x80 for "more"; zero is written 0x80.</summary>
    private static void WriteCount(BinaryWriter w, int value)
    {
        if (value == 0) { w.Write((byte)0x80); return; }
        byte first = (byte)(value & 0x3F);
        value >>= 6;
        if (value != 0) first |= 0x40;
        w.Write(first);
        while (value != 0)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            w.Write(b);
        }
    }

    /// <summary>The 64-bit FNV-1a hash of a path as the game looks it up: each character one byte, lowercase, with backslashes.</summary>
    public static ulong Fnv1a64(string text)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (char ch in text)
        {
            byte c = (byte)ch;
            if (c is >= (byte)'A' and <= (byte)'Z') c += 32;
            else if (c == '/') c = (byte)'\\';
            hash ^= c;
            hash *= 0x100000001B3;
        }
        return hash;
    }
}
