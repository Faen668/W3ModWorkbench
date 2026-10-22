using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using W3ModWorkbench.Core;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>Which of Script Bench's groups a node is in: the project's own scripts, the game's, or an installed mod's.</summary>
public enum BenchGroup { Mine, Game, Mods }

/// <summary>
/// A folder or a script in Script Bench's tree. The project's scripts can be changed; the game's and the installed mods'
/// are there to read, to override, and to look things up in. A mod's scripts are read when it's first expanded.
/// </summary>
public sealed class BenchNode : INotifyPropertyChanged
{
    private bool _expanded, _selected;
    private Func<Task<List<BenchNode>>>? _load;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }
    public required BenchGroup Group { get; init; }
    public BenchNode? Parent { get; private set; }

    /// <summary>One of the three groups at the top of the tree.</summary>
    public bool IsHeading { get; init; }

    /// <summary>A script: the entry it's read from. Null for a folder or a heading.</summary>
    public ArchiveEntry? Entry { get; init; }

    public bool IsFile => Entry is not null;

    /// <summary>For the project's scripts: the folder or file on disk.</summary>
    public string? DiskPath { get; init; }

    /// <summary>The path under a scripts folder (<c>game\player\player.ws</c>), which is how a mod's file overrides the game's.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The navigation key of a script, as its tab has it, so the tree can find the file in front.</summary>
    public string? Key { get; init; }

    /// <summary>A short word beside the name: OVERRIDE for one of the project's scripts that replaces the game's.</summary>
    public string? Badge { get; set; }

    public string? Detail { get; init; }
    public string? ToolTip { get; set; }

    /// <summary>A line that isn't a file or a folder, such as "No project open" or "Reading…".</summary>
    public bool IsNote { get; init; }

    /// <summary>The open project's own mod as installed in the game's mods folder, left out of the count of other mods.</summary>
    public bool IsMyInstalledBuild { get; init; }

    public ObservableCollection<BenchNode> Children { get; } = [];

    public string Glyph => IsNote ? ""
        : IsHeading ? Group switch { BenchGroup.Mine => "", BenchGroup.Game => "", _ => "" }
        : IsFile ? ""
        : Group == BenchGroup.Mods && Parent is { IsHeading: true } ? ""
        : "";

    /// <summary>Whether it can be changed here: only the project's own scripts and folders.</summary>
    public bool IsEditable => Group == BenchGroup.Mine && DiskPath is not null;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            OnChanged();
            if (value && _load is not null) _ = LoadAsync();
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnChanged();
        }
    }

    public void Add(BenchNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Children read the first time the node is expanded, with a note in their place until then.</summary>
    public void LoadLater(Func<Task<List<BenchNode>>> load)
    {
        _load = load;
        Add(Note(Group, L.T("BenchNode.Reading")));
    }

    private async Task LoadAsync()
    {
        var load = _load!;
        _load = null;
        List<BenchNode> children;
        try
        {
            children = await load();
        }
        catch (Exception ex)
        {
            children = [Note(Group, L.T("BenchNode.CouldntRead", ex.Message))];
        }
        Children.Clear();
        foreach (var child in children) Add(child);
    }

    /// <summary>Every script under this node, read so far.</summary>
    public IEnumerable<BenchNode> Files()
    {
        foreach (var child in Children)
        {
            if (child.IsFile) yield return child;
            else foreach (var file in child.Files()) yield return file;
        }
    }

    public static BenchNode Note(BenchGroup group, string text, string? toolTip = null) =>
        new() { Name = text, Group = group, IsNote = true, ToolTip = toolTip };

    /// <summary>
    /// Folders and scripts from paths under a scripts folder, folders first and each level in name order, under
    /// <paramref name="parent"/>. <paramref name="make"/> makes a script's node from its path and its entry.
    /// </summary>
    public static void Fill(BenchNode parent, IEnumerable<(string Relative, ArchiveEntry Entry)> files,
        Func<string, ArchiveEntry, BenchNode> make, Func<string, string?>? folderDisk = null)
    {
        foreach (var (relative, entry) in files)
            parent.FolderAt(System.IO.Path.GetDirectoryName(relative) ?? "", folderDisk).Add(make(relative, entry));
        Sort(parent);
    }

    /// <summary>The folder at a path under this node, made (with the ones above it) when it isn't there yet.</summary>
    public BenchNode FolderAt(string relative, Func<string, string?>? folderDisk = null)
    {
        var node = this;
        string path = "";
        foreach (string part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "\\" + part;
            var next = node.Children.FirstOrDefault(c => !c.IsFile && !c.IsNote && c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new BenchNode { Name = part, Group = Group, RelativePath = path, DiskPath = folderDisk?.Invoke(path) };
                node.Add(next);
            }
            node = next;
        }
        return node;
    }

    /// <summary>A copy of a script's node, for the filtered tree.</summary>
    public BenchNode CopyFile() => new()
    {
        Name = Name, Group = Group, Entry = Entry, DiskPath = DiskPath, RelativePath = RelativePath, Key = Key,
        Badge = Badge, Detail = Detail, ToolTip = ToolTip,
    };

    public static void Sort(BenchNode node)
    {
        var sorted = node.Children.OrderBy(c => c.IsFile).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            if (!child.IsFile) Sort(child);
        }
    }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
