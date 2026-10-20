using System.IO;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// A zlib stream writer that gives the same bytes as the zlib (1.2.7) the engine's tools link in, at level 9: deflate's
/// lazy matcher (hash chains over a 32 KB window, up to 4096 tries, a match given up for one a byte later when that's
/// longer) and its Huffman blocks, each block stored, fixed or dynamic, whichever comes out smallest. .NET's own zlib is a
/// different one (zlib-ng), whose streams read the same but aren't the same bytes; this one lets a texture cache be
/// checked against REDkit's, byte for byte.
/// </summary>
public static class Zlib
{
    /// <summary>A zlib stream (header, deflate data, Adler-32) of <paramref name="source"/> at level 9.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> source) => new Deflater(source).Run();

    private const int MinMatch = 3, MaxMatch = 258, MinLookahead = MaxMatch + MinMatch + 1;
    private const int WBits = 15, WSize = 1 << WBits, WMask = WSize - 1, WindowSize = 2 * WSize, MaxDist = WSize - MinLookahead;
    private const int HashBits = 15, HashSize = 1 << HashBits, HashMask = HashSize - 1, HashShift = (HashBits + MinMatch - 1) / MinMatch;
    private const int LitBufSize = 1 << 14;
    private const int GoodMatch = 32, MaxLazyMatch = 258, NiceMatch = 258, MaxChain = 4096, TooFar = 4096;
    private const int WinInit = MaxMatch;

    private const int Literals = 256, LengthCodes = 29, LCodes = Literals + 1 + LengthCodes, DCodes = 30, BlCodes = 19;
    private const int HeapSize = 2 * LCodes + 1, MaxBits = 15, MaxBlBits = 7, EndBlock = 256;
    private const int Rep3To6 = 16, RepZ3To10 = 17, RepZ11To138 = 18;

    private static readonly int[] ExtraLBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] ExtraDBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly int[] ExtraBlBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7];
    private static readonly int[] BlOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly byte[] LengthCode = new byte[MaxMatch - MinMatch + 1];
    private static readonly byte[] DistCode = new byte[512];
    private static readonly int[] BaseLength = new int[LengthCodes];
    private static readonly int[] BaseDist = new int[DCodes];
    private static readonly ushort[] StaticLCode = new ushort[LCodes + 2], StaticLLen = new ushort[LCodes + 2];
    private static readonly ushort[] StaticDCode = new ushort[DCodes], StaticDLen = new ushort[DCodes];

    static Zlib()
    {
        int length = 0;
        for (int code = 0; code < LengthCodes - 1; code++)
        {
            BaseLength[code] = length;
            for (int n = 0; n < 1 << ExtraLBits[code]; n++) LengthCode[length++] = (byte)code;
        }
        LengthCode[length - 1] = LengthCodes - 1;
        int dist = 0;
        for (int code = 0; code < 16; code++)
        {
            BaseDist[code] = dist;
            for (int n = 0; n < 1 << ExtraDBits[code]; n++) DistCode[dist++] = (byte)code;
        }
        dist >>= 7;
        for (int code = 16; code < DCodes; code++)
        {
            BaseDist[code] = dist << 7;
            for (int n = 0; n < 1 << (ExtraDBits[code] - 7); n++) DistCode[256 + dist++] = (byte)code;
        }
        var blCount = new int[MaxBits + 1];
        int i = 0;
        while (i <= 143) { StaticLLen[i++] = 8; blCount[8]++; }
        while (i <= 255) { StaticLLen[i++] = 9; blCount[9]++; }
        while (i <= 279) { StaticLLen[i++] = 7; blCount[7]++; }
        while (i <= 287) { StaticLLen[i++] = 8; blCount[8]++; }
        GenCodes(StaticLCode, StaticLLen, LCodes + 1, blCount);
        for (i = 0; i < DCodes; i++) { StaticDLen[i] = 5; StaticDCode[i] = (ushort)BiReverse(i, 5); }
    }

    private static int DCode(int dist) => dist < 256 ? DistCode[dist] : DistCode[256 + (dist >> 7)];

    private static int BiReverse(int code, int len)
    {
        int res = 0;
        do { res |= code & 1; code >>= 1; res <<= 1; } while (--len > 0);
        return res >> 1;
    }

    private static void GenCodes(ushort[] code, ushort[] len, int maxCode, int[] blCount)
    {
        var next = new int[MaxBits + 1];
        int c = 0;
        for (int bits = 1; bits <= MaxBits; bits++) next[bits] = c = (c + blCount[bits - 1]) << 1;
        for (int n = 0; n <= maxCode; n++)
        {
            int l = len[n];
            if (l == 0) continue;
            code[n] = (ushort)BiReverse(next[l]++, l);
        }
    }

    /// <summary>A Huffman tree as zlib keeps one: frequency and code share a slot, as do parent and length.</summary>
    private sealed class Tree(int size, int elems, int maxLength, int[] extraBits, int extraBase, ushort[]? staticLen)
    {
        public readonly ushort[] Fc = new ushort[size];
        public readonly ushort[] Dl = new ushort[size];
        public readonly int Elems = elems, MaxLength = maxLength, ExtraBase = extraBase;
        public readonly int[] ExtraBits = extraBits;
        public readonly ushort[]? StaticLen = staticLen;
        public int MaxCode;
    }

    private sealed class Deflater
    {
        private readonly byte[] _input;
        private int _next;
        private readonly byte[] _window = new byte[WindowSize];
        private readonly ushort[] _prev = new ushort[WSize];
        private readonly ushort[] _head = new ushort[HashSize];
        private int _insH, _strStart, _blockStart, _lookahead, _matchStart, _matchLength, _prevMatch, _prevLength, _insert;
        private bool _matchAvailable;
        private long _highWater;
        private uint _adlerA = 1, _adlerB;

        private readonly Tree _l = new(HeapSize, LCodes, MaxBits, ExtraLBits, Literals + 1, StaticLLen);
        private readonly Tree _d = new(2 * DCodes + 1, DCodes, MaxBits, ExtraDBits, 0, StaticDLen);
        private readonly Tree _bl = new(2 * BlCodes + 1, BlCodes, MaxBlBits, ExtraBlBits, 0, null);
        private readonly int[] _heap = new int[2 * LCodes + 1];
        private readonly byte[] _depth = new byte[2 * LCodes + 1];
        private readonly int[] _blCount = new int[MaxBits + 1];
        private int _heapLen, _heapMax;
        private long _optLen, _staticLen;
        private readonly ushort[] _dBuf = new ushort[LitBufSize];
        private readonly byte[] _lBuf = new byte[LitBufSize];
        private int _lastLit;

        private readonly MemoryStream _out = new();
        private ulong _bitBuf;
        private int _bitCount;

        public Deflater(ReadOnlySpan<byte> input)
        {
            _input = input.ToArray();
            _matchLength = _prevLength = MinMatch - 1;
            InitBlock();
        }

        public byte[] Run()
        {
            _out.WriteByte(0x78);
            _out.WriteByte(0xDA);
            DeflateSlow();
            uint adler = (_adlerB << 16) | _adlerA;
            _out.WriteByte((byte)(adler >> 24));
            _out.WriteByte((byte)(adler >> 16));
            _out.WriteByte((byte)(adler >> 8));
            _out.WriteByte((byte)adler);
            return _out.ToArray();
        }

        // ------------------------------------------------------------ bits

        private void SendBits(int value, int length)
        {
            _bitBuf |= (ulong)(uint)value << _bitCount;
            _bitCount += length;
            while (_bitCount >= 16)
            {
                _out.WriteByte((byte)_bitBuf);
                _out.WriteByte((byte)(_bitBuf >> 8));
                _bitBuf >>= 16;
                _bitCount -= 16;
            }
        }

        private void SendCode(int c, Tree tree) => SendBits(tree.Fc[c], tree.Dl[c]);

        private void BiWindup()
        {
            if (_bitCount > 8) { _out.WriteByte((byte)_bitBuf); _out.WriteByte((byte)(_bitBuf >> 8)); }
            else if (_bitCount > 0) _out.WriteByte((byte)_bitBuf);
            _bitBuf = 0;
            _bitCount = 0;
        }

        // ------------------------------------------------------------ the window

        private int ReadBuf(int at, int size)
        {
            int n = Math.Min(size, _input.Length - _next);
            if (n <= 0) return 0;
            Array.Copy(_input, _next, _window, at, n);
            for (int i = 0; i < n; i++)
            {
                _adlerA = (_adlerA + _window[at + i]) % 65521;
                _adlerB = (_adlerB + _adlerA) % 65521;
            }
            _next += n;
            return n;
        }

        private void FillWindow()
        {
            do
            {
                int more = WindowSize - _lookahead - _strStart;
                if (_strStart >= WSize + MaxDist)
                {
                    Array.Copy(_window, WSize, _window, 0, WSize);
                    _matchStart -= WSize;
                    _strStart -= WSize;
                    _blockStart -= WSize;
                    for (int n = 0; n < HashSize; n++) _head[n] = (ushort)(_head[n] >= WSize ? _head[n] - WSize : 0);
                    for (int n = 0; n < WSize; n++) _prev[n] = (ushort)(_prev[n] >= WSize ? _prev[n] - WSize : 0);
                    more += WSize;
                }
                if (_next >= _input.Length) break;
                int read = ReadBuf(_strStart + _lookahead, more);
                _lookahead += read;
                if (_lookahead + _insert >= MinMatch)
                {
                    int str = _strStart - _insert;
                    _insH = _window[str];
                    _insH = ((_insH << HashShift) ^ _window[str + 1]) & HashMask;
                    while (_insert > 0)
                    {
                        _insH = ((_insH << HashShift) ^ _window[str + MinMatch - 1]) & HashMask;
                        _prev[str & WMask] = _head[_insH];
                        _head[_insH] = (ushort)str;
                        str++;
                        _insert--;
                        if (_lookahead + _insert < MinMatch) break;
                    }
                }
            } while (_lookahead < MinLookahead && _next < _input.Length);

            // zlib zeroes the bytes past the data that a match may look at, the first time it gets there
            if (_highWater < WindowSize)
            {
                long curr = _strStart + (long)_lookahead;
                if (_highWater < curr)
                {
                    long init = Math.Min(WindowSize - curr, WinInit);
                    Array.Clear(_window, (int)curr, (int)init);
                    _highWater = curr + init;
                }
                else if (_highWater < curr + WinInit)
                {
                    long init = Math.Min(curr + WinInit - _highWater, WindowSize - _highWater);
                    Array.Clear(_window, (int)_highWater, (int)init);
                    _highWater += init;
                }
            }
        }

        private int InsertString(int str)
        {
            _insH = ((_insH << HashShift) ^ _window[str + MinMatch - 1]) & HashMask;
            int head = _prev[str & WMask] = _head[_insH];
            _head[_insH] = (ushort)str;
            return head;
        }

        private int LongestMatch(int curMatch)
        {
            int chainLength = MaxChain;
            int scan = _strStart;
            int bestLen = _prevLength;
            int niceMatch = NiceMatch;
            int limit = _strStart > MaxDist ? _strStart - MaxDist : 0;
            int strEnd = _strStart + MaxMatch;
            var w = _window;
            byte scanEnd1 = w[scan + bestLen - 1];
            byte scanEnd = w[scan + bestLen];
            if (_prevLength >= GoodMatch) chainLength >>= 2;
            if (niceMatch > _lookahead) niceMatch = _lookahead;
            do
            {
                int match = curMatch;
                if (w[match + bestLen] != scanEnd || w[match + bestLen - 1] != scanEnd1 || w[match] != w[scan] || w[++match] != w[scan + 1]) continue;
                int s = scan + 2;
                match++;
                // zlib compares eight bytes a turn and may run past the end by up to seven before it stops
                while (true)
                {
                    if (w[++s] != w[++match] || w[++s] != w[++match] || w[++s] != w[++match] || w[++s] != w[++match]
                        || w[++s] != w[++match] || w[++s] != w[++match] || w[++s] != w[++match] || w[++s] != w[++match] || s >= strEnd)
                        break;
                }
                int len = MaxMatch - (strEnd - s);
                if (len > bestLen)
                {
                    _matchStart = curMatch;
                    bestLen = len;
                    if (len >= niceMatch) break;
                    scanEnd1 = w[scan + bestLen - 1];
                    scanEnd = w[scan + bestLen];
                }
            } while ((curMatch = _prev[curMatch & WMask]) > limit && --chainLength != 0);
            return bestLen <= _lookahead ? bestLen : _lookahead;
        }

        // ------------------------------------------------------------ the matcher

        private void DeflateSlow()
        {
            while (true)
            {
                if (_lookahead < MinLookahead)
                {
                    FillWindow();
                    if (_lookahead == 0) break;
                }
                int hashHead = 0;
                if (_lookahead >= MinMatch) hashHead = InsertString(_strStart);
                _prevLength = _matchLength;
                _prevMatch = _matchStart;
                _matchLength = MinMatch - 1;
                if (hashHead != 0 && _prevLength < MaxLazyMatch && _strStart - hashHead <= MaxDist)
                {
                    _matchLength = LongestMatch(hashHead);
                    if (_matchLength <= 5 && _matchLength == MinMatch && _strStart - _matchStart > TooFar)
                        _matchLength = MinMatch - 1;
                }
                if (_prevLength >= MinMatch && _matchLength <= _prevLength)
                {
                    int maxInsert = _strStart + _lookahead - MinMatch;
                    bool flush = TallyDist(_strStart - 1 - _prevMatch, _prevLength - MinMatch);
                    _lookahead -= _prevLength - 1;
                    _prevLength -= 2;
                    do
                    {
                        if (++_strStart <= maxInsert) InsertString(_strStart);
                    } while (--_prevLength != 0);
                    _matchAvailable = false;
                    _matchLength = MinMatch - 1;
                    _strStart++;
                    if (flush) FlushBlock(false);
                }
                else if (_matchAvailable)
                {
                    if (TallyLit(_window[_strStart - 1])) FlushBlock(false);
                    _strStart++;
                    _lookahead--;
                }
                else
                {
                    _matchAvailable = true;
                    _strStart++;
                    _lookahead--;
                }
            }
            if (_matchAvailable)
            {
                TallyLit(_window[_strStart - 1]);
                _matchAvailable = false;
            }
            _insert = _strStart < MinMatch - 1 ? _strStart : MinMatch - 1;
            FlushBlock(true);
        }

        private void FlushBlock(bool last)
        {
            FlushBlock(_blockStart >= 0 ? _blockStart : -1, _strStart - _blockStart, last);
            _blockStart = _strStart;
        }

        // ------------------------------------------------------------ trees (zlib's trees.c)

        private bool TallyLit(byte c)
        {
            _dBuf[_lastLit] = 0;
            _lBuf[_lastLit++] = c;
            _l.Fc[c]++;
            return _lastLit == LitBufSize - 1;
        }

        private bool TallyDist(int dist, int len)
        {
            _dBuf[_lastLit] = (ushort)dist;
            _lBuf[_lastLit++] = (byte)len;
            dist--;
            _l.Fc[LengthCode[len] + Literals + 1]++;
            _d.Fc[DCode(dist)]++;
            return _lastLit == LitBufSize - 1;
        }

        private void InitBlock()
        {
            for (int n = 0; n < LCodes; n++) _l.Fc[n] = 0;
            for (int n = 0; n < DCodes; n++) _d.Fc[n] = 0;
            for (int n = 0; n < BlCodes; n++) _bl.Fc[n] = 0;
            _l.Fc[EndBlock] = 1;
            _optLen = _staticLen = 0;
            _lastLit = 0;
        }

        private bool Smaller(Tree tree, int n, int m) =>
            tree.Fc[n] < tree.Fc[m] || (tree.Fc[n] == tree.Fc[m] && _depth[n] <= _depth[m]);

        private void PqDownHeap(Tree tree, int k)
        {
            int v = _heap[k];
            int j = k << 1;
            while (j <= _heapLen)
            {
                if (j < _heapLen && Smaller(tree, _heap[j + 1], _heap[j])) j++;
                if (Smaller(tree, v, _heap[j])) break;
                _heap[k] = _heap[j];
                k = j;
                j <<= 1;
            }
            _heap[k] = v;
        }

        private void GenBitLen(Tree tree)
        {
            int maxCode = tree.MaxCode;
            int overflow = 0;
            for (int bits = 0; bits <= MaxBits; bits++) _blCount[bits] = 0;
            tree.Dl[_heap[_heapMax]] = 0;
            int h;
            for (h = _heapMax + 1; h < HeapSize; h++)
            {
                int n = _heap[h];
                int bits = tree.Dl[tree.Dl[n]] + 1;
                if (bits > tree.MaxLength) { bits = tree.MaxLength; overflow++; }
                tree.Dl[n] = (ushort)bits;
                if (n > maxCode) continue;
                _blCount[bits]++;
                int xbits = n >= tree.ExtraBase ? tree.ExtraBits[n - tree.ExtraBase] : 0;
                long f = tree.Fc[n];
                _optLen += f * (bits + xbits);
                if (tree.StaticLen is { } stree) _staticLen += f * (stree[n] + xbits);
            }
            if (overflow == 0) return;
            do
            {
                int bits = tree.MaxLength - 1;
                while (_blCount[bits] == 0) bits--;
                _blCount[bits]--;
                _blCount[bits + 1] += 2;
                _blCount[tree.MaxLength]--;
                overflow -= 2;
            } while (overflow > 0);
            for (int bits = tree.MaxLength; bits != 0; bits--)
            {
                int n = _blCount[bits];
                while (n != 0)
                {
                    int m = _heap[--h];
                    if (m > maxCode) continue;
                    if (tree.Dl[m] != bits)
                    {
                        _optLen += ((long)bits - tree.Dl[m]) * tree.Fc[m];
                        tree.Dl[m] = (ushort)bits;
                    }
                    n--;
                }
            }
        }

        private void BuildTree(Tree tree)
        {
            int elems = tree.Elems;
            int maxCode = -1;
            _heapLen = 0;
            _heapMax = HeapSize;
            for (int n = 0; n < elems; n++)
            {
                if (tree.Fc[n] != 0)
                {
                    _heap[++_heapLen] = maxCode = n;
                    _depth[n] = 0;
                }
                else tree.Dl[n] = 0;
            }
            while (_heapLen < 2)
            {
                int node = _heap[++_heapLen] = maxCode < 2 ? ++maxCode : 0;
                tree.Fc[node] = 1;
                _depth[node] = 0;
                _optLen--;
                if (tree.StaticLen is { } stree) _staticLen -= stree[node];
            }
            tree.MaxCode = maxCode;
            for (int n = _heapLen / 2; n >= 1; n--) PqDownHeap(tree, n);
            int next = elems;
            do
            {
                int n = _heap[1];
                _heap[1] = _heap[_heapLen--];
                PqDownHeap(tree, 1);
                int m = _heap[1];
                _heap[--_heapMax] = n;
                _heap[--_heapMax] = m;
                tree.Fc[next] = (ushort)(tree.Fc[n] + tree.Fc[m]);
                _depth[next] = (byte)((_depth[n] >= _depth[m] ? _depth[n] : _depth[m]) + 1);
                tree.Dl[n] = tree.Dl[m] = (ushort)next;
                _heap[1] = next++;
                PqDownHeap(tree, 1);
            } while (_heapLen >= 2);
            _heap[--_heapMax] = _heap[1];
            GenBitLen(tree);
            GenCodes(tree.Fc, tree.Dl, maxCode, _blCount);
        }

        private void ScanTree(Tree tree, int maxCode)
        {
            int prevLen = -1, nextLen = tree.Dl[0], count = 0, maxCount = 7, minCount = 4;
            if (nextLen == 0) { maxCount = 138; minCount = 3; }
            tree.Dl[maxCode + 1] = 0xFFFF;
            for (int n = 0; n <= maxCode; n++)
            {
                int curLen = nextLen;
                nextLen = tree.Dl[n + 1];
                if (++count < maxCount && curLen == nextLen) continue;
                if (count < minCount) _bl.Fc[curLen] += (ushort)count;
                else if (curLen != 0)
                {
                    if (curLen != prevLen) _bl.Fc[curLen]++;
                    _bl.Fc[Rep3To6]++;
                }
                else if (count <= 10) _bl.Fc[RepZ3To10]++;
                else _bl.Fc[RepZ11To138]++;
                count = 0;
                prevLen = curLen;
                if (nextLen == 0) { maxCount = 138; minCount = 3; }
                else if (curLen == nextLen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
        }

        private void SendTree(Tree tree, int maxCode)
        {
            int prevLen = -1, nextLen = tree.Dl[0], count = 0, maxCount = 7, minCount = 4;
            if (nextLen == 0) { maxCount = 138; minCount = 3; }
            for (int n = 0; n <= maxCode; n++)
            {
                int curLen = nextLen;
                nextLen = tree.Dl[n + 1];
                if (++count < maxCount && curLen == nextLen) continue;
                if (count < minCount)
                {
                    do SendCode(curLen, _bl); while (--count != 0);
                }
                else if (curLen != 0)
                {
                    if (curLen != prevLen) { SendCode(curLen, _bl); count--; }
                    SendCode(Rep3To6, _bl);
                    SendBits(count - 3, 2);
                }
                else if (count <= 10) { SendCode(RepZ3To10, _bl); SendBits(count - 3, 3); }
                else { SendCode(RepZ11To138, _bl); SendBits(count - 11, 7); }
                count = 0;
                prevLen = curLen;
                if (nextLen == 0) { maxCount = 138; minCount = 3; }
                else if (curLen == nextLen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
        }

        private int BuildBlTree()
        {
            ScanTree(_l, _l.MaxCode);
            ScanTree(_d, _d.MaxCode);
            BuildTree(_bl);
            int maxBlIndex;
            for (maxBlIndex = BlCodes - 1; maxBlIndex >= 3; maxBlIndex--)
                if (_bl.Dl[BlOrder[maxBlIndex]] != 0) break;
            _optLen += 3 * (maxBlIndex + 1) + 5 + 5 + 4;
            return maxBlIndex;
        }

        private void SendAllTrees(int lcodes, int dcodes, int blcodes)
        {
            SendBits(lcodes - 257, 5);
            SendBits(dcodes - 1, 5);
            SendBits(blcodes - 4, 4);
            for (int rank = 0; rank < blcodes; rank++) SendBits(_bl.Dl[BlOrder[rank]], 3);
            SendTree(_l, lcodes - 1);
            SendTree(_d, dcodes - 1);
        }

        private void CompressBlock(ushort[] lCode, ushort[] lLen, ushort[] dCode, ushort[] dLen)
        {
            for (int lx = 0; lx < _lastLit; lx++)
            {
                int dist = _dBuf[lx];
                int lc = _lBuf[lx];
                if (dist == 0) SendBits(lCode[lc], lLen[lc]);
                else
                {
                    int code = LengthCode[lc];
                    SendBits(lCode[code + Literals + 1], lLen[code + Literals + 1]);
                    int extra = ExtraLBits[code];
                    if (extra != 0) SendBits(lc - BaseLength[code], extra);
                    dist--;
                    code = DCode(dist);
                    SendBits(dCode[code], dLen[code]);
                    extra = ExtraDBits[code];
                    if (extra != 0) SendBits(dist - BaseDist[code], extra);
                }
            }
            SendBits(lCode[EndBlock], lLen[EndBlock]);
        }

        private void FlushBlock(int bufAt, int storedLen, bool last)
        {
            BuildTree(_l);
            BuildTree(_d);
            int maxBlIndex = BuildBlTree();
            long optLenB = (_optLen + 3 + 7) >> 3;
            long staticLenB = (_staticLen + 3 + 7) >> 3;
            if (staticLenB <= optLenB) optLenB = staticLenB;
            int lastBit = last ? 1 : 0;
            if (storedLen + 4 <= optLenB && bufAt >= 0)
            {
                SendBits(lastBit, 3);
                BiWindup();
                _out.WriteByte((byte)storedLen);
                _out.WriteByte((byte)(storedLen >> 8));
                _out.WriteByte((byte)~storedLen);
                _out.WriteByte((byte)(~storedLen >> 8));
                _out.Write(_window, bufAt, storedLen);
            }
            else if (staticLenB == optLenB)
            {
                SendBits((1 << 1) + lastBit, 3);
                CompressBlock(StaticLCode, StaticLLen, StaticDCode, StaticDLen);
            }
            else
            {
                SendBits((2 << 1) + lastBit, 3);
                SendAllTrees(_l.MaxCode + 1, _d.MaxCode + 1, maxBlIndex + 1);
                CompressBlock(_l.Fc, _l.Dl, _d.Fc, _d.Dl);
            }
            InitBlock();
            if (last) BiWindup();
        }
    }
}
