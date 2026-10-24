using System.IO;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Hdf5;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A voice line in a folder: its string ID, its sound (a .wem, or a .wav to make one from) and its lip sync, each a file or none.</summary>
public sealed record SpeechFolderLine(uint StringId, string? Wem, string? LipSync, string? Wav = null);

/// <summary>
/// Voice lines as loose files and back: a speech file unpacked into a folder, and a folder packed into a speech file.
/// The files are named as RedSpeech Tool names them, so a folder from either opens in the other: the string ID first
/// (anything after it is left for people to read), <c>.wem</c> for the sound, <c>.raw</c> for the lip sync as the
/// next-gen update (version 163) and the Remastered (164) keep it, and <c>.cr2w</c> for one as files before it (162)
/// keep it. A lip sync can also be a <c>.re</c>, as REDkit's Blender plugin edits them, which is made the game's own
/// when packed. A <c>.wav</c> with no <c>.wem</c> beside it is made one as it's packed, as the game's voice lines are
/// (<see cref="WemOpus"/>); one beside its <c>.wem</c> is a copy to listen to.
/// </summary>
public static class SpeechFolder
{
    /// <summary>
    /// Unpacks every line of <paramref name="speech"/> into <paramref name="folder"/>: <c>ID.wem</c>, and its lip sync as
    /// <c>ID.raw</c> or <c>ID.cr2w</c> (whichever it is), or with <paramref name="re"/> as <c>ID.re</c> for Blender, and with
    /// <paramref name="wav"/> each sound as <c>ID.wav</c> too. Returns how many lines, the lines whose sound couldn't be
    /// made a WAV, and those whose lip sync couldn't be made a <c>.re</c> (kept as they were).
    /// </summary>
    public static (int Lines, List<uint> NoWav, List<uint> NoRe) Unpack(W3Speech speech, string folder, bool wav, bool re = false,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var noWav = new List<uint>();
        var noRe = new List<uint>();
        var order = speech.Order;
        for (int i = 0; i < order.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, order.Count));
            uint id = order[i];
            if (speech.Voice(id) is { } voice)
            {
                File.WriteAllBytes(Path.Combine(folder, $"{id}.wem"), voice.Wem);
                if (wav)
                    try
                    {
                        var (samples, channels, rate) = SoundDecoder.Decode(voice.Wem, WemInfo.Read(voice.Wem));
                        File.WriteAllBytes(Path.Combine(folder, $"{id}.wav"), WwiseOpus.ToWav(samples, channels, rate));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
                    {
                        noWav.Add(id);
                    }
            }
            if (speech.LipSync(id) is not { } lipSync) continue;
            if (re)
                try
                {
                    File.WriteAllBytes(Path.Combine(folder, $"{id}.re"), Hdf5Writer.Write(LipSyncRe.ToRe(LipSyncFile.ReadAny(lipSync))));
                    continue;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
                {
                    noRe.Add(id);
                }
            File.WriteAllBytes(Path.Combine(folder, $"{id}{(LipSyncFile.IsCr2w(lipSync) ? ".cr2w" : ".raw")}"), lipSync);
        }
        progress?.Report((order.Count, order.Count));
        return (order.Count, noWav, noRe);
    }

    /// <summary>
    /// The voice lines a folder holds, by the string ID each file's name starts with: <c>.wem</c> sounds, and lip syncs
    /// as <c>.raw</c>, <c>.cr2w</c>, <c>.re</c> or a name that's only the ID, and <c>.wav</c> sounds with no <c>.wem</c>, to be
    /// made one. Notes say what was left out and why, which <c>.re</c> files have controls the face hasn't got, and how
    /// many sounds will be made from WAV.
    /// </summary>
    public static (List<SpeechFolderLine> Lines, List<string> Notes) Read(string folder)
    {
        var wems = new Dictionary<uint, string>();
        var lipSyncs = new Dictionary<uint, string>();
        var notes = new List<string>();
        var wavs = new Dictionary<uint, string>();
        foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file), ext = Path.GetExtension(file).ToLowerInvariant();
            int digits = 0;
            while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
            if (digits == 0 || !uint.TryParse(name.AsSpan(0, digits), out uint id))
            {
                if (ext is ".wem" or ".raw" or ".cr2w" or ".re") notes.Add(L.T("SpeechFolder.NoId", name));
                continue;
            }
            var into = ext switch
            {
                ".wem" => wems,
                ".raw" or ".cr2w" or ".re" => lipSyncs,
                "" when digits == name.Length => lipSyncs,
                _ => null,
            };
            if (ext == ".wav") wavs.TryAdd(id, file);
            if (into is null) continue;
            if (!into.TryAdd(id, file)) notes.Add(L.T("SpeechFolder.TwoFiles", id, Path.GetFileName(into[id]), name));
        }
        // a .re is checked now, so one that won't pack is said before anything is written
        foreach (var (id, file) in lipSyncs.Where(p => IsRe(p.Value)).ToList())
            try
            {
                var (_, unknown) = LipSyncRe.FromRe(Hdf5Reader.Read(File.ReadAllBytes(file)));
                if (unknown.Count > 0) notes.Add(L.T("SpeechFolder.ReUnknownControls", Path.GetFileName(file), string.Join(", ", unknown.Take(8)) + (unknown.Count > 8 ? ", …" : "")));
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
            {
                notes.Add(L.T("SpeechFolder.ReNotRead", Path.GetFileName(file), ex.Message));
                lipSyncs.Remove(id);
            }
        // a .wav beside its line's .wem is a copy to listen to; one without is the line's sound, to be made a .wem, and is
        // read now so one that can't be is said before anything is written
        foreach (uint id in wems.Keys) wavs.Remove(id);
        foreach (var (id, file) in wavs.ToList())
            try
            {
                var sound = WavFile.Read(File.ReadAllBytes(file));
                if (sound.Channels is not (1 or 2 or 6)) throw new NotSupportedException(L.T("WemOpus.ChannelsNotMade", sound.Channels));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
            {
                notes.Add(L.T("SpeechFolder.WavNotRead", Path.GetFileName(file), ex.Message));
                wavs.Remove(id);
            }
        if (wavs.Count > 0) notes.Add(L.T("SpeechFolder.WavsMade", L.T("Count.VoiceLine", wavs.Count)));
        foreach (uint id in lipSyncs.Keys.Where(id => !wems.ContainsKey(id) && !wavs.ContainsKey(id))) notes.Add(L.T("SpeechFolder.LipSyncWithoutSound", id));
        var lines = wems.Keys.Concat(wavs.Keys).Order()
            .Select(id => new SpeechFolderLine(id, wems.GetValueOrDefault(id), lipSyncs.GetValueOrDefault(id), wavs.GetValueOrDefault(id))).ToList();
        return (lines, notes);
    }

    /// <summary>
    /// Packs <paramref name="lines"/> into a speech file of <paramref name="language"/> at <paramref name="output"/>, with
    /// any lines of <paramref name="startFrom"/> (an existing speech file) the folder hasn't got kept as they were.
    /// Written to a new file first, so <paramref name="startFrom"/> may be the output itself. A line's <c>.wav</c> is made
    /// a .wem at <paramref name="kbps"/> a channel first, several at once.
    /// </summary>
    public static int Pack(IReadOnlyList<SpeechFolderLine> lines, W3Language language, string output, W3Speech? startFrom = null,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default, int kbps = WemOpus.DefaultKbpsPerChannel) =>
        Pack(lines, language, output, startFrom, null, progress, cancel, kbps).Lines;

    /// <summary>
    /// <see cref="Pack(IReadOnlyList{SpeechFolderLine}, W3Language, string, W3Speech?, IProgress{ValueTuple{int, int}}?, CancellationToken, int)"/>,
    /// and with <paramref name="lipSyncs"/> each line with no lip sync gets one made from its sound. Returns how many
    /// lines the speech file has and how many lip syncs were made.
    /// </summary>
    public static (int Lines, int LipSyncsMade) Pack(IReadOnlyList<SpeechFolderLine> lines, W3Language language, string output, W3Speech? startFrom,
        LipSyncModel? lipSyncs, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default, int kbps = WemOpus.DefaultKbpsPerChannel)
    {
        var made = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte[]>();
        var madeLipSyncs = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte[]>();
        var parallel = new ParallelOptions { CancellationToken = cancel };
        Parallel.ForEach(lines.Where(l => l.Wem is null && l.Wav is not null), parallel,
            l => made[l.StringId] = WemOpus.FromWav(File.ReadAllBytes(l.Wav!), kbps, cancel));
        if (lipSyncs is not null)
            Parallel.ForEach(lines.Where(l => l.LipSync is null && (l.Wem is not null || l.Wav is not null)), parallel, l =>
            {
                // from the recording when there's one, else from the sound the line has
                (short[] samples, int channels) sound;
                if (l.Wav is not null)
                {
                    var wav = WavFile.Read(File.ReadAllBytes(l.Wav)).Resampled(WemOpus.SampleRate);
                    sound = (wav.Samples, wav.Channels);
                }
                else sound = WwiseOpus.Decode(File.ReadAllBytes(l.Wem!));
                madeLipSyncs[l.StringId] = lipSyncs.Make(sound.samples, sound.channels).Write();
            });
        var all = new Dictionary<uint, SpeechLine>();
        if (startFrom is not null)
            foreach (uint id in startFrom.Order) all[id] = new SpeechLine(id, () => startFrom.Voice(id), () => startFrom.LipSync(id));
        foreach (var line in lines)
            all[line.StringId] = new SpeechLine(line.StringId,
                () => line.Wem is not null ? Voice(File.ReadAllBytes(line.Wem)) : made.TryGetValue(line.StringId, out var wem) ? Voice(wem) : null,
                () => line.LipSync is not null ? LipSync(line.LipSync) : madeLipSyncs.GetValueOrDefault(line.StringId));
        string temp = output + ".writing";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                W3SpeechWriter.Write(stream, language, all.Values.ToList(), W3SpeechWriter.Version, progress, cancel);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return (all.Count, madeLipSyncs.Count);
    }

    /// <summary>A lip sync file as a speech file keeps it: a <c>.re</c> made the game's own, any other as it is.</summary>
    public static byte[] LipSync(string file) =>
        IsRe(file) ? LipSyncRe.FromRe(Hdf5Reader.Read(File.ReadAllBytes(file))).LipSync.Write() : File.ReadAllBytes(file);

    private static bool IsRe(string file) => Path.GetExtension(file).Equals(".re", StringComparison.OrdinalIgnoreCase);

    /// <summary>A .wem as a speech file keeps it, with the length in seconds and the 1 the game's lines have.</summary>
    public static SpeechVoice Voice(byte[] wem) => new(wem, W3SpeechWriter.Duration(wem), W3SpeechWriter.Compression);

    /// <summary>The language a speech file's name says (<c>enpc.w3speech</c> is English), or null.</summary>
    public static W3Language? LanguageOf(string path)
    {
        string name = Path.GetFileName(path);
        int at = name.IndexOf("pc.w3speech", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? W3Strings.Language(name[..at]) : null;
    }
}
