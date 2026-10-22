using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W3ModWorkbench.Core.Compare;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A row of the comparison tree, as it's shown: indented, with each side's value and how they differ.</summary>
public sealed class CompareRow(DiffNode node, int depth, bool expanded, bool swapped)
{
    public DiffNode Node => node;
    public int Depth => depth;
    public string Name => node.Name;
    public string Left => Value(swapped ? node.Right : node.Left, swapped ? node.Left : node.Right);
    public string Right => Value(swapped ? node.Left : node.Right, swapped ? node.Right : node.Left);

    /// <summary>
    /// A side's value: an object's class (or a list's length) only where it tells something, which it doesn't when both
    /// sides have the same; a dash where that side hasn't got it.
    /// </summary>
    private string Value(string? mine, string? theirs) =>
        node.Children.Count > 0 ? (mine == theirs ? "" : mine ?? "–") : mine ?? "–";
    public Thickness Indent => new(depth * 16, 1, 0, 1);
    public string Chevron => node.Children.Count == 0 ? "" : expanded ? "" : "";
    public FontWeight Weight => node.Children.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public string Tooltip => $"{node.Name}\n{Left}\n{Right}";

    private DiffState State => swapped ? node.State switch { DiffState.Added => DiffState.Removed, DiffState.Removed => DiffState.Added, var s => s } : node.State;

    /// <summary>A faint wash: amber for changed, red for only on the left, green for only on the right.</summary>
    public Brush Tint => State switch
    {
        DiffState.Changed when node.Children.Count == 0 || node.Left != node.Right => Wash(Color.FromRgb(0xE0, 0xA0, 0x30)),
        DiffState.Removed => Wash(Color.FromRgb(0xE0, 0x50, 0x50)),
        DiffState.Added => Wash(Color.FromRgb(0x40, 0xB0, 0x60)),
        _ => Brushes.Transparent,
    };

    public Brush LeftBrush => State == DiffState.Added ? Faint : Normal;
    public Brush RightBrush => State == DiffState.Removed ? Faint : Normal;

    private static Brush Normal => (Brush)Application.Current.FindResource("TextFillColorPrimaryBrush");
    private static Brush Faint => (Brush)Application.Current.FindResource("TextFillColorTertiaryBrush");

    private static SolidColorBrush Wash(Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Two versions of a file compared: CR2W files and strings as a tree of what's in them, each value on its side, the
/// differences washed in colour (only them, unless asked for everything); textures as two pictures, or where they
/// differ. F8 and Shift+F8 move between differences.
/// </summary>
public partial class CompareWindow : Window, IMessageHost
{
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DiffNode? _root;
    private readonly HashSet<DiffNode> _expanded = [];
    private List<CompareRow> _rows = [];
    private bool _swapped;
    private string _leftLabel = "", _rightLabel = "";
    private DecodedImage? _leftImage, _rightImage;
    private string _leftInfo = "", _rightInfo = "";

    private CompareWindow()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refresh();
        };
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    /// <summary>Two files as a tree: a CR2W file's objects and properties, or a strings file's strings.</summary>
    public static void ShowTree(Window? owner, string title, string kind, string leftLabel, string rightLabel, DiffNode root)
    {
        var window = new CompareWindow { _root = root, _leftLabel = leftLabel, _rightLabel = rightLabel };
        window.Title = L.T("CompareWindow.TitleOf", title);
        window.TitleText.Text = title;
        window.KindText.Text = kind;
        // what differs, opened to the first level of what's in it
        foreach (var node in root.Children.Where(c => c.State != DiffState.Same)) window._expanded.Add(node);
        window.Refresh();
        window.Standalone(owner).Show();
    }

    /// <summary>Two pictures side by side (a texture's top level), and where they differ.</summary>
    public static void ShowPictures(Window? owner, string title, string leftLabel, string rightLabel, DecodedImage? left, string leftInfo, DecodedImage? right, string rightInfo)
    {
        var window = new CompareWindow { _leftLabel = leftLabel, _rightLabel = rightLabel, _leftImage = left, _rightImage = right, _leftInfo = leftInfo, _rightInfo = rightInfo };
        window.Title = L.T("CompareWindow.TitleOf", title);
        window.TitleText.Text = title;
        window.KindText.Text = L.T("CompareWindow.TextureKind");
        window.TreeCard.Visibility = window.TreeTools.Visibility = Visibility.Collapsed;
        window.PreviousButton.Visibility = window.NextButton.Visibility = Visibility.Collapsed;
        window.PictureCard.Visibility = window.PictureTools.Visibility = Visibility.Visible;
        window.ShowPictures();
        window.Standalone(owner).Show();
    }

    // ---------------------------------------------------------------- the tree

    private void Refresh()
    {
        if (_root is null) return;
        LeftHeader.Text = _swapped ? _rightLabel : _leftLabel;
        RightHeader.Text = _swapped ? _leftLabel : _rightLabel;
        bool only = OnlyDifferences.IsChecked == true;
        string query = SearchBox.Text.Trim();
        var rows = new List<CompareRow>();
        void Walk(DiffNode node, int depth)
        {
            foreach (var child in node.Children)
            {
                if (only && child.State == DiffState.Same) continue;
                if (query.Length > 0 && !Matches(child, query)) continue;
                // a search opens everything that matches
                bool open = _expanded.Contains(child) || query.Length > 0;
                rows.Add(new CompareRow(child, depth, open, _swapped));
                if (open) Walk(child, depth + 1);
            }
        }
        Walk(_root, 0);
        var selected = (Rows.SelectedItem as CompareRow)?.Node;
        _rows = rows;
        Rows.ItemsSource = rows;
        if (selected is not null && rows.FirstOrDefault(r => r.Node == selected) is { } again) Rows.SelectedItem = again;

        int differences = _root.Differences;
        StatusText.Text = differences == 0 ? L.T("CompareWindow.Identical") : L.T("CompareWindow.DifferencesCount", L.T("Count.Difference", differences));
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = differences == 0 ? L.T("CompareWindow.NoDifferences") : L.T("Common.NothingMatches");
        EmptyText.Text = differences == 0 ? L.T("CompareWindow.NoDifferencesText") : L.T("CompareWindow.NothingMatchesText");
        PreviousButton.IsEnabled = NextButton.IsEnabled = differences > 0;
    }

    private static bool Matches(DiffNode node, string query) =>
        node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (node.Left?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (node.Right?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || node.Children.Any(c => Matches(c, query));

    private void Toggle(CompareRow row)
    {
        if (row.Node.Children.Count == 0) return;
        if (!_expanded.Remove(row.Node)) _expanded.Add(row.Node);
        Refresh();
    }

    private void Rows_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Rows.SelectedItem is CompareRow row) Toggle(row);
    }

    private void Rows_KeyDown(object sender, KeyEventArgs e)
    {
        if (Rows.SelectedItem is not CompareRow row) return;
        if (e.Key == Key.Right && row.Node.Children.Count > 0 && !_expanded.Contains(row.Node) || e.Key == Key.Left && _expanded.Contains(row.Node) || e.Key == Key.Enter)
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    private void Only_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (_root is null) return;
        void Open(DiffNode node)
        {
            foreach (var child in node.Children.Where(c => c.Children.Count > 0))
            {
                _expanded.Add(child);
                Open(child);
            }
        }
        Open(_root);
        Refresh();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        _expanded.Clear();
        Refresh();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>The next (or previous) value that differs, opening what it's in.</summary>
    private void GoToDifference(int step)
    {
        if (_root is null) return;
        // every difference in tree order, with the nodes it's inside
        var all = new List<(DiffNode Node, List<DiffNode> Path)>();
        void Collect(DiffNode node, List<DiffNode> path)
        {
            foreach (var child in node.Children)
            {
                if (child.State == DiffState.Same) continue;
                bool leaf = child.Children.Count == 0 || child.State is DiffState.Added or DiffState.Removed || child.Left != child.Right;
                if (leaf) all.Add((child, path));
                if (child.Children.Count > 0 && child.State == DiffState.Changed) Collect(child, [.. path, child]);
            }
        }
        Collect(_root, []);
        if (all.Count == 0) return;
        var current = (Rows.SelectedItem as CompareRow)?.Node;
        int at = all.FindIndex(d => d.Node == current);
        int next = at < 0 ? (step > 0 ? 0 : all.Count - 1) : (at + step + all.Count) % all.Count;
        foreach (var node in all[next].Path) _expanded.Add(node);
        Refresh();
        if (_rows.FirstOrDefault(r => r.Node == all[next].Node) is { } row)
        {
            Rows.SelectedItem = row;
            Rows.ScrollIntoView(row);
        }
        StatusText.Text = L.T("CompareWindow.DifferenceOf", next + 1, all.Count);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => GoToDifference(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => GoToDifference(1);

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        _swapped = !_swapped;
        if (_root is not null) Refresh();
        else ShowPictures();
    }

    private void CopyLeft_Click(object sender, RoutedEventArgs e) => Copy((Rows.SelectedItem as CompareRow)?.Left);
    private void CopyRight_Click(object sender, RoutedEventArgs e) => Copy((Rows.SelectedItem as CompareRow)?.Right);

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompareRow row) return;
        // the names from the top down to the row
        var path = new List<string>();
        int depth = row.Depth;
        for (int i = _rows.IndexOf(row); i >= 0 && depth >= 0; i--)
            if (_rows[i].Depth == depth)
            {
                path.Insert(0, _rows[i].Name);
                depth--;
            }
        Copy(string.Join(" › ", path));
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    // ---------------------------------------------------------------- pictures

    private void PictureView_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowPictures();
    }

    private void ShowPictures()
    {
        var (left, right) = _swapped ? (_rightImage, _leftImage) : (_leftImage, _rightImage);
        var (leftInfo, rightInfo) = _swapped ? (_rightInfo, _leftInfo) : (_leftInfo, _rightInfo);
        var (leftLabel, rightLabel) = _swapped ? (_rightLabel, _leftLabel) : (_leftLabel, _rightLabel);
        bool difference = DifferenceView.IsChecked == true;
        bool sameSize = left is not null && right is not null && left.Width == right.Width && left.Height == right.Height;
        RightPictureCard.Visibility = difference ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(LeftPictureCard, difference ? 3 : 1);
        if (difference)
        {
            LeftPictureInfo.Text = sameSize ? L.T("CompareWindow.DifferenceInfo", leftLabel, rightLabel) : L.T("CompareWindow.SizesDiffer");
            LeftPicture.Source = null;
            if (sameSize)
            {
                LeftPicture.Source = Bitmap(Difference(left!, right!, out double share, out int changed));
                StatusText.Text = L.T("CompareWindow.PixelsChanged", changed, share);
            }
        }
        else
        {
            LeftPictureInfo.Text = $"{leftLabel}  ·  {leftInfo}";
            RightPictureInfo.Text = $"{rightLabel}  ·  {rightInfo}";
            LeftPicture.Source = left is null ? null : Bitmap(left);
            RightPicture.Source = right is null ? null : Bitmap(right);
            StatusText.Text = left is null || right is null ? L.T("CompareWindow.PictureMissing")
                : sameSize && left.Bgra.AsSpan().SequenceEqual(right.Bgra) ? L.T("CompareWindow.PicturesSame") : "";
        }
    }

    private static BitmapSource Bitmap(DecodedImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Where two pictures differ: the left one dimmed to grey, and each pixel that differs in magenta, brighter the more
    /// it differs (alpha counted too).
    /// </summary>
    private static DecodedImage Difference(DecodedImage a, DecodedImage b, out double share, out int changed)
    {
        var result = new byte[a.Bgra.Length];
        changed = 0;
        for (int i = 0; i < result.Length; i += 4)
        {
            int d = Math.Max(Math.Max(Math.Abs(a.Bgra[i] - b.Bgra[i]), Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1])),
                Math.Max(Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]), Math.Abs(a.Bgra[i + 3] - b.Bgra[i + 3])));
            byte grey = (byte)((a.Bgra[i] + a.Bgra[i + 1] + a.Bgra[i + 2]) / 3 / 3);
            if (d > 0)
            {
                changed++;
                byte level = (byte)Math.Min(255, 96 + d * 2);
                (result[i], result[i + 1], result[i + 2]) = (level, 0, level);
            }
            else (result[i], result[i + 1], result[i + 2]) = (grey, grey, grey);
            result[i + 3] = 255;
        }
        share = changed * 100.0 / Math.Max(1, a.Width * a.Height);
        return new DecodedImage(a.Width, a.Height, result);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F8) GoToDifference(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _root is not null) SearchBox.Focus();
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }
}
