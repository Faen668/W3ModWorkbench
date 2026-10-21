using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

public sealed record ExtractProgress(int Done, int Total, string CurrentPath);

/// <param name="Written">Where each file that was extracted was written.</param>
public sealed record ExtractResult(int Extracted, long Bytes, IReadOnlyList<(string Path, string Error)> Failures, bool Cancelled,
    IReadOnlyDictionary<ArchiveEntry, string> Written);

/// <summary>Turns a file's bytes into another format as it's extracted: the new extension (".png") and bytes, or null to keep it as it is.</summary>
public delegate (string Extension, byte[] Data)? ExtractConverter(ArchiveEntry entry, byte[] data);

public static class Extractor
{
    /// <summary>
    /// Writes each entry under <paramref name="outputDir"/>. With <paramref name="keepStructure"/>
    /// the bundle path is kept (gameplay\gui_new\...); otherwise files go straight into the folder
    /// and clashing names get a " (2)" suffix. <paramref name="convert"/>, when given, can write a file in another format.
    /// </summary>
    public static ExtractResult Extract(
        IReadOnlyList<ArchiveEntry> entries,
        string outputDir,
        bool keepStructure,
        IProgress<ExtractProgress>? progress,
        CancellationToken token,
        ExtractConverter? convert = null)
    {
        var failures = new List<(string, string)>();
        var written = new Dictionary<ArchiveEntry, string>(ReferenceEqualityComparer.Instance);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(outputDir);
        // never into the game's folders, REDkit's or a build: checked once for the folder, as every file goes under it
        if (Projects.FileSafety.Protection(Path.Combine(root, "_")) is { } protection)
            return new ExtractResult(0, 0, [.. entries.Select(e => (e.Path, protection))], false, written);
        int done = 0;
        long bytes = 0;
        long lastReport = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var group in entries.GroupBy(e => e.Archive))
        {
            FileStream? stream = null;
            try
            {
                if (group.Key.ReadsFromDisk)
                    stream = new FileStream(group.Key.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                foreach (var e in group)
                    failures.Add((e.Path, ex.Message));
                done += group.Count();
                continue;
            }

            using (stream)
            {
                foreach (var entry in group.OrderBy(e => e.SortKey))
                {
                    if (token.IsCancellationRequested)
                        return new ExtractResult(done - failures.Count, bytes, failures, true, written);

                    if (clock.ElapsedMilliseconds - lastReport >= 50)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress?.Report(new ExtractProgress(done, entries.Count, entry.Path));
                    }
                    try
                    {
                        byte[] data = entry.Read(stream);
                        string path = entry.ExtractPath;
                        if (convert?.Invoke(entry, data) is { } converted)
                        {
                            path = Path.ChangeExtension(path, converted.Extension);
                            data = converted.Data;
                        }
                        string target = TargetPath(root, path, keepStructure, usedNames);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        bytes += data.Length;
                        written[entry] = target;
                    }
                    catch (Exception ex)
                    {
                        failures.Add((entry.Path, ex.Message));
                    }
                    done++;
                }
            }
        }

        progress?.Report(new ExtractProgress(done, entries.Count, ""));
        return new ExtractResult(done - failures.Count, bytes, failures, false, written);
    }

    private static string TargetPath(string root, string entryPath, bool keepStructure, HashSet<string> usedNames)
    {
        string relative = entryPath.Replace('/', '\\').TrimStart('\\');
        if (!keepStructure)
        {
            string name = Path.GetFileName(relative);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 2; !usedNames.Add(name); n++)
                name = $"{stem} ({n}){ext}";
            relative = name;
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Extractor.EntrysPathPointsOutside"));
        return full;
    }
}
