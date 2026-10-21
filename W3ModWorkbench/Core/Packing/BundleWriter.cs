using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>One file as a bundle written by <see cref="BundleWriter"/> holds it.</summary>
public sealed record PackedFile(string Path, long Offset, int Size, int PackedSize, uint Crc, Compression Compression);

/// <summary>A bundle <see cref="BundleWriter"/> wrote: its file, and its files in the order its table lists them.</summary>
public sealed record PackedBundle(string FilePath, IReadOnlyList<PackedFile> Files);

/// <summary>
/// Packs a folder into bundles as REDkit's <c>wcc_lite pack</c> does, byte for byte: the game paths are the files' paths
/// in the folder, as they're written there (the game looks paths up lowercased). Mesh and animation buffers (<c>.buffer</c>) go in <c>buffers0.bundle</c>, everything else in
/// <c>blob0.bundle</c>; a bundle that would pass 0xF6E00000 bytes of files goes on in <c>blob1.bundle</c> and so on.
/// <c>.db</c> and <c>.cache</c> files and empty files aren't packed.
/// <para>
/// A bundle is a 32-byte header ("POTATO70", the bundle's size rounded up to 16, the table's size, 5, where the data
/// starts), a table of 0x130 bytes a file (its path, where it is, its size packed and unpacked, the CRC-32 of its bytes
/// and how it's packed), then the files, each starting on 16 bytes. A file is packed with LZ4 HC, unless that doesn't
/// make it smaller or it's over 32 MB, when it's kept as it is. The table lists the files in the folder's order (a
/// folder's files, then its folders, each by name); the data is in the order wcc_lite's sort leaves them in, which
/// <see cref="DataOrder"/> repeats.
/// </para>
/// </summary>
public static class BundleWriter
{
    private const string Magic = "POTATO70";
    private const int HeaderSize = 0x20;
    private const int EntrySize = 0x130;
    private const int NameSize = 0x100;

    /// <summary>The most a bundle takes, in its files' own sizes, before the next one starts.</summary>
    public const long SplitSize = 0xF6E00000;

    /// <summary>Files over this are stored as they are, as wcc_lite does, so the game reads them without unpacking.</summary>
    public const int MaxCompressedFile = 0x2000000;

    /// <summary>A file the folder holds that a bundle gets, under its game path.</summary>
    public sealed record Source(string FilePath, string GamePath, long Size);

    /// <summary>
    /// The files in <paramref name="folder"/> a pack would take, in the order wcc_lite lists them (see <see cref="InFolderOrder"/>),
    /// with their paths in the game's form (backslashes).
    /// </summary>
    public static List<Source> Files(string folder)
    {
        string root = Path.GetFullPath(folder);
        var list = new List<Source>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            if (Packs(file.Name, file.Length)) list.Add(new Source(file.FullName, GamePath(Path.GetRelativePath(root, file.FullName)), file.Length));
        return InFolderOrder(list);
    }

    /// <summary>Whether wcc_lite's pack takes a file: not an empty one, nor a <c>.db</c> or <c>.cache</c>.</summary>
    public static bool Packs(string name, long size) => size > 0
        && !name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files in the order wcc_lite walks a folder: each folder's files by name, then its folders by name, neither minding case.
    /// </summary>
    public static List<Source> InFolderOrder(IEnumerable<Source> files)
    {
        var list = files.ToList();
        list.Sort((a, b) => ComparePaths(a.GamePath, b.GamePath));
        return list;
    }

    private static int ComparePaths(string a, string b)
    {
        var x = a.Split('\\');
        var y = b.Split('\\');
        for (int i = 0; ; i++)
        {
            bool xFile = i == x.Length - 1, yFile = i == y.Length - 1;
            // in the same folder: a file comes before a folder, else by name
            if (xFile != yFile) return xFile ? -1 : 1;
            int byName = StringComparer.OrdinalIgnoreCase.Compare(x[i], y[i]);
            if (byName != 0 || xFile) return byName;
        }
    }

    /// <summary>A path as a bundle lists it: as the folder has it, with backslashes. The game looks paths up lowercased.</summary>
    public static string GamePath(string path) => path.Replace('/', '\\');

    /// <summary>
    /// Packs every file in <paramref name="folder"/> into bundles in <paramref name="outFolder"/>, the blobs first, then
    /// the buffers. Returns the bundles written.
    /// </summary>
    public static List<PackedBundle> Pack(string folder, string outFolder, IProgress<string>? progress = null, CancellationToken cancel = default) =>
        Pack(Files(folder), outFolder, progress, cancel);

    /// <summary>Packs these files into bundles in <paramref name="outFolder"/>, grouped and split as wcc_lite does.</summary>
    public static List<PackedBundle> Pack(IReadOnlyList<Source> files, string outFolder, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var groups = new[] { (Name: "blob", Bundles: new List<List<Source>>()), (Name: "buffers", Bundles: new List<List<Source>>()) };
        var sizes = new Dictionary<List<Source>, long>();
        foreach (var file in files)
        {
            var group = groups[file.GamePath.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase) ? 1 : 0].Bundles;
            if (group.Count == 0 || sizes[group[^1]] + file.Size >= SplitSize)
            {
                group.Add([]);
                sizes[group[^1]] = 0;
            }
            group[^1].Add(file);
            sizes[group[^1]] += file.Size;
        }

        Directory.CreateDirectory(outFolder);
        var written = new List<PackedBundle>();
        foreach (var (name, bundles) in groups)
            for (int i = 0; i < bundles.Count; i++)
            {
                string path = Path.Combine(outFolder, $"{name}{i}.bundle");
                progress?.Report(L.T("BundleWriter.Packing", Path.GetFileName(path)));
                written.Add(Write(path, bundles[i], cancel));
            }
        return written;
    }

    /// <summary>Writes one bundle of these files, in this (table) order.</summary>
    public static PackedBundle Write(string path, IReadOnlyList<Source> files, CancellationToken cancel = default)
    {
        int count = files.Count;
        long dataStart = HeaderSize + (long)count * EntrySize;
        var order = DataOrder(count);
        var packed = new PackedFile[count];
        var table = new byte[count * EntrySize];

        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            long offset = Align(dataStart);
            output.Position = offset;
            // packed a batch at a time, in the order they're written, so a big bundle never sits in memory
            const int Batch = 64;
            for (int from = 0; from < count; from += Batch)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(Batch, count - from);
                var blocks = new (byte[] Data, int Size, uint Crc, Compression Compression)[n];
                Parallel.For(0, n, new ParallelOptions { CancellationToken = cancel }, k => blocks[k] = PackFile(files[order[from + k]].FilePath));
                for (int k = 0; k < n; k++)
                {
                    int index = order[from + k];
                    var (data, size, crc, compression) = blocks[k];
                    output.Position = offset;
                    output.Write(data);
                    packed[index] = new PackedFile(files[index].GamePath, offset, size, data.Length, crc, compression);
                    offset = Align(offset + data.Length);
                }
            }
            long end = output.Position;

            for (int i = 0; i < count; i++)
            {
                var e = table.AsSpan(i * EntrySize, EntrySize);
                var name = ArchivePaths.Bytes(packed[i].Path);
                name.AsSpan(0, Math.Min(name.Length, NameSize - 1)).CopyTo(e);
                BinaryPrimitives.WriteInt64LittleEndian(e[0x110..], packed[i].Offset);
                BinaryPrimitives.WriteInt32LittleEndian(e[0x118..], packed[i].Size);
                BinaryPrimitives.WriteInt32LittleEndian(e[0x11C..], packed[i].PackedSize);
                BinaryPrimitives.WriteUInt32LittleEndian(e[0x120..], packed[i].Crc);
                BinaryPrimitives.WriteUInt32LittleEndian(e[0x124..], (uint)packed[i].Compression);
            }

            var header = new byte[HeaderSize];
            Encoding.ASCII.GetBytes(Magic).CopyTo(header, 0);
            // the size rounded up, kept to 32 bits as wcc_lite does
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), (uint)Align(end));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x10), table.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x14), 5);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x16), (uint)dataStart);
            output.Position = 0;
            output.Write(header);
            output.Write(table);
            output.SetLength(end);
        }
        return new PackedBundle(path, packed);
    }

    /// <summary>A file's bytes as the bundle keeps them: LZ4 HC when that's smaller, otherwise as they are.</summary>
    private static (byte[] Data, int Size, uint Crc, Compression Compression) PackFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        uint crc = Checksums.Crc32(data);
        if (data.Length <= MaxCompressedFile)
        {
            var lz = Lz4Hc.Encode(data);
            if (lz.Length < data.Length) return (lz, data.Length, crc, Compression.Lz4Hc);
        }
        return (data, data.Length, crc, Compression.None);
    }

    private static long Align(long at) => (at + 15) & ~15L;

    /// <summary>
    /// The order a bundle's files are written in, by their place in its table. wcc_lite sorts its list by whether a file
    /// is a buffer before writing, which every file of a bundle shares, and its sort (a quicksort that hands runs of 16
    /// or fewer to an insertion sort) leaves equal items reordered: each run longer than 16 comes out reversed, then
    /// each half of it the same way again.
    /// </summary>
    public static int[] DataOrder(int count)
    {
        var order = Enumerable.Range(0, count).ToArray();
        void Sort(int lo, int hi)
        {
            if (hi - lo <= 16) return;
            Array.Reverse(order, lo, hi - lo);
            int mid = lo + (hi - lo) / 2;
            Sort(mid, hi);
            Sort(lo, mid);
        }
        Sort(0, count);
        return order;
    }
}
