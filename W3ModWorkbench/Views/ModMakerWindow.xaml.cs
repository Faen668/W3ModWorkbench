using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A file the window can copy, with its tick.</summary>
public sealed class MakeRow(ModMakerFile file, MakeGroup group) : INotifyPropertyChanged
{
    private bool _picked = file.Pick && !file.InProject;

    public ModMakerFile File => file;
    public string Name => Path.GetFileName(file.GamePath);
    public string Folder => Path.GetDirectoryName(file.GamePath) ?? "";

    /// <summary>"→ .png": what a texture goes in as.</summary>
    public string Becomes => ModMaker.IsTexturePath(file.GamePath) ? "  → " + (file.IsLayered ? ".dds" : group.Format) : "";

    public string Detail => file.Note.Length > 0 && file.Group != ModMakerGroup.Shared ? $"{file.Note}  ·  {Format.Bytes(file.Size)}" : file.Size > 0 ? Format.Bytes(file.Size) : file.Note;
    public string Tooltip => file.GamePath + (file.UsedBy is null ? "" : "\n" + L.T("ModMaker.UsedBy", file.UsedBy))
        + (file.Group == ModMakerGroup.Shared ? "\n" + file.Note : "");

    /// <summary>A file the project has already is left as it is, so it can't be ticked.</summary>
    public bool CanPick => !file.InProject && (file.Size > 0 || file.Note.Length > 0 && file.Group != ModMakerGroup.Other);
    public Visibility InProjectVisibility => file.InProject ? Visibility.Visible : Visibility.Collapsed;
    public double Opacity => CanPick ? 1 : 0.55;

    public bool Picked
    {
        get => _picked;
        set { if (_picked != value) { _picked = value; Changed(); group.RowChanged(); } }
    }

    public void FormatChanged() => Changed(nameof(Becomes));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A group of the window's list: the item, its meshes, materials, textures, other files and shared ones.</summary>
public sealed class MakeGroup : INotifyPropertyChanged
{
    private readonly Action _changed;
    private string _format;

    public MakeGroup(ModMakerGroup kind, string format, Action changed)
    {
        Kind = kind;
        _format = format;
        _changed = changed;
    }

    public ModMakerGroup Kind { get; }
    public List<MakeRow> Rows { get; } = [];

    public string Title => Kind switch
    {
        ModMakerGroup.Item => L.T("ModMaker.GroupItem"),
        ModMakerGroup.Meshes => L.T("ModMaker.GroupMeshes"),
        ModMakerGroup.Materials => L.T("ModMaker.GroupMaterials"),
        ModMakerGroup.Textures => L.T("ModMaker.GroupTextures"),
        ModMakerGroup.Other => L.T("ModMaker.GroupOther"),
        _ => L.T("ModMaker.GroupShared"),
    } + $"  ({Rows.Count})";
    public string Hint => Kind switch
    {
        ModMakerGroup.Textures => L.T("ModMaker.TexturesHint"),
        ModMakerGroup.Other => L.T("ModMaker.OtherHint"),
        ModMakerGroup.Shared => L.T("ModMaker.SharedHint"),
        _ => "",
    };
    public Visibility HintVisibility => Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string[] Formats => ModMaker.PictureFormats;
    public Visibility FormatVisibility => Kind == ModMakerGroup.Textures ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What the group's textures go in as.</summary>
    public string Format
    {
        get => _format;
        set
        {
            if (_format == value) return;
            _format = value;
            foreach (var row in Rows) row.FormatChanged();
            Changed();
        }
    }

    public Visibility ToggleVisibility => Kind != ModMakerGroup.Item && Rows.Count(r => r.CanPick) > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string ToggleText => Rows.Where(r => r.CanPick).All(r => r.Picked) ? L.T("ModMaker.TickNone") : L.T("ModMaker.TickAll");

    public void Toggle()
    {
        bool all = !Rows.Where(r => r.CanPick).All(r => r.Picked);
        foreach (var row in Rows.Where(r => r.CanPick)) row.Picked = all;
    }

    public void RowChanged()
    {
        Changed(nameof(ToggleText));
        _changed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Make a mod of this: a game file and what it's made of (an entity's meshes, their materials and textures), ticked,
/// to copy into the project ready to change. Files many others use are listed but not ticked, textures go in as
/// pictures, and the copy of the file opens once it's in the project.
/// </summary>
public partial class ModMakerWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly byte[]? _data;
    private readonly ResourceLocator _game;
    private readonly CancellationTokenSource _cancel = new();
    private List<MakeGroup> _groups = [];
    private bool _busy;

    private ModMakerWindow(string gamePath, byte[]? data)
    {
        InitializeComponent();
        _path = gamePath;
        _data = data;
        // the game's own files, not another installed mod's copy of them
        _game = new ResourceLocator([], null, GameLocator.Resolve(null), otherMods: false);
        TitleText.Text = L.T("ModMaker.TitleOf", Path.GetFileName(gamePath));
        Title = L.T("ModMaker.TitleOf", Path.GetFileName(gamePath));
        PathText.Text = gamePath;
        OpenCheck.IsChecked = App.Settings.MakeModOpenCopy;
        ShowProject();
        Loaded += async (_, _) => await FindAsync();
    }

    public static void Open(Window? owner, string gamePath, byte[]? data = null) =>
        new ModMakerWindow(gamePath, data).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private void ShowProject() =>
        ProjectText.Text = ProjectWorkspace.Current is { } project ? L.T("ModMaker.IntoProject", project.Name) : L.T("ModMaker.NoProject");

    private async Task FindAsync()
    {
        SetBusy(true, L.T("ModMaker.Finding"));
        try
        {
            var project = ProjectWorkspace.Current;
            var files = await Task.Run(() => ModMaker.Find(_path, _game, p => project is not null && ModMaker.InProject(project, p), _data, _cancel.Token));
            string format = ModMaker.PictureFormats.Contains(App.Settings.MakeModPictureFormat) ? App.Settings.MakeModPictureFormat : ".png";
            _groups = [];
            foreach (var kind in Enum.GetValues<ModMakerGroup>())
            {
                var group = new MakeGroup(kind, format, UpdateSummary);
                group.Rows.AddRange(files.Where(f => f.Group == kind).OrderBy(f => f.GamePath, StringComparer.OrdinalIgnoreCase).Select(f => new MakeRow(f, group)));
                if (group.Rows.Count > 0) _groups.Add(group);
            }
            Groups.ItemsSource = _groups;
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        finally
        {
            SetBusy(false);
        }
    }

    private IEnumerable<MakeRow> Picked => _groups.SelectMany(g => g.Rows).Where(r => r.Picked && r.CanPick);

    private void UpdateSummary()
    {
        var picked = Picked.ToList();
        int all = _groups.Sum(g => g.Rows.Count);
        SummaryText.Text = L.T("ModMaker.Summary", picked.Count, all, Format.Bytes(picked.Sum(r => (long)r.File.Size)));
        CopyText.Text = L.T("ModMaker.CopyCount", L.T("Count.File", picked.Count));
        CopyButton.IsEnabled = !_busy && picked.Count > 0;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) BusyText.Text = text;
        ListScroller.IsEnabled = !busy;
        if (!busy) UpdateSummary();
        else CopyButton.IsEnabled = false;
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MakeGroup group) group.Toggle();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow main) return;
        if (await main.EnsureProjectAsync(L.T("ModMaker.NeedsProject"), Message) is not { } project) return;
        ShowProject();
        var picked = Picked.Select(r => r.File).ToList();
        string format = _groups.FirstOrDefault(g => g.Kind == ModMakerGroup.Textures)?.Format ?? App.Settings.MakeModPictureFormat;
        App.Settings.MakeModPictureFormat = format;
        App.Settings.MakeModOpenCopy = OpenCheck.IsChecked == true;
        App.Settings.Save();

        SetBusy(true, L.T("ModMaker.Copying"));
        var progress = new Progress<string>(path => BusyText.Text = L.T("ModMaker.CopyingFile", Path.GetFileName(path)));
        ModMaker.CopyResult result;
        try { result = await Task.Run(() => ModMaker.Copy(picked, format, _game, project, _data, progress)); }
        finally { SetBusy(false); }

        // the workspace is part of the project, so what's copied there is listed with it
        if (!project.Contains(project.WorkspacePath)) await main.AddToProjectAsync([project.WorkspacePath]);
        else await main.ReloadProjectAsync();

        var root = picked.FirstOrDefault(f => f.Group == ModMakerGroup.Item);
        int pictures = result.Copied.Count(p => ModMaker.PictureFormats.Contains(Path.GetExtension(p).ToLowerInvariant()));
        // a mesh's buffers come with it, said apart so the count matches what was ticked
        int buffers = result.Copied.Count(p => p.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase));
        string text = L.T("ModMaker.Copied", L.T("Count.File", result.Copied.Count - buffers), project.Name);
        if (buffers > 0) text += " " + L.T("ModMaker.CopiedBuffers", L.T("Count.Buffer", buffers));
        if (pictures > 0) text += "\n\n" + L.T("ModMaker.CopiedPictures", L.T("Count.Texture", pictures));
        if (result.Existing.Count > 0) text += "\n\n" + L.T("ModMaker.CopiedExisting", L.T("Count.File", result.Existing.Count));
        if (result.Failed.Count > 0) text += "\n\n" + L.T("ModMaker.CopiedFailed", string.Join("\n", result.Failed.Take(10)));
        await Message.Show(L.T("ModMaker.Done"), text, result.Failed.Count > 0 ? MessageKind.Warning : MessageKind.Info, primary: L.T("Common.OK"));

        if (OpenCheck.IsChecked == true && root is not null)
        {
            string copy = ModMaker.TargetFor(project, root, format);
            if (File.Exists(copy)) main.OpenProjectFile(copy);
        }
        if (result.Failed.Count == 0) Close();
        else await FindAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _cancel.Cancel();
}
