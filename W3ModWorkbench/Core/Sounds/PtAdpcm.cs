using System.Buffers.Binary;
using System.IO;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// PlatinumGames' ADPCM for Wwise (format 0x8311, the controller's vibration waveforms in <c>fx_haptic.bnk</c>), a port
/// of vgmstream's decoder (ISC licence: https://github.com/vgmstream/vgmstream). Each block of the sound holds a frame
/// for each channel in turn, a block's share each: two samples as they are, a table index, then a nibble a sample, low
/// first, each a step from the table (and the index's next) added to twice the last sample less the one before.
/// </summary>
public static class PtAdpcm
{
    // [index][nibble] = (step, next index); index 12, past the table, is all zeros: no step, and back to index 0
    private static readonly int[,,] Table = Build();

    private static int[,,] Build()
    {
        int[][] steps =
        [
            [-14, -10, -7, -5, -3, -2, -1, 0, 0, 1, 2, 3, 5, 7, 10, 14],
            [-28, -20, -14, -10, -7, -5, -3, -1, 1, 3, 5, 7, 10, 14, 20, 28],
            [-56, -40, -28, -20, -14, -10, -6, -2, 2, 6, 10, 14, 20, 28, 40, 56],
            [-112, -80, -56, -40, -28, -20, -12, -4, 4, 12, 20, 28, 40, 56, 80, 112],
            [-224, -160, -112, -80, -56, -40, -24, -8, 8, 24, 40, 56, 80, 112, 160, 224],
            [-448, -320, -224, -160, -112, -80, -48, -16, 16, 48, 80, 112, 160, 224, 320, 448],
            [-896, -640, -448, -320, -224, -160, -96, -32, 32, 96, 160, 224, 320, 448, 640, 896],
            [-1792, -1280, -896, -640, -448, -320, -192, -64, 64, 192, 320, 448, 640, 896, 1280, 1792],
            [-3584, -2560, -1792, -1280, -896, -640, -384, -128, 128, 384, 640, 896, 1280, 1792, 2560, 3584],
            [-7168, -5120, -3584, -2560, -1792, -1280, -768, -256, 256, 768, 1280, 1792, 2560, 3584, 5120, 7168],
            [-14336, -10240, -7168, -5120, -3584, -2560, -1536, -512, 512, 1536, 2560, 3584, 5120, 7168, 10240, 14336],
            [-28672, -20480, -14336, -10240, -7168, -5120, -3072, -1024, 1024, 3072, 5120, 7168, 10240, 14336, 20480, 28672],
        ];
        int[][] next =
        [
            [2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2],
            [3, 3, 2, 2, 1, 1, 1, 0, 0, 1, 1, 1, 2, 2, 3, 3],
            [4, 4, 3, 3, 2, 2, 2, 1, 1, 2, 2, 2, 3, 3, 4, 4],
            [5, 5, 4, 4, 3, 3, 3, 2, 2, 3, 3, 3, 4, 4, 5, 5],
            [6, 6, 5, 5, 4, 4, 4, 3, 3, 4, 4, 4, 5, 5, 6, 6],
            [7, 7, 6, 6, 5, 5, 5, 4, 4, 5, 5, 5, 6, 6, 7, 7],
            [8, 8, 7, 7, 6, 6, 6, 5, 5, 6, 6, 6, 7, 7, 8, 8],
            [9, 9, 8, 8, 7, 7, 7, 6, 6, 7, 7, 7, 8, 8, 9, 9],
            [10, 10, 9, 9, 8, 8, 8, 7, 7, 8, 8, 8, 9, 9, 10, 10],
            [11, 11, 10, 10, 9, 9, 9, 8, 8, 9, 9, 9, 10, 10, 11, 11],
            [11, 11, 11, 11, 10, 10, 10, 9, 9, 10, 10, 10, 11, 11, 11, 11],
            [11, 11, 11, 11, 11, 11, 11, 10, 10, 11, 11, 11, 11, 11, 11, 11],
        ];
        var table = new int[16, 16, 2];
        for (int i = 0; i < steps.Length; i++)
            for (int n = 0; n < 16; n++) (table[i, n, 0], table[i, n, 1]) = (steps[i][n], next[i][n]);
        return table;
    }

    /// <summary>The sound's samples, its channels interleaved.</summary>
    public static short[] Decode(byte[] riff, WemInfo info)
    {
        var data = info.Chunks.First(c => c.Id == "data");
        int channels = Math.Max(1, info.Channels);
        int frame = info.BlockAlign / channels;
        if (frame < 6) throw new InvalidDataException("The sound's blocks are too small for PTADPCM frames.");
        int perFrame = 2 + (frame - 5) * 2;
        int blocks = data.Size / (frame * channels);
        var output = new short[blocks * perFrame * channels];
        for (int b = 0; b < blocks; b++)
            for (int c = 0; c < channels; c++)
            {
                int at = data.Offset + (b * channels + c) * frame;
                int hist2 = BinaryPrimitives.ReadInt16LittleEndian(riff.AsSpan(at));
                int hist1 = BinaryPrimitives.ReadInt16LittleEndian(riff.AsSpan(at + 2));
                int index = Math.Min((int)riff[at + 4], 12);
                int o = (b * perFrame) * channels + c;
                output[o] = (short)hist2;
                output[o + channels] = (short)hist1;
                for (int i = 0; i < perFrame - 2; i++)
                {
                    int nibble = (riff[at + 5 + i / 2] >> ((i & 1) * 4)) & 0xF;
                    int step = index < 12 ? Table[index, nibble, 0] : 0;
                    index = index < 12 ? Table[index, nibble, 1] : 0;
                    int sample = Math.Clamp(step + 2 * hist1 - hist2, short.MinValue, short.MaxValue);
                    output[o + (i + 2) * channels] = (short)sample;
                    hist2 = hist1;
                    hist1 = sample;
                }
            }
        // trimmed to the length the sound says it is, when it says
        long frames = info.Samples > 0 ? Math.Min(info.Samples, output.Length / channels) : output.Length / channels;
        return output.AsSpan(0, (int)frames * channels).ToArray();
    }
}
