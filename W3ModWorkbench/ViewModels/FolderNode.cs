using System.ComponentModel;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;

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
    public List<FolderNode> Children { get; private set; } = [];
    public List<EntryItem> Files { get; } = [];
    public int TotalFiles { get; private set; }
    public string Detail => IsBundle ? Archive.Name : "";
    public string Glyph => !IsBundle ? "\uE8B7" : Archive is ScriptArchive ? "\uE943" : Archive is StringsFolder ? "\uE8D2" : "\uE7B8";
    public string ToolTip => IsBundle ? Archive.FilePath : FullPath;

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
