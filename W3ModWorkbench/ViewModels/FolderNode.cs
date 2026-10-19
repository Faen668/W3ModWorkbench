using System.ComponentModel;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench.ViewModels;

/// <summary>An opened file (tree root) or a folder inside one.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    private FolderNode(string name, IArchive archive, FolderNode? parent)
    {
        Name = name;
        Archive = archive;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public IArchive Archive { get; }
    public FolderNode? Parent { get; }
    public bool IsBundle => Parent is null;

    /// <summary>A mod's entry in the tree, with each of its bundles, compiled scripts and strings under it. It holds no files of its own.</summary>
    public bool IsGroup => Archive is GroupArchive;

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

    /// <summary>A mod's entry in the tree, made of the archives that belong to it.</summary>
    public static FolderNode Group(string owner, IReadOnlyList<FolderNode> members)
    {
        var group = new FolderNode(owner, new GroupArchive(owner), null) { Children = [.. members] };
        group.TotalFiles = members.Sum(m => m.TotalFiles);
        foreach (var member in members) member.SetInGroup(true);
        return group;
    }
    public List<FolderNode> Children { get; private set; } = [];
    public List<EntryItem> Files { get; } = [];
    public int TotalFiles { get; private set; }
    /// <summary>The open project's files, marked out in the tree from the read-only archives and from loose scripts.</summary>
    public bool IsProject => IsBundle && Archive is ScriptFolder { Project: not null };
    /// <summary>Loose scripts opened without a project: they can be read, and edited once they are added to one.</summary>
    public bool IsLooseScripts => IsBundle && Archive is ScriptFolder { Project: null } folder && !ScriptFolder.IsGameScripts(folder.FilePath);
    public string Detail => IsBundle && !InGroup && !IsGroup ? Archive.Name : "";
    public string Glyph => !IsBundle ? "\uE8B7" : IsGroup ? "\uE838" : IsProject ? "\uE821" : IsLooseScripts ? "\uE8A5" : Archive is GameFilesArchive ? "\uE8F1" : Archive is ScriptArchive ? "\uE943" : Archive is TextureCacheArchive ? "\uEB9F" : Archive is StringsFolder ? "\uE8D2" : "\uE7B8";
    public string ToolTip => IsGroup ? $"{Name}: {Children.Count} archives"
        : IsBundle && Archive is GameFilesArchive ? $"Every file the game has, from {Archive.Name}, each from where the game loads it (mods first, then DLC, then content0)\n{Archive.FilePath}"
        : IsBundle && Archive is ScriptFolder { Project: { } project } ? $"Project {project.Name}: its files can be edited, saved and compiled\n{project.FilePath}"
        : IsLooseScripts ? $"Loose scripts, not in a project: editing them offers to add them to one\n{Archive.FilePath}"
        : IsBundle ? Archive.FilePath : FullPath;

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
internal sealed class GroupArchive(string owner) : IArchive
{
    public string FilePath => "mod:" + owner;
    public string Name => "";
    public string Owner => owner;
    public IReadOnlyList<ArchiveEntry> Entries => [];
    public bool ReadsFromDisk => false;
}
