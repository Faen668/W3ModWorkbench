namespace W3ModWorkbench.Core.Textures;

/// <summary>BC7, the high quality colour format: eight modes that differ in how many colour regions a block has and how its endpoints and indices are stored.</summary>
internal static class Bc7
{
    // per mode: subsets, partition bits, rotation bits, index selection bit, colour bits, alpha bits, per-endpoint p-bits, shared p-bit, index bits, second index bits
    private static readonly (int Subsets, int PartitionBits, int RotationBits, int IndexSelection, int ColorBits, int AlphaBits, int EndpointPBits, int SharedPBit, int IndexBits, int Index2Bits)[] Modes =
    [
        (3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        (2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        (3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        (2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        (1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        (1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        (1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        (2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    ];

    private static readonly int[] Weights2 = [0, 21, 43, 64];
    private static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, _at++)
                value |= (_data[_at >> 3] >> (_at & 7) & 1) << i;
            return value;
        }
    }

    public static void Decode(ReadOnlySpan<byte> block, Span<byte> tile)
    {
        int mode = 0;
        while (mode < 8 && (block[0] >> mode & 1) == 0) mode++;
        if (mode >= 8)
        {
            // a reserved mode is opaque black
            for (int i = 0; i < 16; i++) { tile[i * 4] = tile[i * 4 + 1] = tile[i * 4 + 2] = 0; tile[i * 4 + 3] = 255; }
            return;
        }
        var m = Modes[mode];
        var bits = new Bits(block);
        bits.Read(mode + 1);
        int partition = bits.Read(m.PartitionBits);
        int rotation = bits.Read(m.RotationBits);
        int indexSelection = bits.Read(m.IndexSelection);

        int endpoints = m.Subsets * 2;
        Span<int> ep = stackalloc int[6 * 4]; // endpoint, then r g b a
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < endpoints; e++) ep[e * 4 + c] = bits.Read(m.ColorBits);
        if (m.AlphaBits > 0)
            for (int e = 0; e < endpoints; e++) ep[e * 4 + 3] = bits.Read(m.AlphaBits);

        Span<int> pbit = stackalloc int[6];
        if (m.EndpointPBits > 0)
            for (int e = 0; e < endpoints; e++) pbit[e] = bits.Read(1);
        else if (m.SharedPBit > 0)
            for (int s = 0; s < m.Subsets; s++) pbit[s * 2] = pbit[s * 2 + 1] = bits.Read(1);
        bool hasP = m.EndpointPBits > 0 || m.SharedPBit > 0;

        // widen each endpoint to 8 bits: add the p-bit below, then repeat the top bits into the low ones
        for (int e = 0; e < endpoints; e++)
            for (int c = 0; c < 4; c++)
            {
                int width = c < 3 ? m.ColorBits : m.AlphaBits;
                if (width == 0) { ep[e * 4 + c] = 255; continue; }
                int value = ep[e * 4 + c];
                if (hasP)
                {
                    value = value << 1 | pbit[e];
                    width++;
                }
                ep[e * 4 + c] = value << (8 - width) | value >> (2 * width - 8);
            }

        // which subset each pixel is in, and which pixels start a subset (their index is one bit shorter)
        Span<int> subset = stackalloc int[16];
        int anchor1 = 0, anchor2 = 0;
        if (m.Subsets == 2)
        {
            string shape = Bc7Tables.TwoSubsets[partition];
            for (int i = 0; i < 16; i++) subset[i] = shape[i] - '0';
            anchor1 = Bc7Tables.Anchor2[partition];
        }
        else if (m.Subsets == 3)
        {
            string shape = Bc7Tables.ThreeSubsets[partition];
            for (int i = 0; i < 16; i++) subset[i] = shape[i] - '0';
            anchor1 = Bc7Tables.Anchor3Second[partition];
            anchor2 = Bc7Tables.Anchor3Third[partition];
        }

        Span<int> primary = stackalloc int[16], secondary = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0 || m.Subsets >= 2 && i == anchor1 || m.Subsets == 3 && i == anchor2;
            primary[i] = bits.Read(m.IndexBits - (anchor ? 1 : 0));
        }
        if (m.Index2Bits > 0)
            for (int i = 0; i < 16; i++)
                secondary[i] = bits.Read(m.Index2Bits - (i == 0 ? 1 : 0));

        for (int i = 0; i < 16; i++)
        {
            int s = subset[i];
            int e0 = s * 2 * 4, e1 = (s * 2 + 1) * 4;
            int colorBits = m.IndexBits, alphaBits = m.Index2Bits > 0 ? m.Index2Bits : m.IndexBits;
            int colorIndex = primary[i], alphaIndex = m.Index2Bits > 0 ? secondary[i] : primary[i];
            if (m.Index2Bits > 0 && indexSelection == 1)
            {
                (colorBits, alphaBits) = (alphaBits, colorBits);
                (colorIndex, alphaIndex) = (alphaIndex, colorIndex);
            }
            int cw = Weight(colorBits, colorIndex), aw = Weight(alphaBits, alphaIndex);
            int r = Mix(ep[e0], ep[e1], cw), g = Mix(ep[e0 + 1], ep[e1 + 1], cw), b = Mix(ep[e0 + 2], ep[e1 + 2], cw), a = Mix(ep[e0 + 3], ep[e1 + 3], aw);
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }
            tile[i * 4] = (byte)b;
            tile[i * 4 + 1] = (byte)g;
            tile[i * 4 + 2] = (byte)r;
            tile[i * 4 + 3] = (byte)a;
        }
    }

    private static int Weight(int bits, int index) => bits switch { 2 => Weights2[index], 3 => Weights3[index], _ => Weights4[index] };

    private static int Mix(int a, int b, int w) => ((64 - w) * a + w * b + 32) >> 6;
}
