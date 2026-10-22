using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A simplex tree (.w3simplex) as REDkit's SimplexTree Editor shows it: the world from above, divided by the tree's
/// lines into areas, each area filled and numbered, over the world's ground when its world is found. The areas are
/// listed with their size and middle, the splits with their lines and where each side goes, and the file's properties.
/// Hovering says which area a point is in; clicking picks it and its lines.
/// </summary>
public partial class SimplexTreeWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private SimplexTreeFile? _tree;
    private bool _selecting;

    /// <summary>An area in the list: its id, its colour, how many pieces it's in, its size and middle.</summary>
    private sealed record AreaRow(int Area, Brush Swatch, int Pieces, double Size, Vector2 Centre)
    {
        public string SizeText => L.T("SimplexTreeWindow.SquareMetres", Size);
        public string CentreText => $"{Centre.X:0.0}, {Centre.Y:0.0}";
    }

    /// <summary>A split in the list, with where each side goes in words.</summary>
    private sealed record SplitRow(SimplexSplit Split)
    {
        public int Index => Split.Index;
        public float Offset => Split.Offset;
        public string ParentText => Split.Parent >= 0 ? Split.Parent.ToString() : "—";
        public string NormalText => $"{Split.Normal.X,7:0.000} {Split.Normal.Y,7:0.000}";
        public string OffsetText => $"{Split.Offset:0.00}";
        public string PositiveText => Side(Split.Positive, Split.PositiveArea);
        public string NegativeText => Side(Split.Negative, Split.NegativeArea);

        private static string Side(int next, int area) =>
            next >= 0 ? L.T("SimplexTreeWindow.SplitNumber", next) : area >= 0 ? L.T("SimplexTreeWindow.AreaNumber", area) : L.T("SimplexTreeWindow.NoArea");
    }

    private SimplexTreeWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("SimplexTreeWindow.SimplexTree2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await LoadAsync();
    }

    public static bool CanShow(string name) => SimplexTreeFile.IsSimplexTree(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new SimplexTreeWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new SimplexTreeWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        (Vector2 Min, Vector2 Max)? world;
        string? worldPath;
        try
        {
            (_tree, world, worldPath) = await Task.Run(() =>
            {
                var tree = SimplexTreeFile.Read(_readData());
                var (bounds, from) = WorldGround(_path, _origin, _locator);
                return (tree, bounds, from);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("SimplexTreeWindow.CouldNotRead", ex.Message);
            return;
        }
        var tree = _tree;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        Map.Show(tree, world);
        WorldButton.IsEnabled = world is not null;
        if (world is null) WorldButton.ToolTip = L.T("SimplexTreeWindow.NoWorldFound");

        var cells = Map.Cells;
        var areas = cells.Where(c => c.Area >= 0).GroupBy(c => c.Area).OrderBy(g => g.Key).Select(g =>
        {
            double size = g.Sum(c => c.Size);
            var centre = g.Aggregate(Vector2.Zero, (sum, c) => sum + c.Centre * c.Size) / (float)Math.Max(size, 1e-6);
            var swatch = new SolidColorBrush(SimplexMap.ColorOf(g.Key));
            swatch.Freeze();
            return new AreaRow(g.Key, swatch, g.Count(), size, centre);
        }).ToList();
        AreaGrid.ItemsSource = areas;
        SplitGrid.ItemsSource = tree.Splits.Select(s => new SplitRow(s)).ToList();
        AreasTab.Header = L.T("SimplexTreeWindow.Areas2", areas.Count);
        SplitsTab.Header = L.T("SimplexTreeWindow.Splits2", tree.Splits.Count);
        PurposeText.Text = Purpose(Path.GetFileNameWithoutExtension(_path));
        int depth = tree.Splits.Count == 0 ? 0 : tree.Splits.Max(s => Depth(tree, s.Index));
        InfoText.Text = L.T("SimplexTreeWindow.Info", _path, _source, tree.File.Version, L.T("Count.Split", tree.Splits.Count), depth,
            L.T("Count.Area", areas.Count), worldPath is null ? L.T("SimplexTreeWindow.NoWorld") : L.T("SimplexTreeWindow.OverWorld", Path.GetFileName(worldPath)));
        FileProperties.Show(PropertyRow.Of(tree.Resource, tree.File));
        _ = LoadRedkitAsync();
    }

    private static int Depth(SimplexTreeFile tree, int split)
    {
        int depth = 0;
        for (int at = split; at >= 0 && at < tree.Splits.Count && depth <= tree.Splits.Count; at = tree.Splits[at].Parent) depth++;
        return depth;
    }

    /// <summary>What a tree is for, by the names the game gives them, in a sentence.</summary>
    private static string Purpose(string name) =>
        name.Contains("wind", StringComparison.OrdinalIgnoreCase) ? L.T("SimplexTreeWindow.PurposeWind")
        : name.Contains("water", StringComparison.OrdinalIgnoreCase) ? L.T("SimplexTreeWindow.PurposeWater")
        : name.Contains("umbra", StringComparison.OrdinalIgnoreCase) || name.Contains("occ", StringComparison.OrdinalIgnoreCase) ? L.T("SimplexTreeWindow.PurposeOcclusion")
        : L.T("SimplexTreeWindow.PurposeOther");

    /// <summary>
    /// The ground of the world the tree belongs to, from its terrain (where its corner is and how wide it is): the world
    /// in the tree's folder or the nearest above it. Null when none is found or it can't be read.
    /// </summary>
    internal static ((Vector2 Min, Vector2 Max)? Bounds, string? Path) WorldGround(string path, string? origin, ResourceLocator locator)
    {
        try
        {
            string folder = Path.GetDirectoryName(path) ?? "";
            if (Path.IsPathRooted(path) && origin is not null)
            {
                // a file on disk: a world beside it or above it, on disk
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(origin) ?? ""); dir is not null; dir = dir.Parent)
                    if (dir.Exists && dir.GetFiles("*.w2w").FirstOrDefault() is { } found)
                        return (Ground(File.ReadAllBytes(found.FullName)), found.FullName);
                return (null, null);
            }
            var worlds = locator.AllPaths(".w2w");
            for (string at = folder; at.Length > 0; at = Path.GetDirectoryName(at) ?? "")
                if (worlds.FirstOrDefault(w => string.Equals(Path.GetDirectoryName(w), at, StringComparison.OrdinalIgnoreCase)) is { } world
                    && locator.ReadFile(world) is { } data)
                    return (Ground(data), world);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return (null, null);
    }

    private static (Vector2 Min, Vector2 Max)? Ground(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        if (file.First("CClipMap") is not { } clip) return null;
        float size = clip.Float("terrainSize");
        if (size <= 0) return null;
        var corner = clip.Vector("terrainCorner", new Vector3(-size / 2, -size / 2, 0));
        return (new Vector2(corner.X, corner.Y), new Vector2(corner.X + size, corner.Y + size));
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => Map.FitToView();

    private void WorldButton_Click(object sender, RoutedEventArgs e) => Map.FitWorld();

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        Map.ShowAllLines = LinesCheck.IsChecked == true;
        Map.ShowNumbers = NumbersCheck.IsChecked == true;
    }

    private void Map_PointerMoved(object? sender, Vector2? point)
    {
        if (_tree is null) return;
        if (point is not { } p)
        {
            HoverText.Text = L.T("SimplexTreeWindow.DragWheelClick");
            return;
        }
        var (area, path) = _tree.Find(p);
        HoverText.Text = area >= 0
            ? L.T("SimplexTreeWindow.HoverInArea", p.X, p.Y, area, path.Count)
            : L.T("SimplexTreeWindow.HoverNoArea", p.X, p.Y, path.Count);
    }

    private void Map_MapClicked(object? sender, Vector2 point)
    {
        if (_tree is null) return;
        var (area, path) = _tree.Find(point);
        ShowPiece(point, area, path);
        _selecting = true;
        AreaGrid.SelectedItem = (AreaGrid.ItemsSource as List<AreaRow>)?.FirstOrDefault(a => a.Area == area);
        if (AreaGrid.SelectedItem is not null) AreaGrid.ScrollIntoView(AreaGrid.SelectedItem);
        _selecting = false;
        Map.Select(area, path.Count > 0 ? path[^1] : -1);
    }

    /// <summary>A piece clicked on: its area, and the splits passed on the way down to it, each with the side taken.</summary>
    private void ShowPiece(Vector2 point, int area, List<int> path)
    {
        PieceTitle.Text = area >= 0 ? L.T("SimplexTreeWindow.AreaNumber", area) : L.T("SimplexTreeWindow.NoArea");
        PieceDetail.Text = L.T("SimplexTreeWindow.PieceDetail", point.X, point.Y, path.Count);
        PiecePath.Text = string.Join("\n", path.Select(i =>
        {
            var split = _tree!.Splits[i];
            float side = split.Side(point);
            return $"#{i,-4} {(side >= 0 ? L.T("SimplexTreeWindow.InFront") : L.T("SimplexTreeWindow.Behind")),-8} {side,9:0.00}";
        }));
        PiecePanel.Visibility = Visibility.Visible;
    }

    private void AreaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || AreaGrid.SelectedItem is not AreaRow row) return;
        Map.Select(row.Area, -1);
        var outline = Map.Cells.Where(c => c.Area == row.Area).SelectMany(c => c.Outline).ToList();
        if (outline.Count > 0) Map.FitTo(outline.Aggregate(new Vector2(float.MaxValue), Vector2.Min), outline.Aggregate(new Vector2(float.MinValue), Vector2.Max));
        PiecePanel.Visibility = Visibility.Collapsed;
    }

    private void SplitGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SplitGrid.SelectedItem is SplitRow row) Map.Select(Map.SelectedArea, row.Index);
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void CopyCentre_Click(object sender, RoutedEventArgs e)
    {
        if (AreaGrid.SelectedItem is AreaRow row) Copy(row.CentreText);
    }

    private void CopyOutlines_Click(object sender, RoutedEventArgs e)
    {
        if (AreaGrid.SelectedItem is not AreaRow row) return;
        Copy(string.Join(Environment.NewLine, Map.Cells.Where(c => c.Area == row.Area)
            .Select(c => string.Join("  ", c.Outline.Select(p => $"{p.X:0.00},{p.Y:0.00}")))));
    }

    /// <summary>Says where the tree's properties come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CResourceSimplexTree") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("SimplexTreeWindow.SimplexTree3"));
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.OriginalSource is TextBox) return;
        if (e.Key == Key.F)
        {
            Map.FitToView();
            e.Handled = true;
        }
        else if (e.Key == Key.W)
        {
            Map.FitWorld();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
