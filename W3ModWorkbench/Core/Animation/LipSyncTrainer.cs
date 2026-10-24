using System.IO;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>How far learning lip sync has got: what it's doing, and how much of it is done (0 to 1).</summary>
public readonly record struct LipSyncTrainingProgress(string Stage, double Done);

/// <summary>
/// Learns <see cref="LipSyncModel"/> from a game's voice lines: each line's sound (decoded from its speech file) and the
/// lip sync made for it, frame by frame. A share of lines is kept back and the model scored on them, so it's known
/// how close it comes to the game's own on lines it never saw. All of it runs here, on the game the user has.
/// </summary>
public static class LipSyncTrainer
{
    /// <summary>
    /// How big a model is learnt and from how much. The defaults were chosen on the game's English lines, scored on
    /// lines left out: two hidden layers (512, 256) hearing ±12 frames came closest (median correlation 0.58 against
    /// 0.55 for one layer of 512 on the same 6,000 lines), averaged over 5 frames; 10,000 lines take about ten minutes
    /// on a desktop.
    /// </summary>
    public sealed record Options(int Lines = 10000, int[]? Hidden = null, int Epochs = 6, int Context = 12, int Smooth = 5, int Seed = 7)
    {
        public int[] HiddenLayers => Hidden ?? [512, 256];
    }

    /// <summary>The voice lines a game folder has with lip syncs: its speech files, the language each is for.</summary>
    public static List<(string Path, W3Language Language)> SpeechFiles(string gameFolder)
    {
        var found = new List<(string, W3Language)>();
        foreach (string root in new[] { Path.Combine(gameFolder, "content"), Path.Combine(gameFolder, "dlc") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*pc.w3speech", SearchOption.AllDirectories))
                if (SpeechFolder.LanguageOf(file) is { } language && new FileInfo(file).Length > 1024) found.Add((file, language));
        }
        return found;
    }

    /// <summary>
    /// The model learnt on this computer, or, the first time, one learnt now from the game in <paramref name="gameFolder"/>
    /// and saved for next time. The game's folder is needed only for learning.
    /// </summary>
    public static LipSyncModel LoadOrLearn(string? gameFolder, IProgress<LipSyncTrainingProgress>? progress = null, CancellationToken cancel = default)
    {
        if (LipSyncModel.Load(LipSyncModel.DefaultPath) is { } saved) return saved;
        return Learn(gameFolder, progress, cancel);
    }

    /// <summary>Learns a model from the game in <paramref name="gameFolder"/> and saves it in place of any learnt before.</summary>
    public static LipSyncModel Learn(string? gameFolder, IProgress<LipSyncTrainingProgress>? progress = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(gameFolder) || !Directory.Exists(gameFolder)) throw new IOException(L.T("LipSyncTrainer.NoGameFolder"));
        var files = SpeechFiles(gameFolder);
        if (files.Count == 0) throw new InvalidDataException(L.T("LipSyncTrainer.NoSpeechFiles", gameFolder));
        var model = Train(files, new Options(), progress, cancel);
        model.Save(LipSyncModel.DefaultPath);
        return model;
    }

    public static LipSyncModel Train(IReadOnlyList<(string Path, W3Language Language)> speechFiles, Options options,
        IProgress<LipSyncTrainingProgress>? progress = null, CancellationToken cancel = default)
    {
        // every line with a sound and a lip sync, a shuffled share of them
        var all = new List<(W3Speech Speech, uint Id)>();
        foreach (var (path, language) in speechFiles)
        {
            var speech = W3Speech.Open(path, language);
            all.AddRange(speech.Order.Where(id => speech.Has(id) && speech.HasLipSync(id)).Select(id => (speech, id)));
        }
        if (all.Count == 0) throw new InvalidDataException(L.T("LipSyncTrainer.NoLines"));
        var random = new Random(options.Seed);
        var picked = all.OrderBy(_ => random.Next()).Take(options.Lines).ToList();

        var usable = ReadLines(picked, progress, 0.25, cancel);
        int held = Math.Clamp(usable.Count / 50, 1, 500);
        var test = usable.Take(held).ToList();
        var train = usable.Skip(held).ToList();
        if (train.Count < 50) throw new InvalidDataException(L.T("LipSyncTrainer.TooFewLines", train.Count));

        // what each control does in the game's lines: its usual value, its range, whether it moves at all
        var usual = new double[LipSyncFile.ControlCount];
        var square = new double[LipSyncFile.ControlCount];
        var low = Enumerable.Repeat(float.MaxValue, LipSyncFile.ControlCount).ToArray();
        var high = Enumerable.Repeat(float.MinValue, LipSyncFile.ControlCount).ToArray();
        long frames = 0;
        foreach (var (_, controls) in train)
            foreach (var row in controls)
            {
                frames++;
                for (int c = 0; c < row.Length; c++)
                {
                    usual[c] += row[c];
                    square[c] += row[c] * row[c];
                    low[c] = Math.Min(low[c], row[c]);
                    high[c] = Math.Max(high[c], row[c]);
                }
            }
        var outputs = Enumerable.Range(0, LipSyncFile.ControlCount).Where(c => square[c] / frames - Math.Pow(usual[c] / frames, 2) > 1e-6).ToArray();
        int dim = VoiceFeatures.Bands * (2 * options.Context + 1);
        var network = new DenseNetwork([dim, .. options.HiddenLayers, outputs.Length], options.Seed);

        var order = new List<(int Line, int Frame)>();
        for (int i = 0; i < train.Count; i++)
            for (int f = 0; f < train[i].Controls.Length; f++) order.Add((i, f));
        const int Batch = 256;
        int batches = order.Count / Batch;
        for (int epoch = 0; epoch < options.Epochs; epoch++)
        {
            var shuffled = order.OrderBy(_ => random.Next()).ToArray();
            float rate = 1e-3f * MathF.Pow(0.75f, epoch);
            for (int b = 0; b < batches; b++)
            {
                cancel.ThrowIfCancellationRequested();
                var inputs = new float[Batch][];
                var targets = new float[Batch][];
                for (int k = 0; k < Batch; k++)
                {
                    var (li, f) = shuffled[b * Batch + k];
                    inputs[k] = LipSyncModel.Input(train[li].Feat, f, options.Context);
                    targets[k] = outputs.Select(c => train[li].Controls[f][c]).ToArray();
                }
                network.Train(inputs, targets, rate);
                if (b % 50 == 0)
                    progress?.Report(new(L.T("LipSyncTrainer.Learning", epoch + 1, options.Epochs), 0.25 + 0.7 * (epoch + (double)b / batches) / options.Epochs));
            }
        }

        var provisional = new LipSyncModel
        {
            Context = options.Context, Smooth = options.Smooth, Outputs = outputs,
            Usual = usual.Select(u => (float)(u / frames)).ToArray(),
            Low = outputs.Select(c => low[c]).ToArray(), High = outputs.Select(c => high[c]).ToArray(),
            Network = network, Info = new(train.Count, frames, 0, 0, DateTime.UtcNow, ""),
        };
        progress?.Report(new(L.T("LipSyncTrainer.Checking", test.Count), 0.97));
        provisional = Calibrate(provisional, test);
        var (median, jaw) = Score(provisional, test);
        string source = string.Join("; ", speechFiles.Select(s => Path.GetFileName(s.Path)).Distinct());
        progress?.Report(new(L.T("LipSyncTrainer.Done"), 1));
        return new LipSyncModel
        {
            Context = provisional.Context, Smooth = provisional.Smooth, Outputs = outputs, Usual = provisional.Usual,
            Low = provisional.Low, High = provisional.High, Gain = provisional.Gain, Offset = provisional.Offset, Network = network,
            Info = new(train.Count, frames, median, jaw, DateTime.UtcNow, source),
        };
    }

    /// <summary>
    /// Each line's features and its lip sync's controls at each frame, read several at once; lines that can't be read
    /// are passed over. <paramref name="share"/> is how much of the progress reading is.
    /// </summary>
    public static List<(float[][] Feat, float[][] Controls)> ReadLines(IReadOnlyList<(W3Speech Speech, uint Id)> picked,
        IProgress<LipSyncTrainingProgress>? progress = null, double share = 1, CancellationToken cancel = default)
    {
        var lines = new (float[][] Feat, float[][] Controls)?[picked.Count];
        int read = 0;
        Parallel.For(0, picked.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var (speech, id) = picked[i];
            try
            {
                var voice = speech.Voice(id);
                var lipBytes = speech.LipSync(id);
                if (voice is null || lipBytes is null) return;
                var (pcm, channels) = WwiseOpus.Decode(voice.Wem);
                var lip = LipSyncFile.ReadAny(lipBytes);
                if (lip.FrameCount < 2 || lip.Controls.Length != LipSyncFile.ControlCount) return;
                var feat = VoiceFeatures.Of(VoiceFeatures.Mono(pcm, channels), lip.FrameCount, lip.FramesPerSecond);
                var controls = new float[lip.FrameCount][];
                for (int f = 0; f < lip.FrameCount; f++)
                {
                    controls[f] = new float[LipSyncFile.ControlCount];
                    for (int c = 0; c < LipSyncFile.ControlCount; c++) controls[f][c] = lip.Value(c, f);
                }
                lines[i] = (feat, controls);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or EndOfStreamException or NotSupportedException)
            {
                // a line that can't be read is passed over
            }
            int done = Interlocked.Increment(ref read);
            if (done % 200 == 0) progress?.Report(new(L.T("LipSyncTrainer.Reading", done, picked.Count), share * done / picked.Count));
        });
        return lines.Where(l => l is not null).Select(l => l!.Value).ToList();
    }

    /// <summary>
    /// The model with each control's gain and offset set so its movements are as big as the game's on
    /// <paramref name="lines"/> (lines it didn't learn from): its smoothed values given the same mean and spread as the
    /// game's. A network learnt on the squared error plays safe when it isn't sure and so moves too little; this
    /// stretches each control back out without changing when it moves. A gain is kept between 0.5 and 3.
    /// </summary>
    public static LipSyncModel Calibrate(LipSyncModel model, IReadOnlyList<(float[][] Feat, float[][] Controls)> lines)
    {
        int outputs = model.Outputs.Length;
        var s = new double[outputs, 4];
        long n = 0;
        foreach (var (feat, controls) in lines)
        {
            var smoothed = Smoothed(model, feat);
            for (int f = 0; f < controls.Length; f++)
            {
                n++;
                for (int o = 0; o < outputs; o++)
                {
                    double p = smoothed[f][o], y = controls[f][model.Outputs[o]];
                    s[o, 0] += p; s[o, 1] += p * p; s[o, 2] += y; s[o, 3] += y * y;
                }
            }
        }
        var gain = new float[outputs];
        var offset = new float[outputs];
        for (int o = 0; o < outputs; o++)
        {
            double mp = s[o, 0] / n, my = s[o, 2] / n;
            double sp = Math.Sqrt(Math.Max(0, s[o, 1] / n - mp * mp)), sy = Math.Sqrt(Math.Max(0, s[o, 3] / n - my * my));
            double g = sp < 1e-6 ? 1 : Math.Clamp(sy / sp, 0.5, 3);
            gain[o] = (float)g;
            offset[o] = (float)(my - g * mp);
        }
        return new LipSyncModel
        {
            Context = model.Context, Smooth = model.Smooth, Outputs = model.Outputs, Usual = model.Usual, Low = model.Low, High = model.High,
            Gain = gain, Offset = offset, Network = model.Network, Info = model.Info,
        };
    }

    /// <summary>The network's controls at each frame of a line, averaged over the model's smoothing window (not yet calibrated).</summary>
    private static float[][] Smoothed(LipSyncModel model, float[][] feat)
    {
        int frames = feat.Length, outputs = model.Outputs.Length;
        var made = new float[frames][];
        Parallel.For(0, frames, f => made[f] = model.Network.Predict(model.Input(feat, f)));
        var result = new float[frames][];
        for (int f = 0; f < frames; f++)
        {
            result[f] = new float[outputs];
            for (int o = 0; o < outputs; o++)
            {
                float a = 0;
                int k = 0;
                for (int d = -(model.Smooth / 2); d <= model.Smooth / 2; d++)
                {
                    int g = f + d;
                    if (g < 0 || g >= frames) continue;
                    a += made[g][o];
                    k++;
                }
                result[f][o] = a / k;
            }
        }
        return result;
    }

    /// <summary>
    /// How close the model comes to the game's lip syncs of lines it didn't learn from: the median of its controls'
    /// correlations with the game's, and the jaw's (jaw_open_a).
    /// </summary>
    public static (double Median, double Jaw) Score(LipSyncModel model, IReadOnlyList<(float[][] Feat, float[][] Controls)> lines)
    {
        int outputs = model.Outputs.Length;
        var s = new double[outputs, 5];
        long n = 0;
        foreach (var (feat, controls) in lines)
        {
            int frames = controls.Length;
            var smoothed = Smoothed(model, feat);
            for (int f = 0; f < frames; f++)
            {
                n++;
                for (int o = 0; o < outputs; o++)
                {
                    double a = Math.Clamp(model.Calibrated(o, smoothed[f][o]), model.Low[o], model.High[o]);
                    double b = controls[f][model.Outputs[o]];
                    s[o, 0] += a * b; s[o, 1] += a * a; s[o, 2] += b * b; s[o, 3] += a; s[o, 4] += b;
                }
            }
        }
        var r = new double[outputs];
        for (int o = 0; o < outputs; o++)
        {
            double cov = s[o, 0] - s[o, 3] * s[o, 4] / n, vx = s[o, 1] - s[o, 3] * s[o, 3] / n, vy = s[o, 2] - s[o, 4] * s[o, 4] / n;
            r[o] = vx < 1e-12 || vy < 1e-12 ? 0 : cov / Math.Sqrt(vx * vy);
        }
        int jaw = Array.IndexOf(model.Outputs, Array.IndexOf(LipSyncRe.ControlNames, "jaw_open_a"));
        return (r.OrderBy(x => x).ElementAt(outputs / 2), jaw >= 0 ? r[jaw] : 0);
    }
}
