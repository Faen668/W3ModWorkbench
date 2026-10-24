using System.Buffers.Binary;
using System.IO;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A voice line in a folder: its string ID, its sound (a .wem) and its lip sync, each a file or none.</summary>
public sealed record SpeechFolderLine(uint StringId, string? Wem, string? LipSync);

/// <summary>
/// Voice lines as loose files and back: a speech file unpacked into a folder, and a folder packed into a speech file.
/// The files are named as RedSpeech Tool names them, so a folder from either opens in the other: the string ID first
/// (anything after it is left for people to read), <c>.wem</c> for the sound, <c>.raw</c> for the lip sync as the
/// Remastered keeps it and <c>.cr2w</c> for one as older files keep it.
/// </summary>
public static class SpeechFolder
{
    /// <summary>
    /// Unpacks every line of <paramref name="speech"/> into <paramref name="folder"/>: <c>ID.wem</c>, and its lip sync as
    /// <c>ID.raw</c> or <c>ID.cr2w</c> (whichever it is), and with <paramref name="wav"/> each sound as <c>ID.wav</c> too.
    /// Returns how many lines, and the lines whose sound couldn't be made a WAV.
    /// </summary>
    public static (int Lines, List<uint> NoWav) Unpack(W3Speech speech, string folder, bool wav,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var noWav = new List<uint>();
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
            if (speech.LipSync(id) is { } lipSync)
                File.WriteAllBytes(Path.Combine(folder, $"{id}{(IsCr2w(lipSync) ? ".cr2w" : ".raw")}"), lipSync);
        }
        progress?.Report((order.Count, order.Count));
        return (order.Count, noWav);
    }

    private static bool IsCr2w(byte[] data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x57325243; // "CR2W"

    /// <summary>
    /// The voice lines a folder holds, by the string ID each file's name starts with: <c>.wem</c> sounds, and lip syncs
    /// as <c>.raw</c>, <c>.cr2w</c> or a name that's only the ID. Notes say what was left out and why.
    /// </summary>
    public static (List<SpeechFolderLine> Lines, List<string> Notes) Read(string folder)
    {
        var wems = new Dictionary<uint, string>();
        var lipSyncs = new Dictionary<uint, string>();
        var notes = new List<string>();
        var wavs = new HashSet<uint>();
        foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file), ext = Path.GetExtension(file).ToLowerInvariant();
            int digits = 0;
            while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
            if (digits == 0 || !uint.TryParse(name.AsSpan(0, digits), out uint id))
            {
                if (ext is ".wem" or ".raw" or ".cr2w") notes.Add(L.T("SpeechFolder.NoId", name));
                continue;
            }
            var into = ext switch
            {
                ".wem" => wems,
                ".raw" or ".cr2w" => lipSyncs,
                "" when digits == name.Length => lipSyncs,
                _ => null,
            };
            if (ext == ".wav") wavs.Add(id);
            if (into is null) continue;
            if (!into.TryAdd(id, file)) notes.Add(L.T("SpeechFolder.TwoFiles", id, Path.GetFileName(into[id]), name));
        }
        // a .wav beside its line's .wem is a copy to listen to; one without is a sound still to be made a .wem
        wavs.ExceptWith(wems.Keys);
        if (wavs.Count > 0) notes.Add(L.T("SpeechFolder.WavsLeft", L.T("Count.File", wavs.Count)));
        foreach (uint id in lipSyncs.Keys.Where(id => !wems.ContainsKey(id))) notes.Add(L.T("SpeechFolder.LipSyncWithoutSound", id));
        var lines = wems.Keys.Order().Select(id => new SpeechFolderLine(id, wems[id], lipSyncs.GetValueOrDefault(id))).ToList();
        return (lines, notes);
    }

    /// <summary>
    /// Packs <paramref name="lines"/> into a speech file of <paramref name="language"/> at <paramref name="output"/>, with
    /// any lines of <paramref name="startFrom"/> (an existing speech file) the folder hasn't got kept as they were.
    /// Written to a new file first, so <paramref name="startFrom"/> may be the output itself.
    /// </summary>
    public static int Pack(IReadOnlyList<SpeechFolderLine> lines, W3Language language, string output, W3Speech? startFrom = null,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var all = new Dictionary<uint, SpeechLine>();
        if (startFrom is not null)
            foreach (uint id in startFrom.Order) all[id] = new SpeechLine(id, () => startFrom.Voice(id), () => startFrom.LipSync(id));
        foreach (var line in lines)
            all[line.StringId] = new SpeechLine(line.StringId,
                () => line.Wem is null ? null : Voice(File.ReadAllBytes(line.Wem)),
                () => line.LipSync is null ? null : File.ReadAllBytes(line.LipSync));
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
        return all.Count;
    }

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
