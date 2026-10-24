using System.ComponentModel;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>An opened file (tree root) or a folder inside one.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    private FolderNode(string name, IArchive archive, FolderNode? parent)
    {
        _name = name;
        Archive = archive;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _name;

    /// <summary>A folder's name; a tree root's is its archive's owner, asked each time (Game files is translated).</summary>
    public string Name => Parent is null ? Archive.Owner : _name;
    public IArchive Archive { get; }
    public FolderNode? Parent { get; }
    public bool IsBundle => Parent is null;

    /// <summary>A mod's entry in the tree, with each of its bundles, compiled scripts and strings under it. It holds no files of its own.</summary>
    public bool IsGroup => Archive is GroupArchive;

    /// <summary>A heading at the top of a tree (the asset browser's Game files and Mods), shown in the accent colour.</summary>
    public bool IsHeading
    {
        get => _isHeading;
        set
        {
            if (_isHeading == value) return;
            _isHeading = value;
            // headings live as long as the app (the asset browser keeps its tree), so they follow the language
            if (value) Loc.LanguageChanged += Retitle;
            else Loc.LanguageChanged -= Retitle;
        }
    }

    private bool _isHeading;

    private void Retitle()
    {
        Raise(nameof(Name));
        Raise(nameof(DisplayName));
        Raise(nameof(ToolTip));
    }

    /// <summary>Whether this archive is listed under its mod's entry, which already says the mod's name.</summary>
    public bool InGroup { get; private set; }

    /// <summary>The name shown in the tree: the archive's file name when it's under its mod, otherwise the mod's.</summary>
    public string DisplayName => InGroup ? Archive.Name : Name;

    /// <summary>Puts an archive under its mod's entry, or back out of it.</summary>
    public void SetInGroup(bool inGroup)
    {
        if (InGroup == inGroup) return;
        InGroup = inGroup;
        Raise(nameof(DisplayName));
        Raise(nameof(Detail));
    }

    /// <summary>
    /// A mod's entry in the tree, made of the archives that belong to it. <paramref name="membersInGroup"/> false keeps
    /// the members' own names, for a group of groups (the asset browser's Mods, each mod's entry under it).
    /// </summary>
    public static FolderNode Group(string owner, IReadOnlyList<FolderNode> members, bool membersInGroup = true, bool heading = false) =>
        Group(() => owner, members, membersInGroup, heading);

    /// <summary>A group whose title is looked up each time it's shown, for a heading the tables translate (Mod DLC, Mods).</summary>
    public static FolderNode Group(Func<string> owner, IReadOnlyList<FolderNode> members, bool membersInGroup = true, bool heading = false)
    {
        var group = new FolderNode("", new GroupArchive(owner), null) { Children = [.. members], IsHeading = heading };
        group.TotalFiles = members.Sum(m => m.TotalFiles);
        if (membersInGroup) foreach (var member in members) member.SetInGroup(true);
        return group;
    }

    /// <summary>A heading with nothing under it yet, greyed out, saying why and how to set it up (REDkit before Settings has it).</summary>
    public static FolderNode Placeholder(string title, string why) =>
        new(title, new GroupArchive(title), null) { IsHeading = true, Unavailable = why };

    /// <summary>Why the heading is greyed out with nothing under it, or null when it isn't.</summary>
    public string? Unavailable { get; private init; }

    public bool IsUnavailable => Unavailable is not null;

    public List<FolderNode> Children { get; private set; } = [];
    public List<EntryItem> Files { get; } = [];
    public int TotalFiles { get; private set; }
    /// <summary>The open project's files, marked out in the tree from the read-only archives and from loose scripts.</summary>
    public bool IsProject => IsBundle && Archive is ScriptFolder { Project: not null };
    /// <summary>Loose scripts opened without a project: they can be read, and edited once they are added to one.</summary>
    public bool IsLooseScripts => IsBundle && Archive is ScriptFolder { Project: null } folder && !ScriptFolder.IsGameScripts(folder.FilePath);
    public string Detail => IsBundle && !InGroup && !IsGroup ? Archive.Name : "";
    public string Glyph => !IsBundle ? "\uE8B7" : IsGroup ? "\uE838" : IsProject ? "\uE821" : IsLooseScripts ? "\uE8A5" : Archive is RedkitDepot ? "\uEC7A" : Archive is GameFilesArchive ? "\uE8F1" : Archive is ScriptArchive ? "\uE943" : Archive is TextureCacheArchive ? "\uEB9F" : Archive is StringsFolder ? "\uE8D2" : "\uE7B8";
    public string ToolTip => Unavailable ?? (IsBundle && Archive is RedkitDepot ? L.T("FolderNode.REDkitsOwnFilesBefore", Archive.FilePath)
        : IsGroup ? L.T("FolderNode.Archives", Name, Children.Count)
        : IsBundle && Archive is GameFilesArchive ? L.T("FolderNode.EveryFileGameHas", Archive.Name, Archive.FilePath)
        : IsBundle && Archive is ScriptFolder { Project: { } project } ? L.T("FolderNode.ProjectFilesCanEdited", project.Name, project.FilePath)
        : IsLooseScripts ? L.T("FolderNode.LooseScriptsNotProject", Archive.FilePath)
        : IsBundle ? Archive.FilePath : FullPath);

    public string FullPath => Parent is null || Parent.IsBundle ? Name : Parent.FullPath + "\\" + Name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public IEnumerable<EntryItem> AllFiles()
    {
        var stack = new Stack<FolderNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var f in node.Files)
                yield return f;
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    /// <summary>This folder and every folder under it.</summary>
    public IEnumerable<FolderNode> Folders()
    {
        var stack = new Stack<FolderNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    public static FolderNode Build(IArchive archive, Action<EntryItem, bool> checkedChanged)
    {
        var root = new FolderNode(archive.Owner, archive, null);
        var lookup = new Dictionary<(FolderNode, string), FolderNode>();
        var children = new Dictionary<FolderNode, List<FolderNode>>();

        foreach (var entry in archive.Entries)
        {
            var node = root;
            var parts = entry.Path.Split('\\', '/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0) continue;
                var key = (node, parts[i].ToLowerInvariant());
                if (!lookup.TryGetValue(key, out var child))
                {
                    child = new FolderNode(parts[i], archive, node);
                    lookup[key] = child;
                    if (!children.TryGetValue(node, out var list))
                        children[node] = list = [];
                    list.Add(child);
                }
                node = child;
            }
            node.Files.Add(new EntryItem(entry, checkedChanged));
        }

        foreach (var (node, list) in children)
            node.Children = [.. list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        root.CountFiles();
        root.IsExpanded = true;
        return root;
    }

    private int CountFiles()
    {
        // scripts keep their generated order (all.ws first); bundles list alphabetically
        if (Archive is not ScriptArchive)
            Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        TotalFiles = Files.Count + Children.Sum(c => c.CountFiles());
        return TotalFiles;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Stands behind a mod's entry in the tree, which has no file of its own.</summary>
internal sealed class GroupArchive(Func<string> owner) : IArchive
{
    public GroupArchive(string owner) : this(() => owner) { }

    public string FilePath => "mod:" + owner();
    public string Name => "";
    /// <summary>Asked each time, so a heading's title follows a language change.</summary>
    public string Owner => owner();
    public IReadOnlyList<ArchiveEntry> Entries => [];
    public bool ReadsFromDisk => false;
}
