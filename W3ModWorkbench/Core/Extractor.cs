using System.IO;

namespace W3ModWorkbench.Core;

public sealed record ExtractProgress(int Done, int Total, string CurrentPath);

public sealed record ExtractResult(int Extracted, long Bytes, IReadOnlyList<(string Path, string Error)> Failures, bool Cancelled);

public static class Extractor
{
    /// <summary>
    /// Writes each entry under <paramref name="outputDir"/>. With <paramref name="keepStructure"/>
    /// the bundle path is kept (gameplay\gui_new\...); otherwise files go straight into the folder
    /// and clashing names get a " (2)" suffix.
    /// </summary>
    public static ExtractResult Extract(
        IReadOnlyList<ArchiveEntry> entries,
        string outputDir,
        bool keepStructure,
        IProgress<ExtractProgress>? progress,
        CancellationToken token)
    {
        var failures = new List<(string, string)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(outputDir);
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
                        return new ExtractResult(done - failures.Count, bytes, failures, true);

                    if (clock.ElapsedMilliseconds - lastReport >= 50)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress?.Report(new ExtractProgress(done, entries.Count, entry.Path));
                    }
                    try
                    {
                        string target = TargetPath(root, entry.ExtractPath, keepStructure, usedNames);
                        byte[] data = entry.Read(stream);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        bytes += data.Length;
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
        return new ExtractResult(done - failures.Count, bytes, failures, false);
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
            throw new InvalidDataException("The entry's path points outside the output folder.");
        return full;
    }
}
