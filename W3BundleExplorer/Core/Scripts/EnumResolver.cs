using System.IO;
using System.Text.RegularExpressions;

namespace W3BundleExplorer.Core.Scripts;

/// <summary>
/// Turns enum values back into names. Compiled code only keeps the number, so names come from the
/// blob's own enums and, when the game is installed alongside the mod, from the game's script sources.
/// </summary>
public sealed partial class EnumResolver
{
    private static readonly Dictionary<string, Dictionary<string, Dictionary<long, string>>> VanillaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    private readonly Dictionary<string, Dictionary<long, string>> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, string>> _vanilla;

    public EnumResolver(ScriptBlob blob)
    {
        string? dir = GameScripts.FindFolder(blob.FilePath);
        _vanilla = dir is null ? [] : LoadVanilla(dir);
        foreach (var e in blob.Enums)
            _local[e.Name] = Number(e, _vanilla.GetValueOrDefault(e.Name));
    }

    /// <summary>
    /// Members stored as int.MaxValue have no number of their own. A full redeclaration counts up
    /// from 0; a partial list of new names extends the game's enum after its last value.
    /// </summary>
    private static Dictionary<long, string> Number(ScriptEnum e, Dictionary<long, string>? vanilla)
    {
        var map = new Dictionary<long, string>();
        bool extends = vanilla is { Count: > 0 } && !vanilla.Values.All(v => e.Members.Any(m => m.Name == v));
        long next = extends ? vanilla!.Keys.Max() + 1 : 0;
        foreach (var (name, value) in e.Members)
        {
            if (value != int.MaxValue) next = value;
            map.TryAdd(next, name);
            next++;
        }
        return map;
    }

    /// <summary>True when the blob's enum adds names to one the game already declares.</summary>
    public bool IsExtension(ScriptEnum e) =>
        _vanilla.TryGetValue(e.Name, out var v) && v.Count > 0 && !v.Values.All(n => e.Members.Any(m => m.Name == n));

    public bool HasGameScripts => _vanilla.Count > 0;

    public string? Find(string enumType, long value)
    {
        if (_local.TryGetValue(enumType, out var map) && map.TryGetValue(value, out var n)) return n;
        if (_vanilla.TryGetValue(enumType, out map) && map.TryGetValue(value, out n)) return n;
        return null;
    }

    private static Dictionary<string, Dictionary<long, string>> LoadVanilla(string dir)
    {
        lock (CacheLock)
        {
            if (VanillaCache.TryGetValue(dir, out var cached)) return cached;
            var result = new Dictionary<string, Dictionary<long, string>>(StringComparer.Ordinal);
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.ws", SearchOption.AllDirectories))
                {
                    string text = Comments().Replace(File.ReadAllText(file), " ");
                    foreach (Match m in EnumDecl().Matches(text))
                    {
                        var map = new Dictionary<long, string>();
                        long next = 0;
                        foreach (var raw in m.Groups[2].Value.Split(','))
                        {
                            var parts = raw.Split('=', 2, StringSplitOptions.TrimEntries);
                            if (parts[0].Length == 0) continue;
                            if (parts.Length == 2 && TryNumber(parts[1], out long v)) next = v;
                            map.TryAdd(next, parts[0]);
                            next++;
                        }
                        result.TryAdd(m.Groups[1].Value, map);
                    }
                }
            }
            catch (IOException)
            {
                // unreadable script folder: fall back to numbers
            }
            VanillaCache[dir] = result;
            return result;
        }
    }

    private static bool TryNumber(string s, out long v) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v)
            : long.TryParse(s, out v);

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\benum\s+(\w+)\s*\{([^}]*)\}")]
    private static partial Regex EnumDecl();
}
