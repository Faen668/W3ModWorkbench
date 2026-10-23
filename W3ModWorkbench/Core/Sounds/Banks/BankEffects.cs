// loc-ignore-start
// Settings are named as Wwise's authoring tool labels them, in English, so they can be found there; see BankTerms.
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// The settings of Wwise's own effects and sources, as a bank keeps them: each plugin's settings are a fixed list of
/// values one after another. Each layout here was checked against every block of that plugin in the game's banks
/// and older mods' banks (its size must come out exactly, its values must be sensible). Blocks of other plugins, or
/// of an unexpected size, are kept as bytes.
/// </summary>
internal static class BankEffects
{
    private enum T { Float, Int, Flag, Choice32, Choice8, Id }

    private sealed record Setting(string Name, T Type, string Unit = "", string[]? Choices = null);

    private static Setting F(string name, string unit = "") => new(name, T.Float, unit);
    private static Setting I(string name) => new(name, T.Int);
    private static Setting B(string name) => new(name, T.Flag);
    private static Setting C(string name, params string[] choices) => new(name, T.Choice32, "", choices);
    private static Setting C8(string name, params string[] choices) => new(name, T.Choice8, "", choices);

    private static readonly string[] EqFilters = ["Low Pass", "High Pass", "Band Pass", "Notch", "Low Shelf", "High Shelf", "Peaking"];
    private static readonly string[] Filters = ["None", "Low Shelf", "Peaking", "High Shelf", "Low Pass", "High Pass", "Band Pass", "Notch"];
    private static readonly string[] Waveforms = ["Sine", "Triangle", "Square", "Saw Up", "Saw Down", "Random"];

    private static Setting[] Band(string band) =>
        [C($"{band} Filter", EqFilters), F($"{band} Gain", "dB"), F($"{band} Frequency", "Hz"), F($"{band} Q"), B($"{band} On")];

    private static Setting[] RoomFilter(int n) => [F($"Filter {n} Gain", "dB"), F($"Filter {n} Frequency", "Hz"), F($"Filter {n} Q")];

    private static Setting[] StereoChannel(string side) =>
        [C($"{side} Input", "Left or Right", "Center", "Downmix", "None"), F($"{side} Delay Time", "s"), F($"{side} Feedback", "dB"), F($"{side} Cross-feed", "dB")];

    private static Setting[] Voice(int n) =>
        [B($"Voice {n} On"), F($"Voice {n} Pitch", "cents"), F($"Voice {n} Gain", "dB"), C($"Voice {n} Filter", Filters),
         F($"Voice {n} Filter Gain", "dB"), F($"Voice {n} Filter Frequency", "Hz"), F($"Voice {n} Filter Q")];

    /// <summary>The layouts, by plugin number.</summary>
    private static readonly Dictionary<long, Setting[]> Layouts = new()
    {
        [0x00650002] = [F("Duration", "s"), F("Random Duration Minus", "s"), F("Random Duration Plus", "s")], // Wwise Silence
        [0x00660002] = // Wwise Tone Generator
        [
            F("Gain", "dB"), F("Start Frequency", "Hz"), F("Stop Frequency", "Hz"), F("Start Frequency Random Min", "Hz"),
            F("Start Frequency Random Max", "Hz"), B("Frequency Sweep"), C("Sweep", "Linear", "Logarithmic"), F("Stop Frequency Random Min", "Hz"),
            F("Stop Frequency Random Max", "Hz"), C("Waveform", "Sine", "Triangle", "Square", "Sawtooth", "White Noise", "Pink Noise"),
            C("Duration Mode", "Fixed", "Envelope"), F("Fixed Duration", "s"), F("Attack", "s"), F("Decay", "s"), F("Sustain", "s"),
            F("Sustain Level", "dB"), F("Release", "s"), I("Channel Mask"),
        ],
        [0x00690003] = [.. Band("Band 1"), .. Band("Band 2"), .. Band("Band 3"), F("Output Level", "dB"), B("Process LFE")], // Wwise Parametric EQ
        [0x006A0003] = [F("Delay Time", "s"), F("Feedback", "%"), F("Wet/Dry Mix", "%"), F("Output Level", "dB"), B("Enable Feedback"), B("Process LFE")], // Wwise Delay
        [0x006C0003] = [F("Threshold", "dB"), F("Ratio"), F("Attack", "s"), F("Release", "s"), F("Output Gain", "dB"), B("Process LFE"), B("Channel Link")], // Wwise Compressor
        [0x006E0003] = [F("Threshold", "dB"), F("Ratio"), F("Look-ahead", "s"), F("Release", "s"), F("Output Gain", "dB"), B("Process LFE"), B("Channel Link")], // Wwise Peak Limiter
        [0x00760003] = // Wwise RoomVerb
        [
            F("Decay Time", "s"), F("HF Damping"), F("Diffusion", "%"), F("Stereo Width", "°"), .. RoomFilter(1), .. RoomFilter(2), .. RoomFilter(3),
            F("Front Level", "dB"), F("Rear Level", "dB"), F("Center Level", "dB"), F("LFE Level", "dB"), F("Dry Level", "dB"), F("ER Level", "dB"),
            F("Reverb Level", "dB"), B("Enable Early Reflections"), I("ER Pattern"), F("Reverb Delay", "ms"), F("Room Size", "%"), F("ER Front/Back Delay", "ms"),
            F("Density", "%"), F("Room Shape", "%"), I("Reverb Units"), B("Enable Tone Controls"),
            C("Filter 1 Insert", "Off", "ER Only", "Reverb Only", "ER and Reverb"), C("Filter 1 Curve", "Low Shelf", "Peaking", "High Shelf"),
            C("Filter 2 Insert", "Off", "ER Only", "Reverb Only", "ER and Reverb"), C("Filter 2 Curve", "Low Shelf", "Peaking", "High Shelf"),
            C("Filter 3 Insert", "Off", "ER Only", "Reverb Only", "ER and Reverb"), C("Filter 3 Curve", "Low Shelf", "Peaking", "High Shelf"),
            F("Input Center Level", "dB"), F("Input LFE Level", "dB"),
            F("Density Delay Min", "ms"), F("Density Delay Max", "ms"), F("Density Delay Random", "%"), F("Room Shape Min"), F("Room Shape Max"),
            F("Diffusion Delay Scale", "%"), F("Diffusion Delay Max", "ms"), F("Diffusion Delay Random", "%"), F("DC Filter Cut", "Hz"),
            F("Reverb Unit Input Delay", "ms"), F("Reverb Unit Input Delay Random", "%"),
        ],
        [0x007D0003] = // Wwise Flanger
        [
            F("Delay Time", "ms"), F("Dry Level"), F("Feed-forward Level"), F("Feedback Level"), F("Modulation Depth"), F("LFO Frequency", "Hz"),
            C("LFO Waveform", Waveforms), F("LFO Smoothing"), F("LFO Pulse Width"), F("Phase Offset", "°"),
            C("Phase Mode", "Left/Right", "Front/Rear", "Circular", "Random"), F("Phase Spread", "°"), F("Output Level", "dB"), F("Wet/Dry Mix", "%"),
            B("Enable LFO"), B("Process Center"), B("Process LFE"),
        ],
        [0x00810003] = // Wwise Meter
        [
            F("Attack", "s"), F("Release", "s"), F("Min", "dB"), F("Max", "dB"), F("Hold", "s"), B("Infinite Hold"), C8("Mode", "Peak", "RMS"),
            C8("Scope", "Global", "Game Object"), B("Apply Downstream Volume"), new("Game Parameter", T.Id),
        ],
        [0x00820003] = [I("Window Size"), F("Time Stretch", "%"), F("Time Stretch Random", "%"), F("Output Gain", "dB")], // Wwise Time Stretch
        [0x00870003] = // Wwise Stereo Delay
        [
            .. StereoChannel("Left"), .. StereoChannel("Right"), C("Filter", Filters), F("Filter Gain", "dB"), F("Filter Frequency", "Hz"), F("Filter Q"),
            F("Dry Level", "dB"), F("Wet Level", "dB"), F("Front/Rear Balance"), B("Enable Feedback"), B("Enable Cross-feed"),
        ],
        [0x008A0003] = // Wwise Harmonizer
        [
            .. Voice(1), .. Voice(2), C("Input", "As Input", "Center", "Stereo", "3.0", "4.0", "5.0", "Left Only"), F("Dry Level", "dB"),
            F("Wet Level", "dB"), I("Window Size"), B("Process LFE"), B("Sync Dry"),
        ],
        [0x008B0003] = [F("Gain", "dB"), F("LFE Gain", "dB")], // Wwise Gain
        [0x00880003] = // Wwise Pitch Shifter (worked out from one next-gen bank: the two switches are both off there)
        [
            C("Input", "As Input", "Center", "Stereo", "3.0", "4.0", "5.0", "Left Only"), F("Dry Level", "dB"), F("Wet Level", "dB"),
            F("Delay Time", "ms"), B("Process LFE"), B("Sync Dry"), F("Pitch", "cents"), C("Filter", Filters), F("Filter Gain", "dB"),
            F("Filter Frequency", "Hz"), F("Filter Q"),
        ],
    };

    /// <summary>Layouts an older Wwise kept for a plugin, by plugin and size: the Meter before it had Infinite Hold.</summary>
    private static readonly Dictionary<(long Plugin, long Size), Setting[]> OlderLayouts = new()
    {
        [(0x00810003, 27)] =
        [
            F("Attack", "s"), F("Release", "s"), F("Min", "dB"), F("Max", "dB"), F("Hold", "s"), C8("Mode", "Peak", "RMS"),
            C8("Scope", "Global", "Game Object"), B("Apply Downstream Volume"), new("Game Parameter", T.Id),
        ],
    };

    private static Setting[]? LayoutFor(long plugin, long size) =>
        OlderLayouts.TryGetValue((plugin, size), out var older) ? older
        : Layouts.TryGetValue(plugin, out var layout) && layout.Sum(SizeOf) == size ? layout : null;

    private static int SizeOf(Setting s) => s.Type is T.Flag or T.Choice8 ? 1 : 4;

    /// <summary>Whether a plugin's settings of this size can be read as named values.</summary>
    public static bool Known(long plugin, long size)
    {
        if (plugin == 0x00730003) return size >= 29;
        return LayoutFor(plugin, size) is not null;
    }

    /// <summary>Reads a plugin's settings block as named values under <paramref name="o"/>.</summary>
    public static void Read(BankNode o, long plugin, long size)
    {
        if (plugin == 0x00730003)
        {
            MatrixReverb(o, size);
            return;
        }
        foreach (var s in LayoutFor(plugin, size)!) Value(o, s);
    }

    private static void Value(BankNode o, Setting s)
    {
        BankField f = s.Type switch
        {
            T.Float => o.F32(s.Name),
            T.Int => o.U32(s.Name),
            T.Flag => o.U8(s.Name),
            T.Choice32 => o.U32(s.Name),
            T.Choice8 => o.U8(s.Name),
            _ => o.Tid(s.Name).Fnv(BankHash.GameParameter),
        };
        if (s.Choices is { } choices) f.Meaning = f.Long >= 0 && f.Long < choices.Length ? choices[f.Long] : $"{f.Long}";
        if (s.Unit.Length > 0) f.Unit = s.Unit;
    }

    /// <summary>Wwise Matrix Reverb: its settings, then a delay time for each delay line when they're set by hand.</summary>
    private static void MatrixReverb(BankNode o, long size)
    {
        o.F32("Reverb Time").Unit = "s";
        o.F32("HF Ratio");
        long delays = o.U32("Number of Delays").Long;
        o.F32("Dry Level").Unit = "dB";
        o.F32("Wet Level").Unit = "dB";
        o.F32("Pre-delay").Unit = "ms";
        o.U8("Process LFE");
        var mode = o.U32("Delay Lengths");
        mode.Meaning = mode.Long == 1 ? "Custom" : "Default";
        if (mode.Long == 1)
            for (int i = 1; i <= delays && size >= 29 + i * 4; i++) o.F32($"Delay {i}").Unit = "ms";
    }
}
// loc-ignore-end
