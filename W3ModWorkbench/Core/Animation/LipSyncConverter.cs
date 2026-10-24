using System.IO;
using W3ModWorkbench.Core.Hdf5;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>The forms a lip sync can be converted to: the game's own (raw) or REDkit's <c>.re</c> for Blender.</summary>
public enum LipSyncFormat { Raw, Re }

/// <summary>What happened to one lip sync: the file written, or why none was (a note when it was passed over, an error when it failed).</summary>
public sealed record LipSyncConversion(string Input, string? Output, string? Note = null, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Lip sync files from one form to another: the game's raw form (<c>.raw</c>), older speech files' CR2W
/// (<c>.cr2w</c>) and REDkit's <c>.re</c>, to raw or <c>.re</c>. Each is read by what it holds, not its name.
/// </summary>
public static class LipSyncConverter
{
    public static readonly string[] Extensions = [".raw", ".cr2w", ".re"];

    public static string Extension(LipSyncFormat format) => format == LipSyncFormat.Re ? ".re" : ".raw";

    /// <summary>The lip sync files among <paramref name="paths"/>: files as given, and the lip syncs in folders (and their subfolders with <paramref name="recursive"/>), each with the folder it was found under.</summary>
    public static List<(string File, string? Root)> Inputs(IEnumerable<string> paths, bool recursive)
    {
        var found = new List<(string, string?)>();
        foreach (string path in paths)
        {
            if (File.Exists(path)) found.Add((Path.GetFullPath(path), null));
            else if (Directory.Exists(path))
                foreach (string file in Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                             .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase))
                    found.Add((Path.GetFullPath(file), Path.GetFullPath(path)));
        }
        return found;
    }

    /// <summary>A lip sync file's bytes read as whichever form they are: a <c>.re</c> (HDF5), CR2W, or raw. <paramref name="unknown"/> lists a <c>.re</c>'s controls the face hasn't got.</summary>
    public static LipSyncFile Read(byte[] data, out List<string> unknown)
    {
        unknown = [];
        if (Hdf5Reader.IsHdf5(data))
        {
            var (lipSync, names) = LipSyncRe.FromRe(Hdf5Reader.Read(data));
            unknown = names;
            return lipSync;
        }
        return LipSyncFile.ReadAny(data);
    }

    /// <summary>The form a file's bytes are in, as the extension it would have.</summary>
    public static string FormOf(byte[] data) => Hdf5Reader.IsHdf5(data) ? ".re" : LipSyncFile.IsCr2w(data) ? ".cr2w" : ".raw";

    public static byte[] Write(LipSyncFile lipSync, LipSyncFormat format) =>
        format == LipSyncFormat.Re ? Hdf5Writer.Write(LipSyncRe.ToRe(lipSync)) : lipSync.Write();

    /// <summary>
    /// Converts each input to <paramref name="format"/>: beside it, or under <paramref name="outputFolder"/> keeping
    /// the folders below the one it was found in. A file already in that form is passed over, and so is one whose
    /// output is there already unless <paramref name="overwrite"/>.
    /// </summary>
    public static List<LipSyncConversion> Convert(IReadOnlyList<(string File, string? Root)> inputs, LipSyncFormat format, string? outputFolder, bool overwrite,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var results = new List<LipSyncConversion>();
        string extension = Extension(format);
        for (int i = 0; i < inputs.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, inputs.Count));
            var (file, root) = inputs[i];
            try
            {
                byte[] data = File.ReadAllBytes(file);
                if (FormOf(data) == extension)
                {
                    results.Add(new(file, null, Note: L.T("LipSyncConverter.AlreadyThat", extension)));
                    continue;
                }
                string name = Path.ChangeExtension(Path.GetFileName(file), extension);
                string below = root is null ? "." : Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                string folder = outputFolder is null ? Path.GetDirectoryName(file)! : below == "." ? outputFolder : Path.Combine(outputFolder, below);
                string output = Path.Combine(folder, name);
                if (File.Exists(output) && !overwrite)
                {
                    results.Add(new(file, null, Note: L.T("LipSyncConverter.OutputThere", Path.GetFileName(output))));
                    continue;
                }
                var lipSync = Read(data, out var unknown);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(output, Write(format == LipSyncFormat.Raw ? lipSync.WithStillControls() : lipSync, format));
                results.Add(new(file, output, Note: unknown.Count > 0 ? L.T("LipSyncConverter.UnknownControls", string.Join(", ", unknown.Take(8))) : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
                                           or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
            {
                results.Add(new(file, null, Error: ex.Message));
            }
        }
        progress?.Report((inputs.Count, inputs.Count));
        return results;
    }
}
