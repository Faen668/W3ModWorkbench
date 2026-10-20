namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// LZ4 high-compression encoder, to the raw block format <see cref="Lz4.Decode"/> reads, as the engine's tools pack a
/// bundle's files. It's the LZ4 HC wcc_lite links in, an old one (r1xx), at its default of 256 tries a position: a hash
/// chain over the last 64 KB, a shortcut for runs that repeat every 1 to 4 bytes, and the lazy parse that looks for a
/// longer match a step or two ahead before settling. Newer LZ4 HC parses differently, so its blocks read the same but
/// aren't the same bytes; this one gives wcc_lite's, byte for byte, which is what lets a build be checked against REDkit's.
/// </summary>
public static class Lz4Hc
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int MfLimit = 12;
    private const int MaxDistance = 65535;
    private const int HashLog = 15;
    private const int MlBits = 4, MlMask = 15, RunMask = 15;
    private const int OptimalMl = MlMask - 1 + MinMatch;

    /// <summary>The most a block of <paramref name="size"/> bytes can grow to.</summary>
    public static int Bound(int size) => size + size / 255 + 16;

    /// <summary>Packs <paramref name="source"/> into a new block.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> source, int maxAttempts = 256)
    {
        var output = new byte[Bound(source.Length)];
        int length = new Encoder(source, output, maxAttempts).Run();
        Array.Resize(ref output, length);
        return output;
    }

    private ref struct Encoder
    {
        private readonly ReadOnlySpan<byte> _src;
        private readonly Span<byte> _dst;
        private readonly int _maxAttempts;
        private readonly int[] _hashTable;
        private readonly ushort[] _chainTable;
        private int _nextToUpdate;
        private int _op, _anchor;

        public Encoder(ReadOnlySpan<byte> src, Span<byte> dst, int maxAttempts)
        {
            _src = src;
            _dst = dst;
            _maxAttempts = maxAttempts;
            _hashTable = new int[1 << HashLog];
            _chainTable = new ushort[65536];
            _chainTable.AsSpan().Fill(0xFFFF);
            _nextToUpdate = 1;
            _op = 0;
            _anchor = 0;
        }

        private readonly uint Read32(int at) => (uint)(_src[at] | _src[at + 1] << 8 | _src[at + 2] << 16 | _src[at + 3] << 24);

        private readonly int Hash(int at) => (int)((Read32(at) * 2654435761u) >> (MinMatch * 8 - HashLog));

        /// <summary>How many bytes match from <paramref name="a"/> and <paramref name="b"/> on, stopping at <paramref name="limit"/> (for <paramref name="a"/>).</summary>
        private readonly int Count(int a, int b, int limit)
        {
            int start = a;
            while (a < limit && _src[a] == _src[b]) { a++; b++; }
            return a - start;
        }

        /// <summary>Adds every position up to <paramref name="ip"/> to the hash chains.</summary>
        private void Insert(int ip)
        {
            for (int idx = _nextToUpdate; idx < ip; idx++)
            {
                int h = Hash(idx);
                int delta = idx - _hashTable[h];
                if (delta > MaxDistance) delta = MaxDistance;
                _chainTable[(ushort)idx] = (ushort)delta;
                _hashTable[h] = idx;
            }
            if (ip > _nextToUpdate) _nextToUpdate = ip;
        }

        private int FindBestMatch(int ip, int limit, out int match)
        {
            int attempts = _maxAttempts, ml = 0, repeat = 0, delta = 0;
            match = 0;
            Insert(ip);
            // a hash slot never set points at the start, which is a match like any other while it's in reach
            int m = _hashTable[Hash(ip)];
            // a run repeating every 1 to 4 bytes: take it, and fill its chain in one go
            if (ip - m <= 4)
            {
                if (Read32(m) == Read32(ip))
                {
                    delta = ip - m;
                    ml = repeat = Count(ip + MinMatch, m + MinMatch, limit) + MinMatch;
                    match = m;
                }
                m -= _chainTable[(ushort)m];
            }
            while (ip - m <= MaxDistance && attempts > 0)
            {
                attempts--;
                if (_src[m + ml] == _src[ip + ml] && Read32(m) == Read32(ip))
                {
                    int mlt = Count(ip + MinMatch, m + MinMatch, limit) + MinMatch;
                    if (mlt > ml) { ml = mlt; match = m; }
                }
                m -= _chainTable[(ushort)m];
            }
            if (repeat > 0)
            {
                int p = ip, end = ip + repeat - (MinMatch - 1);
                while (p < end - delta) _chainTable[(ushort)p++] = (ushort)delta;
                do
                {
                    _chainTable[(ushort)p] = (ushort)delta;
                    _hashTable[Hash(p)] = p;
                    p++;
                } while (p < end);
                _nextToUpdate = end;
            }
            return ml;
        }

        /// <summary>
        /// A match at <paramref name="ip"/> longer than <paramref name="longest"/>, grown back as far as
        /// <paramref name="startLimit"/>: where it starts and what it copies, or <paramref name="longest"/> if there's none.
        /// </summary>
        private int GetWiderMatch(int ip, int startLimit, int matchLimit, int longest, ref int match, ref int start)
        {
            int attempts = _maxAttempts;
            int delta = ip - startLimit;
            Insert(ip);
            int m = _hashTable[Hash(ip)];
            while (ip - m <= MaxDistance && attempts > 0)
            {
                attempts--;
                if (_src[startLimit + longest] == _src[m - delta + longest] && Read32(m) == Read32(ip))
                {
                    int end = ip + MinMatch + Count(ip + MinMatch, m + MinMatch, matchLimit);
                    int s = ip, r = m;
                    while (s > startLimit && r > 0 && _src[s - 1] == _src[r - 1]) { s--; r--; }
                    if (end - s > longest) { longest = end - s; match = r; start = s; }
                }
                m -= _chainTable[(ushort)m];
            }
            return longest;
        }

        private void Length(int length)
        {
            for (; length > 254; length -= 255) _dst[_op++] = 255;
            _dst[_op++] = (byte)length;
        }

        private void Sequence(ref int ip, int matchLength, int match)
        {
            int literals = ip - _anchor;
            int token = _op++;
            if (literals >= RunMask)
            {
                _dst[token] = RunMask << MlBits;
                Length(literals - RunMask);
            }
            else _dst[token] = (byte)(literals << MlBits);
            _src.Slice(_anchor, literals).CopyTo(_dst[_op..]);
            _op += literals;
            int offset = ip - match;
            _dst[_op++] = (byte)offset;
            _dst[_op++] = (byte)(offset >> 8);
            int length = matchLength - MinMatch;
            if (length >= MlMask)
            {
                _dst[token] += MlMask;
                length -= MlMask;
                for (; length > 509; length -= 510) { _dst[_op++] = 255; _dst[_op++] = 255; }
                if (length > 254) { length -= 255; _dst[_op++] = 255; }
                _dst[_op++] = (byte)length;
            }
            else _dst[token] += (byte)length;
            ip += matchLength;
            _anchor = ip;
        }

        public int Run()
        {
            int iend = _src.Length, mflimit = iend - MfLimit, matchLimit = iend - LastLiterals;
            int ip = 1;
            int ml, ml0, ml2, ml3, match = 0, match0, match2 = 0, match3 = 0, start0, start2 = 0, start3 = 0;

            while (ip < mflimit)
            {
                ml = FindBestMatch(ip, matchLimit, out match);
                if (ml == 0) { ip++; continue; }

                // kept, in case the lazy steps skip too far
                start0 = ip; match0 = match; ml0 = ml;

            Search2:
                ml2 = ip + ml < mflimit ? GetWiderMatch(ip + ml - 2, ip + 1, matchLimit, ml, ref match2, ref start2) : ml;
                if (ml2 == ml)
                {
                    Sequence(ref ip, ml, match);
                    continue;
                }
                if (start0 < ip && start2 < ip + ml0)
                {
                    ip = start0; match = match0; ml = ml0;
                }
                if (start2 - ip < 3)
                {
                    // the first match is too small: the wider one replaces it
                    ml = ml2; ip = start2; match = match2;
                    goto Search2;
                }

            Search3:
                if (start2 - ip < OptimalMl)
                {
                    int newMl = Math.Min(ml, OptimalMl);
                    if (ip + newMl > start2 + ml2 - MinMatch) newMl = start2 - ip + ml2 - MinMatch;
                    int correction = newMl - (start2 - ip);
                    if (correction > 0) { start2 += correction; match2 += correction; ml2 -= correction; }
                }
                ml3 = start2 + ml2 < mflimit ? GetWiderMatch(start2 + ml2 - 3, start2, matchLimit, ml2, ref match3, ref start3) : ml2;
                if (ml3 == ml2)
                {
                    // no better match: two sequences to write
                    if (start2 < ip + ml) ml = start2 - ip;
                    Sequence(ref ip, ml, match);
                    ip = start2;
                    Sequence(ref ip, ml2, match2);
                    continue;
                }
                if (start3 < ip + ml + 3)
                {
                    // no room for the second match: it goes
                    if (start3 >= ip + ml)
                    {
                        if (start2 < ip + ml)
                        {
                            int correction = ip + ml - start2;
                            start2 += correction; match2 += correction; ml2 -= correction;
                            if (ml2 < MinMatch) { start2 = start3; match2 = match3; ml2 = ml3; }
                        }
                        Sequence(ref ip, ml, match);
                        ip = start3; match = match3; ml = ml3;
                        start0 = start2; match0 = match2; ml0 = ml2;
                        goto Search2;
                    }
                    start2 = start3; match2 = match3; ml2 = ml3;
                    goto Search3;
                }

                // three matches one after another: the first can be written
                if (start2 < ip + ml)
                {
                    if (start2 - ip < MlMask)
                    {
                        if (ml > OptimalMl) ml = OptimalMl;
                        if (ip + ml > start2 + ml2 - MinMatch) ml = start2 - ip + ml2 - MinMatch;
                        int correction = ml - (start2 - ip);
                        if (correction > 0) { start2 += correction; match2 += correction; ml2 -= correction; }
                    }
                    else ml = start2 - ip;
                }
                Sequence(ref ip, ml, match);
                ip = start2; match = match2; ml = ml2;
                start2 = start3; match2 = match3; ml2 = ml3;
                goto Search3;
            }

            // the last literals
            int lastRun = iend - _anchor;
            if (lastRun >= RunMask)
            {
                _dst[_op++] = RunMask << MlBits;
                Length(lastRun - RunMask);
            }
            else _dst[_op++] = (byte)(lastRun << MlBits);
            _src[_anchor..].CopyTo(_dst[_op..]);
            _op += lastRun;
            return _op;
        }
    }
}
