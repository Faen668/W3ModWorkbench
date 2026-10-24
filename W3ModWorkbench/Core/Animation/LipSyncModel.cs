using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// Lip sync made from a recording, as the game's own voice lines taught it: <see cref="LipSyncTrainer"/> learns how each
/// of the face's controls moves with the sound from the game's lines (their audio and the lip syncs made for them) on
/// this computer, and the model it makes is kept here, in the user's settings folder, never shipped. Making a lip sync
/// is <see cref="VoiceFeatures"/> of the sound around each frame (0.4 seconds either side), the network's
/// controls for that frame, smoothed over a few frames as the game's curves are, stretched by each control's gain (so
/// its movements are as big as the game's: a network learnt on the squared error plays safe and moves too little), kept
/// within the range each control
/// has in the game's lines. The controls the game's lip syncs never move keep their usual value.
/// </summary>
public sealed class LipSyncModel
{
    private const uint Magic = 0x4D4C3357; // "W3LM"
    private const int FileVersion = 2;

    /// <summary>The frames a second lip syncs are made at, as the game's are near.</summary>
    public const double FramesPerSecond = 30;

    /// <summary>Frames of sound either side of a frame that the network hears.</summary>
    public required int Context { get; init; }

    /// <summary>Frames the network's controls are averaged over (odd; 1 for none).</summary>
    public required int Smooth { get; init; }

    /// <summary>The controls the network makes, by index into the face's 148.</summary>
    public required int[] Outputs { get; init; }

    /// <summary>Each of the 148 controls' usual value in the game's lines (what a control the network doesn't make keeps).</summary>
    public required float[] Usual { get; init; }

    /// <summary>The lowest and highest each of <see cref="Outputs"/> goes in the game's lines.</summary>
    public required float[] Low { get; init; }
    public required float[] High { get; init; }

    /// <summary>
    /// For each of <see cref="Outputs"/>, what its smoothed value is multiplied by and then has added, so that its
    /// movements are as big as the game's (<see cref="LipSyncTrainer.Calibrate"/>); 1 and 0 for a model not calibrated.
    /// </summary>
    public float[]? Gain { get; init; }
    public float[]? Offset { get; init; }

    public required DenseNetwork Network { get; init; }

    /// <summary>What it learnt from: lines, frames, how close it came on lines it didn't learn from, and when.</summary>
    public required LipSyncModelInfo Info { get; init; }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "lipsync", "model.w3lm");

    /// <summary>A lip sync for a recording (48 kHz, any channels), as long as it is.</summary>
    public LipSyncFile Make(short[] samples, int channels)
    {
        var mono = VoiceFeatures.Mono(samples, channels);
        double duration = mono.Length / (double)VoiceFeatures.SampleRate;
        int frames = Math.Max(2, (int)Math.Round(duration * FramesPerSecond) + 1);
        double fps = duration > 0 ? (frames - 1) / duration : FramesPerSecond;
        var feat = VoiceFeatures.Of(mono, frames, fps);
        var made = new float[frames][];
        Parallel.For(0, frames, f => made[f] = Network.Predict(Input(feat, f)));
        var controls = new float[LipSyncFile.ControlCount][];
        for (int c = 0; c < controls.Length; c++) controls[c] = [Usual[c]];
        for (int o = 0; o < Outputs.Length; o++)
        {
            var keys = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int count = 0;
                for (int d = -(Smooth / 2); d <= Smooth / 2; d++)
                {
                    int g = f + d;
                    if (g < 0 || g >= frames) continue;
                    sum += made[g][o];
                    count++;
                }
                keys[f] = Math.Clamp(Calibrated(o, sum / count), Low[o], High[o]);
            }
            controls[Outputs[o]] = keys;
        }
        return new LipSyncFile { Duration = (float)duration, FrameCount = frames, Controls = controls }.WithStillControls();
    }

    /// <summary>A control's smoothed value from the network, its gain and offset applied.</summary>
    public float Calibrated(int output, float value) => Gain is null || Offset is null ? value : value * Gain[output] + Offset[output];

    /// <summary>The network's input at a frame: the features of the frames around it, the line's first and last repeated past its ends.</summary>
    public float[] Input(float[][] feat, int frame) => Input(feat, frame, Context);

    public static float[] Input(float[][] feat, int frame, int context)
    {
        var x = new float[VoiceFeatures.Bands * (2 * context + 1)];
        int at = 0;
        for (int d = -context; d <= context; d++)
        {
            Array.Copy(feat[Math.Clamp(frame + d, 0, feat.Length - 1)], 0, x, at, VoiceFeatures.Bands);
            at += VoiceFeatures.Bands;
        }
        return x;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".writing";
        using (var w = new BinaryWriter(File.Create(temp)))
        {
            w.Write(Magic);
            w.Write(FileVersion);
            w.Write(VoiceFeatures.Bands);
            w.Write(Context);
            w.Write(Smooth);
            w.Write(Outputs.Length);
            foreach (int o in Outputs) w.Write(o);
            foreach (float v in Usual) w.Write(v);
            foreach (float v in Low) w.Write(v);
            foreach (float v in High) w.Write(v);
            for (int o = 0; o < Outputs.Length; o++) w.Write(Gain?[o] ?? 1f);
            for (int o = 0; o < Outputs.Length; o++) w.Write(Offset?[o] ?? 0f);
            Info.Write(w);
            Network.Write(w);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The model saved at <paramref name="path"/>, or null when there isn't one (or it's from another version of the app).</summary>
    public static LipSyncModel? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic) return null;
            // version 1 had no gains: it's read as one not calibrated
            int version = r.ReadInt32();
            if (version is < 1 or > FileVersion || r.ReadInt32() != VoiceFeatures.Bands) return null;
            int context = r.ReadInt32(), smooth = r.ReadInt32(), outputs = r.ReadInt32();
            if (context is < 0 or > 64 || smooth is < 1 or > 31 || outputs is < 1 or > LipSyncFile.ControlCount) return null;
            var o = Enumerable.Range(0, outputs).Select(_ => r.ReadInt32()).ToArray();
            var usual = Enumerable.Range(0, LipSyncFile.ControlCount).Select(_ => r.ReadSingle()).ToArray();
            var low = Enumerable.Range(0, outputs).Select(_ => r.ReadSingle()).ToArray();
            var high = Enumerable.Range(0, outputs).Select(_ => r.ReadSingle()).ToArray();
            float[]? gain = null, offset = null;
            if (version >= 2)
            {
                gain = Enumerable.Range(0, outputs).Select(_ => r.ReadSingle()).ToArray();
                offset = Enumerable.Range(0, outputs).Select(_ => r.ReadSingle()).ToArray();
            }
            var info = LipSyncModelInfo.Read(r);
            var network = DenseNetwork.Read(r);
            if (network.Sizes[0] != VoiceFeatures.Bands * (2 * context + 1) || network.Sizes[^1] != outputs) return null;
            return new LipSyncModel { Context = context, Smooth = smooth, Outputs = o, Usual = usual, Low = low, High = high, Gain = gain, Offset = offset, Network = network, Info = info };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What a <see cref="LipSyncModel"/> learnt from and how well it did on lines it didn't learn from.</summary>
public sealed record LipSyncModelInfo(int Lines, long Frames, double MedianCorrelation, double JawCorrelation, DateTime Trained, string Source)
{
    public void Write(BinaryWriter w)
    {
        w.Write(Lines);
        w.Write(Frames);
        w.Write(MedianCorrelation);
        w.Write(JawCorrelation);
        w.Write(Trained.ToBinary());
        w.Write(Source);
    }

    public static LipSyncModelInfo Read(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt64(), r.ReadDouble(), r.ReadDouble(), DateTime.FromBinary(r.ReadInt64()), r.ReadString());

    public string Describe() => L.T("LipSyncModel.Info", L.T("Count.VoiceLine", Lines), Trained.ToLocalTime().ToString("d"), MedianCorrelation, JawCorrelation);

    /// <summary>What it learnt from and when, in a line.</summary>
    public string Short() => L.T("LipSyncModel.InfoShort", L.T("Count.VoiceLine", Lines), Trained.ToLocalTime().ToString("d"));
}
