using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace W3ModWorkbench.Core;

/// <summary>
/// Reads the tables of an SQLite database file, for REDkit's strings database, without a native SQLite library.
/// Read-only and table scans only: it walks each table's b-tree from the root page sqlite_master names, and
/// decodes the records it finds, overflow pages and all. The file is opened shared, so REDkit can have it open.
/// <para>
/// A database in WAL mode keeps recent changes in a "-wal" file beside it until they're checkpointed; those
/// aren't read.
/// </para>
/// </summary>
public sealed class SqliteFile : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly long _length;
    private readonly int _pageSize;
    private readonly int _usable;
    private readonly Encoding _encoding;
    private readonly Dictionary<string, int> _roots = new(StringComparer.OrdinalIgnoreCase);

    private SqliteFile(SafeFileHandle file)
    {
        _file = file;
        _length = RandomAccess.GetLength(file);
        var header = new byte[100];
        if (_length < 512 || RandomAccess.Read(file, header, 0) < 100
            || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("This isn't an SQLite database.");
        int size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
        _pageSize = size == 1 ? 65536 : size;
        if (_pageSize < 512 || (_pageSize & (_pageSize - 1)) != 0)
            throw new InvalidDataException("The database's page size makes no sense.");
        _usable = _pageSize - header[20];
        _encoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56)) switch
        {
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };

        // sqlite_master: type, name, tbl_name, rootpage, sql
        Scan(1, row =>
        {
            if (row.Text(0) == "table" && row.Text(1) is { } name) _roots[name] = (int)row.Integer(3);
        });
    }

    public static SqliteFile Open(string path) =>
        new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    public bool HasTable(string name) => _roots.ContainsKey(name);

    /// <summary>Calls <paramref name="visit"/> with every row of a table. The row is only good until it returns.</summary>
    public void Scan(string table, Action<SqliteRow> visit)
    {
        if (!_roots.TryGetValue(table, out int root))
            throw new InvalidDataException($"The database has no {table} table.");
        Scan(root, visit);
    }

    private void Scan(int root, Action<SqliteRow> visit)
    {
        var row = new SqliteRow(_encoding);
        var pending = new Stack<int>();
        pending.Push(root);
        int visited = 0;
        while (pending.Count > 0)
        {
            int number = pending.Pop();
            if (++visited > _length / _pageSize + 1) throw new InvalidDataException("The database's pages loop.");
            byte[] page = ReadPage(number, ref _treePage);
            int at = number == 1 ? 100 : 0;
            byte type = page[at];
            int cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(at + 3));
            if (type == 5)
            {
                // interior: children in order are each cell's left pointer, then the right-most one; pushed backwards
                pending.Push((int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + 8)));
                for (int i = cells - 1; i >= 0; i--)
                {
                    int cell = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(at + 12 + i * 2));
                    pending.Push((int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(cell)));
                }
            }
            else if (type == 13)
            {
                for (int i = 0; i < cells; i++)
                {
                    int cell = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(at + 8 + i * 2));
                    long payload = ReadVarint(page, ref cell);
                    long rowId = ReadVarint(page, ref cell);
                    row.Load(rowId, Payload(page, cell, (int)payload, row.Buffer));
                    visit(row);
                }
            }
            else throw new InvalidDataException($"Page {number} isn't part of a table (type {type}).");
        }
    }

    /// <summary>A cell's payload, with what spilled onto overflow pages joined back on.</summary>
    private byte[] Payload(byte[] page, int start, int size, byte[] buffer)
    {
        if (buffer.Length < size) buffer = new byte[Math.Max(size, buffer.Length * 2)];
        int maxLocal = _usable - 35;
        int local = size;
        if (size > maxLocal)
        {
            int minLocal = (_usable - 12) * 32 / 255 - 23;
            local = minLocal + (size - minLocal) % (_usable - 4);
            if (local > maxLocal) local = minLocal;
        }
        page.AsSpan(start, local).CopyTo(buffer);
        if (local < size)
        {
            int next = (int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(start + local));
            int done = local;
            while (done < size)
            {
                if (next == 0) throw new InvalidDataException("A record's overflow pages end early.");
                byte[] overflow = ReadPage(next, ref _overflowPage);
                int take = Math.Min(size - done, _usable - 4);
                overflow.AsSpan(4, take).CopyTo(buffer.AsSpan(done));
                done += take;
                next = (int)BinaryPrimitives.ReadUInt32BigEndian(overflow);
            }
        }
        return buffer;
    }

    private byte[] _treePage = [];
    private byte[] _overflowPage = [];

    /// <summary>Reads a page into a buffer kept for it, so tree pages and overflow pages don't overwrite each other.</summary>
    private byte[] ReadPage(int number, ref byte[] buffer)
    {
        long offset = (long)(number - 1) * _pageSize;
        if (number < 1 || offset + _pageSize > _length) throw new InvalidDataException($"Page {number} is past the end of the database.");
        if (buffer.Length != _pageSize) buffer = new byte[_pageSize];
        RandomAccess.Read(_file, buffer, offset);
        return buffer;
    }

    internal static long ReadVarint(ReadOnlySpan<byte> data, ref int at)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            byte b = data[at++];
            value = value << 7 | (long)(b & 0x7f);
            if (b < 0x80) return value;
        }
        return value << 8 | data[at++];
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>One row of a table, its columns decoded when they're asked for.</summary>
public sealed class SqliteRow
{
    private readonly Encoding _encoding;
    private readonly List<(long Type, int Offset)> _columns = [];
    private byte[] _data = new byte[4096];

    internal SqliteRow(Encoding encoding) => _encoding = encoding;

    internal byte[] Buffer => _data;

    /// <summary>The rowid, which is also the value of an INTEGER PRIMARY KEY column (stored as null in the record).</summary>
    public long RowId { get; private set; }

    public int Count => _columns.Count;

    internal void Load(long rowId, byte[] data)
    {
        RowId = rowId;
        _data = data;
        _columns.Clear();
        int at = 0;
        int headerSize = (int)SqliteFile.ReadVarint(data, ref at);
        int body = headerSize;
        while (at < headerSize)
        {
            long type = SqliteFile.ReadVarint(data, ref at);
            _columns.Add((type, body));
            body += Size(type);
        }
    }

    private static int Size(long type) => type switch
    {
        0 or 8 or 9 or 10 or 11 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 6,
        6 or 7 => 8,
        _ => (int)((type - 12) / 2),
    };

    public bool IsNull(int column) => column >= _columns.Count || _columns[column].Type == 0;

    /// <summary>An integer column's value; 0 for null or a column the row doesn't have.</summary>
    public long Integer(int column)
    {
        if (column >= _columns.Count) return 0;
        var (type, offset) = _columns[column];
        if (type is 8 or 0) return 0;
        if (type == 9) return 1;
        if (type is < 1 or > 6) return 0;
        int size = Size(type);
        long value = (sbyte)_data[offset];
        for (int i = 1; i < size; i++) value = value << 8 | _data[offset + i];
        return value;
    }

    /// <summary>A text column's value, or null when it's null or not text.</summary>
    public string? Text(int column)
    {
        if (column >= _columns.Count) return null;
        var (type, offset) = _columns[column];
        if (type < 13 || type % 2 == 0) return null;
        return _encoding.GetString(_data, offset, Size(type));
    }
}
