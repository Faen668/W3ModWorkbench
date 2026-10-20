using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;

namespace W3ModWorkbench;

/// <summary>Where a viewer's properties came from, said above them.</summary>
public enum PropertySource
{
    /// <summary>No resource file was found.</summary>
    None,
    /// <summary>REDkit's source file, which keeps what cooking leaves out.</summary>
    Redkit,
    /// <summary>The game's cooked file, as REDkit hasn't got one or isn't installed.</summary>
    Cooked,
    /// <summary>The file itself, as it's from a mod, a project or somewhere on disk, and that's what's being looked at.</summary>
    Own,
    /// <summary>The game's cooked file, as REDkit's source file is in a format this tool can't read yet.</summary>
    RedkitUnreadable,
}

/// <summary>Where a viewer takes its properties from: REDkit's copy of the file, when there is one to use, and what to say about it.</summary>
public sealed record PropertyOrigin(Cr2wFile? Redkit, PropertySource Source, string? RedkitPath);

/// <summary>
/// REDkit's own copies of the game's files, before they're cooked (the loose files in its <c>r4data</c>). Cooking leaves
/// out most of the editor's properties, so a viewer shows one of the game's own files' properties from REDkit's copy
/// when REDkit is installed and has one, and from the game's cooked file otherwise, saying which above them. A file from
/// a mod, a project or anywhere else shows its own properties: that's what's being looked at, and REDkit's copy is the
/// game's version. What a viewer draws always comes from the file opened; only the property lists come from here.
/// </summary>
public static class RedkitSource
{
    /// <summary>
    /// Where a file's properties should come from. <paramref name="origin"/> is where it was opened from: the archive
    /// (bundle or cache) it's in, or the file on disk. Run off the UI thread.
    /// </summary>
    public static PropertyOrigin For(string gamePath, string? origin)
    {
        if (origin is not null && FileSafety.IsRedkitFile(origin)) return new(null, PropertySource.Redkit, origin);
        if (origin is null || !FileSafety.IsGameContent(origin)) return new(null, PropertySource.Own, null);
        if (Path.IsPathRooted(gamePath) || FileSafety.RedkitFile(gamePath) is not { } source) return new(null, PropertySource.Cooked, null);
        return Parse(() => File.ReadAllBytes(source)) is { } read
            ? new(read, PropertySource.Redkit, source)
            : new(null, PropertySource.RedkitUnreadable, source);
    }

    /// <summary>REDkit's copy of one of the game's files, read. Null when REDkit isn't installed, hasn't got one, or it can't be read.</summary>
    public static (Cr2wFile File, string Path)? Read(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || Path.IsPathRooted(gamePath) || FileSafety.RedkitFile(gamePath) is not { } file) return null;
        return Parse(() => File.ReadAllBytes(file)) is { } read ? (read, file) : null;
    }

    /// <summary>A CR2W file's bytes read, or null when there are none or they're not a CR2W file this tool reads.</summary>
    public static Cr2wFile? Parse(Func<byte[]?> read)
    {
        try { return read() is { Length: >= 4 } data && data.AsSpan(0, 4).SequenceEqual("CR2W"u8) ? Cr2wFile.Read(data) : null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The object in REDkit's copy that is <paramref name="cooked"/> in the game's file: at the same place when the two
    /// files have the same objects, otherwise the one of its class with the same name, or the only one of its class, or
    /// the one at the same place among those of its class when both have as many. Null when none can be told apart.
    /// </summary>
    public static RedObject? Match(Cr2wFile redkit, RedObject cooked, Cr2wFile cookedFile)
    {
        if (cooked.Export is not { } export) return null;
        try
        {
            if (redkit.Exports.Count == cookedFile.Exports.Count && export.Index < redkit.Exports.Count
                && redkit.Exports[export.Index].ClassName == export.ClassName)
                return redkit.Object(export.Index);
            var same = redkit.Exports.Where(x => x.ClassName == export.ClassName).ToList();
            if (same.Count == 0) return null;
            if (Name(cooked) is { } name && same.Select(x => redkit.Object(x.Index)).FirstOrDefault(o => Name(o) == name) is { } named) return named;
            if (same.Count == 1) return redkit.Object(same[0].Index);
            var ours = cookedFile.Exports.Where(x => x.ClassName == export.ClassName).ToList();
            int at = ours.FindIndex(x => x.Index == export.Index);
            return ours.Count == same.Count && at >= 0 ? redkit.Object(same[at].Index) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// A struct in REDkit's object that is <paramref name="cooked"/> in the game's (an appearance, a slot): one of the same
    /// class and name in any of its lists. Null when there's none, or the struct has no name to tell it by.
    /// </summary>
    public static RedObject? MatchStruct(RedObject? redkitOwner, RedObject cooked) =>
        redkitOwner is null || Name(cooked) is not { } name ? null
            : redkitOwner.Properties.Select(p => p.Value).OfType<List<object?>>().SelectMany(list => list.OfType<RedObject>())
                .FirstOrDefault(o => o.ClassName == cooked.ClassName && Name(o) == name);

    private static string? Name(RedObject o) => o["name"] is string n && n.Length > 0 && n != "None" ? n : null;

    /// <summary>A file's CR2W version in a few words, " (CR2W 134)", or nothing when it can't be read.</summary>
    private static string Version(string? file)
    {
        try
        {
            if (file is null) return "";
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[8];
            return stream.Read(head) == 8 && head[..4].SequenceEqual("CR2W"u8) ? $" (CR2W {BitConverter.ToInt32(head[4..])})" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The note above the properties: where they came from, and why some may be missing.</summary>
    /// <param name="what">What the file is, in a word or two: "texture", "mesh", "behaviour tree", "entity".</param>
    /// <summary>
    /// The note over a scene's or cutscene's panel: where its properties come from, and for REDkit's copy that its
    /// timeline's events are still the game's, as REDkit keeps them in an order of its own and they can't be matched.
    /// </summary>
    public static string TimelineNote(PropertySource source, string? redkitPath, string what) => source == PropertySource.Redkit
        ? Note(source, redkitPath, what).TrimEnd('.') + ". Timeline events are read from the file the game loads, so what cooking leaves out isn't shown for them."
        : Note(source, redkitPath, what);

    public static string Note(PropertySource source, string? redkitPath, string what) => source switch
    {
        PropertySource.Redkit => $"Properties from REDkit's source file, which keeps what cooking leaves out: {redkitPath}",
        PropertySource.Cooked or PropertySource.None when !FileSafety.RedkitInstalled => "REDkit isn't installed, so the properties the game's cooked files leave out can't be shown. "
            + "With REDkit installed (free on Steam and GOG for owners of the game), they're read from its source files.",
        PropertySource.RedkitUnreadable => $"REDkit has a source file for this {what}, but it's in an older format this tool can't read yet{Version(redkitPath)}, "
            + $"so only what the game's cooked file keeps is shown: {redkitPath}",
        PropertySource.Own => $"Properties from this {what} itself, as it's from a mod, a project or a file on disk: REDkit's copy would show the game's version instead.",
        PropertySource.Cooked => $"REDkit has no source file for this {what}, so only what the game's cooked file keeps is shown.",
        _ => $"Neither REDkit nor the game has this {what}'s resource file, so only what's known without it is shown.",
    };
}
