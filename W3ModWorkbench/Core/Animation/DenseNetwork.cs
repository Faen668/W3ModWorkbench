using System.IO;
using System.Numerics;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// A small neural network of dense layers: ReLU between them, a linear output, trained by Adam on the mean squared
/// error. Written for <see cref="LipSyncModel"/>; sized so that learning from a game's voice lines takes minutes.
/// </summary>
public sealed class DenseNetwork
{
    private readonly int[] _sizes;
    private readonly float[][] _w, _b, _mw, _vw, _mb, _vb;
    private int _step;

    public IReadOnlyList<int> Sizes => _sizes;

    public DenseNetwork(int[] sizes, int seed)
    {
        _sizes = sizes;
        int layers = sizes.Length - 1;
        var random = new Random(seed);
        _w = new float[layers][]; _b = new float[layers][];
        for (int l = 0; l < layers; l++)
        {
            _w[l] = new float[sizes[l + 1] * sizes[l]];
            _b[l] = new float[sizes[l + 1]];
            float scale = MathF.Sqrt((l == layers - 1 ? 1f : 2f) / sizes[l]);
            for (int k = 0; k < _w[l].Length; k++) _w[l][k] = (float)(random.NextDouble() * 2 - 1) * scale;
        }
        _mw = _w.Select(w => new float[w.Length]).ToArray(); _vw = _w.Select(w => new float[w.Length]).ToArray();
        _mb = _b.Select(b => new float[b.Length]).ToArray(); _vb = _b.Select(b => new float[b.Length]).ToArray();
    }

    private DenseNetwork(int[] sizes, float[][] w, float[][] b)
    {
        _sizes = sizes; _w = w; _b = b;
        _mw = _vw = _mb = _vb = [];
    }

    public float[] Predict(float[] input) => Forward(input)[^1];

    private float[][] Forward(float[] input)
    {
        int layers = _sizes.Length - 1;
        var a = new float[layers + 1][];
        a[0] = input;
        for (int l = 0; l < layers; l++)
        {
            var output = new float[_sizes[l + 1]];
            int n = _sizes[l];
            for (int j = 0; j < output.Length; j++)
            {
                float v = _b[l][j] + Dot(_w[l].AsSpan(j * n, n), a[l]);
                output[j] = l < layers - 1 ? MathF.Max(0, v) : v;
            }
            a[l + 1] = output;
        }
        return a;
    }

    /// <summary>One step of learning on a batch: the gradient of the squared error over it, then Adam. Returns the batch's mean squared error.</summary>
    public double Train(float[][] inputs, float[][] targets, float rate)
    {
        int layers = _sizes.Length - 1, n = inputs.Length, outputs = _sizes[^1];
        var gw = _w.Select(w => new float[w.Length]).ToArray();
        var gb = _b.Select(b => new float[b.Length]).ToArray();
        double loss = 0;
        var gate = new object();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, n, Math.Max(1, n / Environment.ProcessorCount)), range =>
        {
            var lw = _w.Select(w => new float[w.Length]).ToArray();
            var lb = _b.Select(b => new float[b.Length]).ToArray();
            double local = 0;
            for (int s = range.Item1; s < range.Item2; s++)
            {
                var a = Forward(inputs[s]);
                var d = new float[outputs];
                for (int o = 0; o < outputs; o++)
                {
                    float e = a[layers][o] - targets[s][o];
                    local += e * e;
                    d[o] = e * 2f / outputs;
                }
                for (int l = layers - 1; l >= 0; l--)
                {
                    int width = _sizes[l];
                    var back = l > 0 ? new float[width] : null;
                    for (int j = 0; j < d.Length; j++)
                    {
                        float dj = d[j];
                        if (dj == 0) continue;
                        lb[l][j] += dj;
                        Axpy(dj, a[l], lw[l].AsSpan(j * width, width));
                        if (back is not null) Axpy(dj, _w[l].AsSpan(j * width, width), back);
                    }
                    if (back is null) break;
                    for (int i = 0; i < width; i++) if (a[l][i] <= 0) back[i] = 0;
                    d = back;
                }
            }
            lock (gate)
            {
                for (int l = 0; l < layers; l++) { Add(gw[l], lw[l]); Add(gb[l], lb[l]); }
                loss += local;
            }
        });
        _step++;
        for (int l = 0; l < layers; l++)
        {
            Adam(_w[l], gw[l], _mw[l], _vw[l], rate, n);
            Adam(_b[l], gb[l], _mb[l], _vb[l], rate, n);
        }
        return loss / n / outputs;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(_sizes.Length);
        foreach (int s in _sizes) w.Write(s);
        for (int l = 0; l < _w.Length; l++)
        {
            foreach (float v in _w[l]) w.Write(v);
            foreach (float v in _b[l]) w.Write(v);
        }
    }

    public static DenseNetwork Read(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 2 || count > 8) throw new InvalidDataException();
        var sizes = Enumerable.Range(0, count).Select(_ => r.ReadInt32()).ToArray();
        if (sizes.Any(s => s < 1 || s > 1 << 16)) throw new InvalidDataException();
        var w = new float[count - 1][]; var b = new float[count - 1][];
        for (int l = 0; l < count - 1; l++)
        {
            w[l] = new float[sizes[l + 1] * sizes[l]];
            for (int k = 0; k < w[l].Length; k++) w[l][k] = r.ReadSingle();
            b[l] = new float[sizes[l + 1]];
            for (int k = 0; k < b[l].Length; k++) b[l][k] = r.ReadSingle();
        }
        return new DenseNetwork(sizes, w, b);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        int k = 0, width = Vector<float>.Count;
        for (; k <= a.Length - width; k += width) sum += new Vector<float>(a[k..]) * new Vector<float>(b[k..]);
        float s = Vector.Dot(sum, Vector<float>.One);
        for (; k < a.Length; k++) s += a[k] * b[k];
        return s;
    }

    private static void Axpy(float scale, ReadOnlySpan<float> x, Span<float> y)
    {
        int k = 0, width = Vector<float>.Count;
        var vs = new Vector<float>(scale);
        for (; k <= x.Length - width; k += width) (new Vector<float>(y[k..]) + vs * new Vector<float>(x[k..])).CopyTo(y[k..]);
        for (; k < x.Length; k++) y[k] += scale * x[k];
    }

    private static void Add(float[] a, float[] b)
    {
        for (int k = 0; k < a.Length; k++) a[k] += b[k];
    }

    private void Adam(float[] w, float[] g, float[] m, float[] v, float rate, int n)
    {
        const float b1 = 0.9f, b2 = 0.999f;
        float c1 = 1 - MathF.Pow(b1, _step), c2 = 1 - MathF.Pow(b2, _step);
        for (int k = 0; k < w.Length; k++)
        {
            float gk = g[k] / n;
            m[k] = b1 * m[k] + (1 - b1) * gk;
            v[k] = b2 * v[k] + (1 - b2) * gk * gk;
            w[k] -= rate * (m[k] / c1) / (MathF.Sqrt(v[k] / c2) + 1e-8f);
        }
    }
}
