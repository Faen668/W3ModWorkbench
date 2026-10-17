using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

/// <summary>A search result: one string, shown on a single line.</summary>
public sealed class GameStringRow(GameString s)
{
    public uint Id => s.Id;
    public string Hash => s.KeyHash == 0 ? "" : s.KeyHash.ToString("x8");
    public string Text { get; } = s.Text.ReplaceLineEndings(" ⏎ ");
    public string Source => s.Source;
    public GameString String => s;
}

/// <summary>
/// Searches the text of the game's own strings, and the DLC's and installed mods', in one language: what a
/// modder does to find the ID or key of a line the game already has, instead of making a new one.
/// </summary>
public partial class GameStringsWindow : Window, IMessageHost
{
    private const int MaxShown = 2000;

    private readonly string _gameFolder;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private GameStringIndex? _index;
    private int _loadId;
    private bool _ready;

    private GameStringsWindow(string gameFolder, string handle)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _gameFolder = gameFolder;
        LanguageBox.ItemsSource = W3Strings.Languages;
        LanguageBox.SelectedItem = W3Strings.Language(handle) ?? W3Strings.Language("en");
        _ready = true;
        Grid.SelectionChanged += (_, _) => CopyIdButton.IsEnabled = CopyScriptButton.IsEnabled = Grid.SelectedItem is not null;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSearch();
        };
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            _ = LoadAsync();
        };
    }

    /// <summary>Opens the window for the game folder, or says what's missing when there isn't one.</summary>
    public static void Open(Window owner, string gameFolder, string handle = "en", string? query = null)
    {
        if (!GameLocator.IsGameFolder(gameFolder))
        {
            if (owner is IMessageHost host)
                _ = host.ShowMessage("The game folder isn't set", "Searching the game's strings needs The Witcher 3's folder. Set it in Settings, or let Settings find it.", MessageKind.Info);
            return;
        }
        var window = new GameStringsWindow(gameFolder, handle) { Owner = owner };
        if (query is not null) window.SearchBox.Text = query;
        window.Show();
    }

    private W3Language Language => LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        _index = null;
        Grid.ItemsSource = null;
        StatusText.Text = $"Reading the {Language.Handle} strings…";
        try
        {
            var index = await GameStringIndex.GetAsync(_gameFolder, Language, ModsCheck.IsChecked == true);
            if (id != _loadId) return;
            _index = index;
            string sources = index.Sources.Count == 0
                ? $"No {Language.Handle}.w3strings files found under the game folder"
                : $"{Format.Count(index.All.Count, "string")} from {Format.Count(index.Sources.Count, "file")}";
            DetailText.Text = sources + (index.Problems.Count > 0 ? $"  ·  {Format.Count(index.Problems.Count, "file")} couldn't be read" : "");
            RunSearch();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void RunSearch()
    {
        if (_index is null) return;
        string query = SearchBox.Text;
        if (query.Trim().Length == 0)
        {
            Grid.ItemsSource = null;
            StatusText.Text = $"{Format.Count(_index.All.Count, "string")} ready to search";
            return;
        }
        var matches = _index.Search(query, WholeWordCheck.IsChecked == true).Take(MaxShown + 1).ToList();
        bool more = matches.Count > MaxShown;
        Grid.ItemsSource = matches.Take(MaxShown).Select(m => new GameStringRow(m)).ToList();
        StatusText.Text = matches.Count == 0 ? "Nothing matches"
            : more ? $"First {MaxShown:N0} matches shown: add more words to narrow it down"
            : matches.Count == 1 ? "1 match" : $"{matches.Count:N0} matches";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) RunSearch();
    }

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && IsLoaded) _ = LoadAsync();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && IsLoaded) _ = LoadAsync();
    }

    // ---------------------------------------------------------------- copying

    private IEnumerable<GameString> Selected() => Grid.SelectedItems.OfType<GameStringRow>().Select(r => r.String);

    private void Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            _ = Message.Show(null, $"Copied {what}.", MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected().ToList();
        if (chosen.Count > 0) Copy(string.Join(Environment.NewLine, chosen.Select(s => s.Id)), chosen.Count == 1 ? "the ID" : $"{chosen.Count} IDs");
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected().ToList();
        if (chosen.Count > 0) Copy(string.Join(Environment.NewLine, chosen.Select(s => s.Text)), chosen.Count == 1 ? "the text" : $"{chosen.Count} texts");
    }

    private void CopyScript_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected().ToList();
        if (chosen.Count > 0)
            Copy(string.Join(Environment.NewLine, chosen.Select(s => $"GetLocStringById({s.Id})")), "GetLocStringById");
    }

    private void CopyCsv_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected().ToList();
        if (chosen.Count == 0) return;
        Copy(string.Join(Environment.NewLine, chosen.Select(s =>
            $"{s.Id.ToString(CultureInfo.InvariantCulture)}|{(s.KeyHash == 0 ? "" : s.KeyHash.ToString("x8"))}||{s.Text}")), "as strings CSV lines");
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is GameStringRow row) Copy(row.Id.ToString(CultureInfo.InvariantCulture), "the ID");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Grid.IsKeyboardFocusWithin)
            CopyId_Click(sender, e);
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
