using System.Numerics;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// What a voice line sounds like at each frame of its lip sync, for <see cref="LipSyncModel"/>: the energy in 40 bands
/// spaced as hearing spaces pitch (60 Hz to 8 kHz) of 43 ms of sound around each frame, on a log scale, and each band's
/// mean and spread over the line taken away, so how loud the line is and how it was recorded matter less.
/// </summary>
public static class VoiceFeatures
{
    public const int Bands = 40;
    public const int SampleRate = 48000;
    private const int Window = 2048;

    private static readonly List<(int Bin, float Weight)>[] Filters = MakeFilters();
    private static readonly float[] Hann = Enumerable.Range(0, Window).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1)))).ToArray();

    /// <summary>The features of <paramref name="frames"/> frames <paramref name="fps"/> a second of mono 48 kHz sound.</summary>
    public static float[][] Of(ReadOnlySpan<short> pcm, int frames, double fps)
    {
        var buffer = new Complex[Window];
        var feat = new float[frames][];
        for (int f = 0; f < frames; f++)
        {
            int centre = (int)(f / fps * SampleRate);
            for (int i = 0; i < Window; i++)
            {
                int at = centre - Window / 2 + i;
                buffer[i] = new Complex(at >= 0 && at < pcm.Length ? pcm[at] / 32768.0 * Hann[i] : 0, 0);
            }
            Fft(buffer);
            var x = new float[Bands];
            for (int b = 0; b < Bands; b++)
            {
                double e = 0;
                foreach (var (k, w) in Filters[b]) e += w * (buffer[k].Real * buffer[k].Real + buffer[k].Imaginary * buffer[k].Imaginary);
                x[b] = (float)Math.Log(e + 1e-8);
            }
            feat[f] = x;
        }
        for (int b = 0; b < Bands; b++)
        {
            double mean = 0, spread = 0;
            foreach (var x in feat) mean += x[b];
            mean /= Math.Max(1, frames);
            foreach (var x in feat) spread += (x[b] - mean) * (x[b] - mean);
            double sd = Math.Sqrt(spread / Math.Max(1, frames)) + 1e-3;
            foreach (var x in feat) x[b] = (float)((x[b] - mean) / sd);
        }
        return feat;
    }

    /// <summary>One channel of sound from several, their average.</summary>
    public static short[] Mono(short[] samples, int channels)
    {
        if (channels <= 1) return samples;
        var mono = new short[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++) sum += samples[i * channels + c];
            mono[i] = (short)(sum / channels);
        }
        return mono;
    }

    private static List<(int, float)>[] MakeFilters()
    {
        static double Mel(double hz) => 2595 * Math.Log10(1 + hz / 700);
        static double Hz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
        double lo = Mel(60), hi = Mel(8000);
        var edges = Enumerable.Range(0, Bands + 2).Select(i => Hz(lo + (hi - lo) * i / (Bands + 1)) / SampleRate * Window).ToArray();
        var filters = new List<(int, float)>[Bands];
        for (int b = 0; b < Bands; b++)
        {
            filters[b] = [];
            for (int k = (int)edges[b]; k <= (int)edges[b + 2] + 1 && k < Window / 2; k++)
            {
                double w = k < edges[b + 1] ? (k - edges[b]) / (edges[b + 1] - edges[b]) : (edges[b + 2] - k) / (edges[b + 2] - edges[b + 1]);
                if (w > 0) filters[b].Add((k, (float)w));
            }
        }
        return filters;
    }

    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                var u = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var x = a[i + k];
                    var y = a[i + k + len / 2] * u;
                    a[i + k] = x + y;
                    a[i + k + len / 2] = x - y;
                    u *= w;
                }
            }
        }
    }
}
