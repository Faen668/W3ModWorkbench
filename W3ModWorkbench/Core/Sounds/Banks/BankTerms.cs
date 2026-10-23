// loc-ignore-start
// The names here are the settings as Wwise's authoring tool shows them, in English, so they can be found there when a
// bank is made again; like the game's own property names they're left untranslated.
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>What the numbers in a bank mean: properties, actions, curves, plugins and the small lists of choices.</summary>
internal static class BankTerms
{
    // ---------------------------------------------------------------- properties

    /// <summary>The properties up to 89 (the original game's banks), by number.</summary>
    private static readonly string[] Properties89 =
    [
        "Volume", "LFE Volume", "Pitch", "Low-pass Filter", "Bus Volume", "Priority", "Priority Distance Offset", "Loop Count",
        "Motion Volume", "Motion Low-pass", "Mute Ratio", "Pan Left/Right", "Pan Front/Back", "Center %", "Delay", "Transition Time",
        "Probability", "Dialogue Mode", "User Send Volume 1", "User Send Volume 2", "User Send Volume 3", "User Send Volume 4",
        "Game Send Volume", "Output Bus Volume", "Output Bus Low-pass", "Initial Delay", "HDR Threshold", "HDR Ratio",
        "HDR Release Time", "HDR Game Parameter", "HDR Game Parameter Min", "HDR Game Parameter Max", "HDR Active Range",
        "Make-Up Gain", "Loop Start", "Loop End", "Trim In", "Trim Out", "Fade-In Time", "Fade-Out Time", "Fade-In Curve",
        "Fade-Out Curve", "Loop Crossfade Duration", "Crossfade Up Curve", "Crossfade Down Curve",
    ];

    /// <summary>The properties from 127 to 145 (the next-gen update's banks are 140).</summary>
    private static readonly string[] Properties145 =
    [
        "Volume", "LFE Volume", "Pitch", "Low-pass Filter", "High-pass Filter", "Bus Volume", "Make-Up Gain", "Priority",
        "Priority Distance Offset", "Motion Volume", "Motion Low-pass", "Mute Ratio", "Pan Left/Right", "Pan Front/Back", "Center %",
        "Delay", "Transition Time", "Probability", "Dialogue Mode", "User Send Volume 1", "User Send Volume 2", "User Send Volume 3",
        "User Send Volume 4", "Game Send Volume", "Output Bus Volume", "Output Bus High-pass", "Output Bus Low-pass", "HDR Threshold",
        "HDR Ratio", "HDR Release Time", "HDR Game Parameter", "HDR Game Parameter Min", "HDR Game Parameter Max", "HDR Active Range",
        "Loop Start", "Loop End", "Trim In", "Trim Out", "Fade-In Time", "Fade-Out Time", "Fade-In Curve", "Fade-Out Curve",
        "Loop Crossfade Duration", "Crossfade Up Curve", "Crossfade Down Curve", "MIDI Root Note", "MIDI Play On", "MIDI Transposition",
        "MIDI Velocity Offset", "MIDI Key Range Min", "MIDI Key Range Max", "MIDI Velocity Range Min", "MIDI Velocity Range Max",
        "MIDI Channels", "Playback Speed", "MIDI Tempo Source", "MIDI Target", "Attached Effect", "Loop Count", "Initial Delay",
        "User Send Low-pass 1", "User Send Low-pass 2", "User Send Low-pass 3", "User Send Low-pass 4", "User Send High-pass 1",
        "User Send High-pass 2", "User Send High-pass 3", "User Send High-pass 4", "Game Send Low-pass", "Game Send High-pass",
        "Attenuation", "3D Blend", "Reflections Volume", "Pan Up/Down",
    ];

    /// <summary>The properties from 146 to 150 (the Remastered's banks).</summary>
    private static readonly string[] Properties150 =
    [
        "Volume", "Pitch", "Low-pass Filter", "High-pass Filter", "Bus Volume", "Make-Up Gain", "Priority", "Mute Ratio",
        "User Send Volume 1", "User Send Volume 2", "User Send Volume 3", "User Send Volume 4", "Game Send Volume",
        "Output Bus Volume", "Output Bus High-pass", "Output Bus Low-pass", "User Send Low-pass 1", "User Send Low-pass 2",
        "User Send Low-pass 3", "User Send Low-pass 4", "User Send High-pass 1", "User Send High-pass 2", "User Send High-pass 3",
        "User Send High-pass 4", "Game Send Low-pass", "Game Send High-pass", "Reflections Volume", "HDR Threshold", "HDR Ratio",
        "HDR Release Time", "HDR Active Range", "MIDI Transposition", "MIDI Velocity Offset", "Playback Speed", "Initial Delay",
        "2D Pan X", "2D Pan Y", "2D Pan Z", "3D Pan X", "3D Pan Y", "3D Pan Z", "Center %", "3D Blend", "Enable Attenuation",
        "Cone Attenuation On", "Cone Attenuation", "Cone Low-pass", "Cone High-pass", "Bypass Effect", "Bypass All Effects",
        "Spare 1", "Spare 2", "Spare 3", "Max Instances", "Bypass All Metadata", "Special Transitions", "Priority Distance Offset",
        "Delay", "Transition Time", "Probability", "Dialogue Mode", "HDR Game Parameter", "HDR Game Parameter Min",
        "HDR Game Parameter Max", "Loop Start", "Loop End", "Trim In", "Trim Out", "Fade-In Time", "Fade-Out Time", "Fade-In Curve",
        "Fade-Out Curve", "Loop Crossfade Duration", "Crossfade Up Curve", "Crossfade Down Curve", "MIDI Root Note", "MIDI Play On",
        "MIDI Key Range Min", "MIDI Key Range Max", "MIDI Velocity Range Min", "MIDI Velocity Range Max", "MIDI Channels",
        "MIDI Tempo Source", "MIDI Target", "Loop Count", "Attenuation",
    ];

    /// <summary>A property's name in a bank of this version, or null when its version's list isn't known.</summary>
    public static string? Property(int version, long id)
    {
        string[]? list = version switch { <= 89 => Properties89, >= 127 and <= 145 => Properties145, >= 146 and <= 150 => Properties150, _ => null };
        return list is not null && id >= 0 && id < list.Length ? list[id] : null;
    }

    /// <summary>Properties whose value is an object's ID rather than a number.</summary>
    public static bool PropertyIsId(string? name) => name is "Attenuation" or "Attached Effect" or "MIDI Target";

    /// <summary>The unit a property's value is in, for showing it.</summary>
    public static string Unit(string? name) => name switch
    {
        null => "",
        _ when name.EndsWith("Volume", StringComparison.Ordinal) || name == "Make-Up Gain" || name == "Cone Attenuation" || name.StartsWith("HDR Threshold", StringComparison.Ordinal) => " dB",
        "Pitch" => " cents",
        "Delay" or "Initial Delay" or "Transition Time" or "Fade-In Time" or "Fade-Out Time" or "Loop Crossfade Duration" or "HDR Release Time" => " ms",
        _ => "",
    };

    // ---------------------------------------------------------------- what game parameters, states and modulators change

    /// <summary>What a game parameter curve or a state can change, up to 89.</summary>
    private static readonly Dictionary<long, string> Targets89 = new()
    {
        [0x00] = "Volume", [0x01] = "LFE Volume", [0x02] = "Pitch", [0x03] = "Low-pass Filter", [0x04] = "Bus Volume",
        [0x05] = "Special Transitions", [0x06] = "Initial Delay", [0x08] = "Priority", [0x09] = "Max Instances", [0x0A] = "Positioning Type",
        [0x0B] = "Center %", [0x0C] = "Cone Attenuation On", [0x0D] = "Cone Attenuation", [0x0E] = "Cone Low-pass",
        [0x0F] = "User Send Volume 1", [0x10] = "User Send Volume 2", [0x11] = "User Send Volume 3", [0x12] = "User Send Volume 4",
        [0x13] = "Game Send Volume", [0x14] = "2D Pan X", [0x15] = "2D Pan Y", [0x16] = "Output Bus Volume", [0x17] = "Output Bus Low-pass",
        [0x18] = "Bypass Effect 1", [0x19] = "Bypass Effect 2", [0x1A] = "Bypass Effect 3", [0x1B] = "Bypass Effect 4", [0x1C] = "Bypass All Effects",
        [0x1D] = "Motion Volume", [0x1E] = "Motion Low-pass", [0x1F] = "Motion Pitch", [0x20] = "HDR Threshold", [0x21] = "HDR Release Time",
        [0x22] = "HDR Ratio", [0x23] = "HDR Active Range", [0x24] = "Make-Up Gain", [0x25] = "3D Pan X", [0x26] = "3D Pan Y",
    };

    /// <summary>What a game parameter curve or a state can change, from 135 on.</summary>
    private static readonly Dictionary<long, string> Targets135 = new()
    {
        [0x00] = "Volume", [0x01] = "LFE Volume", [0x02] = "Pitch", [0x03] = "Low-pass Filter", [0x04] = "High-pass Filter", [0x05] = "Bus Volume",
        [0x06] = "Initial Delay", [0x07] = "Make-Up Gain", [0x08] = "Motion Volume", [0x09] = "Motion Low-pass", [0x0A] = "Motion Pitch",
        [0x0B] = "MIDI Transposition", [0x0C] = "MIDI Velocity Offset", [0x0D] = "Playback Speed", [0x0E] = "Mute Ratio",
        [0x0F] = "Special Transitions", [0x10] = "Max Instances", [0x11] = "Priority", [0x12] = "2D Pan X", [0x13] = "2D Pan Y",
        [0x14] = "3D Pan X", [0x15] = "3D Pan Y", [0x16] = "3D Pan Z", [0x17] = "3D Blend", [0x18] = "Center %", [0x19] = "Cone Attenuation On",
        [0x1A] = "Cone Attenuation", [0x1B] = "Cone Low-pass", [0x1C] = "Cone High-pass", [0x1D] = "Bypass Effect 1", [0x1E] = "Bypass Effect 2",
        [0x1F] = "Bypass Effect 3", [0x20] = "Bypass Effect 4", [0x21] = "Bypass All Effects", [0x22] = "HDR Threshold", [0x23] = "HDR Release Time",
        [0x24] = "HDR Ratio", [0x25] = "HDR Active Range", [0x26] = "Game Send Volume", [0x27] = "User Send Volume 1", [0x28] = "User Send Volume 2",
        [0x29] = "User Send Volume 3", [0x2A] = "User Send Volume 4", [0x2B] = "Output Bus Volume", [0x2C] = "Output Bus High-pass",
        [0x2D] = "Output Bus Low-pass", [0x2E] = "Enable Attenuation", [0x2F] = "Reflections Volume", [0x30] = "User Send Low-pass 1",
        [0x31] = "User Send Low-pass 2", [0x32] = "User Send Low-pass 3", [0x33] = "User Send Low-pass 4", [0x34] = "User Send High-pass 1",
        [0x35] = "User Send High-pass 2", [0x36] = "User Send High-pass 3", [0x37] = "User Send High-pass 4", [0x38] = "Game Send Low-pass",
        [0x39] = "Game Send High-pass", [0x3A] = "2D Pan Z", [0x3B] = "Bypass All Metadata",
    };

    public static string? Target(int version, long id) => version switch
    {
        <= 89 => Targets89.GetValueOrDefault(id),
        >= 135 => Targets135.GetValueOrDefault(id),
        _ => null,
    };

    /// <summary>A modulator's (LFO, envelope, time modulator's) own settings.</summary>
    public static string? ModulatorSetting(int version, long id)
    {
        string[] list = version <= 145
            ? ["Scope", "Stop Playback", "Depth", "Attack", "Frequency", "Waveform", "Smoothing", "Pulse Width", "Initial Phase",
               "Attack Time", "Attack Curve", "Decay Time", "Sustain Level", "Sustain Time", "Release Time", "Trigger On",
               "Duration", "Loops", "Playback Rate", "Initial Delay"]
            : ["Scope", "Stop Playback", "Depth", "Attack", "Frequency", "Waveform", "Smoothing", "Pulse Width", "Initial Phase",
               "Retrigger", "Attack Time", "Attack Curve", "Decay Time", "Sustain Level", "Sustain Time", "Release Time", "Trigger On",
               "Duration", "Loops", "Playback Rate", "Initial Delay"];
        return id >= 0 && id < list.Length ? list[id] : null;
    }

    /// <summary>What a modulator's own game parameter curves can change.</summary>
    public static readonly Dictionary<long, string> ModulatorTargets = new()
    {
        [0] = "Depth", [1] = "Attack", [2] = "Frequency", [3] = "Waveform", [4] = "Smoothing", [5] = "Pulse Width", [6] = "Initial Phase",
        [7] = "Attack Time", [8] = "Decay Time", [9] = "Sustain Level", [10] = "Sustain Time", [11] = "Release Time",
    };

    // ---------------------------------------------------------------- actions

    /// <summary>An action's kind, from the top byte of its type.</summary>
    public static string Action(long type, int version)
    {
        long kind = (type >> 8) & 0xFF;
        if (version >= 150 && kind == 0x1A) return "Break";
        if (version >= 150 && kind == 0x1B) return "Trigger";
        string name = kind switch
        {
            0x01 => "Stop", 0x02 => "Pause", 0x03 => "Resume", 0x04 => "Play", 0x05 => "Play and Continue", 0x06 => "Mute", 0x07 => "Unmute",
            0x08 => "Set Pitch", 0x09 => "Reset Pitch", 0x0A => "Set Voice Volume", 0x0B => "Reset Voice Volume", 0x0C => "Set Bus Volume",
            0x0D => "Reset Bus Volume", 0x0E => "Set Low-pass Filter", 0x0F => "Reset Low-pass Filter", 0x10 => "Enable State",
            0x11 => "Disable State", 0x12 => "Set State", 0x13 => "Set Game Parameter", 0x14 => "Reset Game Parameter", 0x15 => "Stop Event",
            0x16 => "Pause Event", 0x17 => "Resume Event", 0x18 => "Duck", 0x19 => "Set Switch", 0x1A => "Bypass Effect",
            0x1B => "Reset Bypass Effect", 0x1C => "Break", 0x1D => "Trigger", 0x1E => "Seek", 0x1F => "Release Envelope",
            0x20 => "Set High-pass Filter", 0x21 => "Play Event", 0x22 => "Reset Playlist", 0x30 => "Reset High-pass Filter",
            0x31 => "Set Effect", 0x32 => "Reset Effect", 0x33 => "Bypass Effect Slot", 0x34 => "Reset Effect Slot Bypass",
            0x35 => "Bypass All Effects", 0x36 => "Reset Bypass All Effects", 0x37 => "Reset All Effect Bypasses", 0x40 => "Nothing",
            _ => $"Action {type:X4}",
        };
        // the low byte: what it acts on, and whether only on the game object that posted the event
        string scope = (type & 0xFF) switch
        {
            0x03 or 0x01 when kind is not (0x04 or 0x05 or 0x21) => " (game object)",
            0x04 => " (all)",
            0x05 => " (all, game object)",
            0x08 => " (all but exceptions)",
            0x09 => " (all but exceptions, game object)",
            _ => "",
        };
        return name + scope;
    }

    // ---------------------------------------------------------------- the small lists

    public static string Curve(long id) => id switch
    {
        0 => "Log (base 3)", 1 => "Sine", 2 => "Log (base 1.41)", 3 => "Inverted S", 4 => "Linear", 5 => "S-curve", 6 => "Exp (base 1.41)",
        7 => "Reciprocal Sine", 8 => "Exp (base 3)", 9 => "Constant", _ => $"{id}",
    };

    public static string CurveScaling(long id) => id switch { 0 => "None", 2 => "dB", 3 => "Log", 4 => "dB to Linear", _ => $"{id}" };
    public static string Sync(long id) => id switch
    {
        0 => "Immediate", 1 => "Next Grid", 2 => "Next Bar", 3 => "Next Beat", 4 => "Next Cue", 5 => "Next Custom Cue", 6 => "Entry Cue",
        7 => "Exit Cue", 8 => "Never", 9 => "Last Exit Position", _ => $"{id}",
    };
    public static string ContainerMode(long id) => id == 1 ? "Sequence" : "Random";
    public static string RandomMode(long id) => id == 1 ? "Shuffle" : "Standard";
    public static string Transition(long id) => id switch
    {
        0 => "None", 1 => "Crossfade (amp)", 2 => "Crossfade (power)", 3 => "Delay", 4 => "Sample Accurate", 5 => "Trigger Rate", _ => $"{id}",
    };
    public static string GroupKind(long id) => id == 1 ? "State" : "Switch";
    public static string OnSwitch(long id) => id == 1 ? "Stop" : "Play to End";
    public static string VirtualResume(long id) => id switch { 0 => "Play from Beginning", 1 => "Play from Elapsed Time", 2 => "Resume", _ => $"{id}" };
    public static string BelowThreshold(long id) => id switch
    {
        0 => "Continue to Play", 1 => "Kill Voice", 2 => "Send to Virtual Voice", 3 => "Kill if One-shot, else Virtual", _ => $"{id}",
    };
    public static string PathMode(long id) => id switch
    {
        0 => "Step Sequence", 1 => "Step Random", 2 => "Continuous Sequence", 3 => "Continuous Random", 4 => "Step Sequence, New Path",
        5 => "Step Random, New Path", _ => $"{id}",
    };
    public static string Positioning89(long id) => id switch { 1 => "2D", 2 => "3D, Defined by the Sound", 3 => "3D, Defined by the Game", _ => "None" };
    public static string Position3d(long id) => id switch { 0 => "Emitter", 1 => "Emitter with Automation", 2 => "Listener with Automation", _ => $"{id}" };
    public static string Spatialisation(long id) => id switch { 0 => "None", 1 => "Position", 2 => "Position and Orientation", _ => $"{id}" };
    public static string Panner(long id) => id switch { 0 => "Direct Speaker Assignment", 1 => "Balance-Fade", 2 => "Steering", _ => $"{id}" };
    public static string CurveKind(int version, long id) => version <= 143
        ? id switch { 0 => "Game Parameter", 1 => "MIDI", 2 => "Modulator", _ => $"{id}" }
        : id switch { 0 => "Game Parameter", 1 => "MIDI", 2 => "Switch", 3 => "State", 4 => "Modulator", _ => $"{id}" };
    public static string Blend(int version, long id) => version <= 125
        ? id switch { 0 => "Exclusive", 1 => "Additive", 2 => "Multiply", _ => $"{id}" }
        : id switch { 0 => "None", 1 => "Exclusive", 2 => "Additive", 3 => "Multiply", 4 => "Boolean", 5 => "Maximum", 6 => "Filter", _ => $"{id}" };
    public static string ValueMeaning(long id) => id switch { 0 => "Default", 1 => "Absolute", 2 => "Relative", _ => $"{id}" };
    public static string TrackKind(long id) => id switch { 0 => "Normal", 1 => "Random Step", 2 => "Sequence Step", 3 => "Switch", _ => $"{id}" };
    public static string PlaylistKind(long id) => id switch
    {
        -1 => "None", 0 => "Sequence Continuous", 1 => "Sequence Step", 2 => "Random Continuous", 3 => "Random Step", _ => $"{id}",
    };
    public static string TreeMode(long id) => id == 1 ? "Weighted" : "Best Match";
    public static string JumpTo(long id) => id switch { 0 => "Start of Playlist", 1 => "Specific Item", 2 => "Last Played Segment", 3 => "Next Segment", _ => $"{id}" };
    public static string EntryAt(long id) => id switch { 0 => "Entry Cue", 1 => "Same Time as Playing Segment", 2 => "Random Cue", 3 => "Random Custom Cue", 4 => "Last Exit Position", _ => $"{id}" };
    public static string ClipAutomation(int version, long id) => version <= 89
        ? id switch { 0 => "Volume", 1 => "Low-pass", 2 => "Fade In", 3 => "Fade Out", _ => $"{id}" }
        : id switch { 0 => "Volume", 1 => "Low-pass", 2 => "High-pass", 3 => "Fade In", 4 => "Fade Out", _ => $"{id}" };
    public static string ChannelConfig(long id) => id switch { 0 => "Anonymous", 1 => "Standard", 2 => "Ambisonic", 3 => "Objects", 14 => "Main Device", 15 => "Passthrough", _ => $"{id}" };
    public static string BankKind(long id) => id switch { 0 => "User", 30 => "Event", 31 => "Bus", _ => $"{id}" };

    /// <summary>How a source is kept: in the bank, or streamed, or streamed with its start in the bank.</summary>
    public static string Storage(BankFormat format, long id) => id == 0 ? "In Bank" : id == format.StreamedCode ? "Streamed" : "Prefetched";

    // ---------------------------------------------------------------- plugins

    /// <summary>
    /// A plugin's number: bits 0-3 its kind (codec, source, effect…), 4-15 its maker (0 is Audiokinetic), 16-31 which one.
    /// </summary>
    public static string PluginKind(long id) => (id & 0xF) switch
    {
        0 => "None", 1 => "Codec", 2 => "Source", 3 => "Effect", 4 => "Motion Device", 5 => "Motion Source", 6 => "Mixer", 7 => "Output",
        8 => "Extension", 9 => "Metadata", _ => $"{id & 0xF}",
    };

    /// <summary>Audiokinetic's own codecs, sources and effects, by their numbers.</summary>
    private static readonly Dictionary<long, string> Plugins = new()
    {
        [0x00000001] = "Bank", [0x00010001] = "PCM", [0x00020001] = "ADPCM", [0x00030001] = "XMA", [0x00040001] = "Vorbis",
        [0x00050001] = "Wii ADPCM", [0x00070001] = "PCM", [0x00080001] = "External Source", [0x00090001] = "xWMA", [0x000A0001] = "AAC",
        [0x000C0001] = "ATRAC9", [0x000D0001] = "VAG", [0x00100001] = "MIDI", [0x00110001] = "Opus (Switch)", [0x00130001] = "Opus",
        [0x00140001] = "Opus", [0x00150001] = "Opus",
        [0x00640002] = "Wwise Sine", [0x00650002] = "Wwise Silence", [0x00660002] = "Wwise Tone Generator",
        [0x00690003] = "Wwise Parametric EQ", [0x006A0003] = "Wwise Delay", [0x006C0003] = "Wwise Compressor", [0x006D0003] = "Wwise Expander",
        [0x006E0003] = "Wwise Peak Limiter", [0x00730003] = "Wwise Matrix Reverb", [0x00740003] = "SoundSeed Impact", [0x00760003] = "Wwise RoomVerb",
        [0x00770002] = "SoundSeed Air Wind", [0x00780002] = "SoundSeed Air Woosh", [0x007D0003] = "Wwise Flanger",
        [0x007E0003] = "Wwise Guitar Distortion", [0x007F0003] = "Wwise Convolution Reverb", [0x00810003] = "Wwise Meter",
        [0x00820003] = "Wwise Time Stretch", [0x00830003] = "Wwise Tremolo", [0x00840003] = "Wwise Recorder", [0x00870003] = "Wwise Stereo Delay",
        [0x00880003] = "Wwise Pitch Shifter", [0x008A0003] = "Wwise Harmonizer", [0x008B0003] = "Wwise Gain", [0x00940002] = "Wwise Synth One",
        [0x00AB0003] = "Wwise Reflect", [0x00B70002] = "SoundSeed Grain", [0x00BA0003] = "Mastering Suite", [0x00C80002] = "Wwise Audio Input",
        [0x00AE0007] = "System Output", [0x00B00007] = "Communication Output", [0x00B10007] = "Controller Headphones",
        [0x00B30007] = "Controller Speaker", [0x00B50007] = "No Output",
        [0x01950002] = "Wwise Motion Generator", [0x01990002] = "Wwise Motion Source", [0x01FB0007] = "Motion Output",
    };

    /// <summary>A plugin's name, when it's one of Audiokinetic's own; else its kind and number.</summary>
    public static string Plugin(long id) => id <= 0 ? "None" : Plugins.TryGetValue(id, out var name) ? name : $"{PluginKind(id)} {id:X8}";

    /// <summary>The kind of object a type number is, in the later numbering (old banks' are moved to it first).</summary>
    public static int ModernType(int type, BankFormat format) => format.OldTypeNumbers && type >= 0x12 ? type - 2 : type;
}
// loc-ignore-end
