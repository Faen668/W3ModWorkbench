using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A folder searched as it is on disk. Its files show as paths under it, and come from <paramref name="Source"/>.</summary>
/// <param name="Editable">False for the game's own scripts, which are for reading.</param>
public sealed record SearchRoot(string Folder, string Source, bool Editable);

/// <param name="FilesSearched">Files read, whether or not they matched.</param>
/// <param name="FilesSkipped">Files left out for being too big or not text.</param>
public sealed record FolderSearchResult(List<ScriptHit> Hits, int FilesSearched, int FilesSkipped);

/// <summary>
/// Find in files, as in a text editor: the text of every file under some folders whose name passes the filters,
/// read straight from disk. Nothing needs to be open; unlike <see cref="ScriptSearch"/> it isn't tied to a script.
/// </summary>
public static class FolderSearch
{
    /// <summary>Bigger files than this aren't text worth searching (bundles, textures).</summary>
    private const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The files under <paramref name="roots"/> whose names pass <paramref name="filters"/>: wildcards such as
    /// <c>*.ws; *.xml</c>, with <c>!</c> in front to leave files out. Nothing listed means every file. A file
    /// under two roots is listed once, under the first.
    /// </summary>
    public static List<(SearchRoot Root, string Path)> Files(IEnumerable<SearchRoot> roots, string filters, bool subfolders, CancellationToken cancel)
    {
        var (include, exclude) = ParseFilters(filters);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = subfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(SearchRoot, string)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Folder)) continue;
            var found = new List<string>();
            try
            {
                foreach (string path in Directory.EnumerateFiles(root.Folder, "*", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(path);
                    if (include.Count > 0 && !include.Any(r => r.IsMatch(name))) continue;
                    if (exclude.Any(r => r.IsMatch(name))) continue;
                    if (seen.Add(path)) found.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that can't be listed has nothing to search
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            files.AddRange(found.Select(p => (root, p)));
        }
        return files;
    }

    /// <summary>
    /// Whether a file passes <paramref name="filters"/>, as <see cref="Files"/> reads them: by its name, or by its
    /// path (under its folder) for a wildcard with a \ or / in it, such as <c>game\player\*</c>.
    /// </summary>
    public static Func<string, bool> PathFilter(string filters)
    {
        var (include, exclude) = ParseFilters(filters.Replace('/', '\\'));
        if (include.Count == 0 && exclude.Count == 0) return _ => true;
        bool Matches(Regex r, string path) => r.IsMatch(r.ToString().Contains(@"\\") ? path : Path.GetFileName(path));
        return path => (include.Count == 0 || include.Any(r => Matches(r, path))) && !exclude.Any(r => Matches(r, path));
    }

    private static (List<Regex> Include, List<Regex> Exclude) ParseFilters(string filters)
    {
        var include = new List<Regex>();
        var exclude = new List<Regex>();
        foreach (string token in filters.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool not = token.StartsWith('!');
            string glob = not ? token[1..] : token;
            if (glob.Length == 0 || glob == "*.*" && !not) continue; // *.* is every file, as nothing listed is
            var regex = new Regex("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            (not ? exclude : include).Add(regex);
        }
        return (include, exclude);
    }

    /// <summary>One hit per match, in file order. Stops at <see cref="ScriptSearch.MaxHits"/>.</summary>
    public static FolderSearchResult Text(Regex pattern, IReadOnlyList<(SearchRoot Root, string Path)> files, CancellationToken cancel,
        IProgress<int>? progress = null)
    {
        var hits = new ConcurrentBag<(int Order, ScriptHit Hit)>();
        int count = 0, done = 0, skipped = 0;
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, (i, loop) =>
        {
            var (root, path) = files[i];
            string? text = ReadText(path);
            if (text is null) Interlocked.Increment(ref skipped);
            else
            {
                ScriptFile? file = null;
                List<int>? starts = null;
                for (var m = pattern.Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Length == 0) continue;
                    if (cancel.IsCancellationRequested || Interlocked.Increment(ref count) > ScriptSearch.MaxHits) { loop.Stop(); return; }
                    file ??= FileFor(root, path);
                    starts ??= ScriptSearch.LineStarts(text);
                    hits.Add((i, ScriptSearch.Hit(file, starts, text, m.Index, m.Length, null)));
                }
            }
            int n = Interlocked.Increment(ref done);
            if (n % 100 == 0) progress?.Report(n);
        });
        cancel.ThrowIfCancellationRequested();
        return new FolderSearchResult([.. hits.OrderBy(h => h.Order).ThenBy(h => h.Hit.Offset).Select(h => h.Hit)], done, skipped);
    }

    /// <summary>The file as the script lookups know it: its full path is its key, its path shown is under the root.</summary>
    public static ScriptFile FileFor(SearchRoot root, string path) => new()
    {
        Key = Path.GetFullPath(path),
        Path = Path.GetRelativePath(root.Folder, path),
        Source = root.Source,
        SourceKey = root.Folder,
        DiskPath = root.Editable ? Path.GetFullPath(path) : null,
        Read = () => File.ReadAllBytes(path),
    };

    /// <summary>The file's text, or null when it's too big, can't be read or isn't text.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return null;
            byte[] data = File.ReadAllBytes(path);
            bool utf16 = data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF);
            if (!utf16 && Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 4096)) >= 0) return null;
            return TextDecoding.Decode(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
