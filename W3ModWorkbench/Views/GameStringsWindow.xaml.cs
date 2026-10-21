using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A search result: one string, shown on a single line.</summary>
public sealed class GameStringRow(GameString s)
{
    public uint Id => s.Id;
    public string Key => s.KeyName ?? (s.KeyHash == 0 ? "" : s.KeyHash.ToString("x8"));
    public string Text { get; } = s.Text.ReplaceLineEndings(" ⏎ ");
    public string Kind => s.Property ?? "";
    public string UsedIn => s.Resource ?? "";
    public string Voiceover => s.Voiceover ?? "";
    public string Source => s.Source;
    public GameString String => s;
}

/// <summary>
/// Searches the text of the game's own strings, and the DLC's and installed mods', in one language: what a
/// modder does to find the ID or key of a line the game already has, instead of making a new one. The strings
/// come from the game's .w3strings files, or from REDkit's strings database, which also names each key and says
/// where each string is used.
/// </summary>
public partial class GameStringsWindow : Window, IMessageHost
{
    private const int MaxShown = 2000;

    private readonly string? _gameFolder;
    private readonly string? _redkitDatabase = RedkitStrings.Find(App.Settings.WccLitePath);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private GameStringIndex? _index;
    private int _loadId;
    private bool _ready;

    private GameStringsWindow(string? gameFolder, string handle)
    {
        InitializeComponent();
        _gameFolder = gameFolder;
        LanguageBox.ItemsSource = W3Strings.Languages;
        LanguageBox.SelectedItem = W3Strings.Language(handle) ?? App.GameLanguage;
        GameItem.IsEnabled = gameFolder is not null;
        RedkitItem.IsEnabled = _redkitDatabase is not null;
        RedkitItem.Content = _redkitDatabase is null ? L.T("GameStringsWindow.REDkitDatabaseNotFound") : L.T("GameStringsWindow.REDkitDatabase");
        RedkitItem.ToolTip = _redkitDatabase ?? L.T("GameStringsWindow.REDkitWasntFoundSet");
        string saved = App.Settings.GameStringsSource;
        if (saved == "File" && !File.Exists(App.Settings.StringsDatabasePath)) saved = "REDkit";
        if (saved == "REDkit" && _redkitDatabase is null) saved = "Game";
        if (saved == "Game" && gameFolder is null) saved = _redkitDatabase is not null ? "REDkit" : "File";
        SourceBox.SelectedItem = SourceBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == saved);
        _lastSource = saved;
        ShowSource();
        _ready = true;
        Grid.SelectionChanged += (_, _) => CopyIdButton.IsEnabled = CopyScriptButton.IsEnabled = Grid.SelectedItem is not null;
        Grid.ContextMenuOpening += (_, _) =>
            CopyKeyItem.IsEnabled = CopyKeyScriptItem.IsEnabled = Selected().Any(s => s.KeyName is not null);
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

    /// <summary>
    /// Opens the window for the game folder, or says what's missing when there's neither it nor a REDkit strings
    /// database to read.
    /// </summary>
    /// <param name="handle">The language to start in; the one Settings names when not given.</param>
    public static void Open(Window owner, string? gameFolder, string? handle = null, string? query = null)
    {
        handle ??= App.Settings.GameLanguage;
        if (gameFolder is not null && !GameLocator.IsGameFolder(gameFolder)) gameFolder = null;
        if (gameFolder is null && RedkitStrings.Find(App.Settings.WccLitePath) is null && !File.Exists(App.Settings.StringsDatabasePath))
        {
            if (owner is IMessageHost host)
                _ = host.ShowMessage(L.T("Common.GameFolderIsntSet"), L.T("GameStringsWindow.SearchingGamesStringsNeeds"), MessageKind.Info);
            return;
        }
        var window = new GameStringsWindow(gameFolder, handle).Standalone(owner);
        if (query is not null) window.SearchBox.Text = query;
        window.Show();
    }

    private W3Language PickedLanguage => LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];

    private string SourceTag => (SourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Game";

    /// <summary>The database to read, or null for the game's .w3strings files.</summary>
    private string? Database => SourceTag switch
    {
        "REDkit" => _redkitDatabase,
        "File" => App.Settings.StringsDatabasePath,
        _ => null,
    };

    /// <summary>The database's extra columns, and what the mods box adds, follow where the strings come from.</summary>
    private void ShowSource()
    {
        bool database = SourceTag != "Game";
        var shown = database ? Visibility.Visible : Visibility.Collapsed;
        KindColumn.Visibility = UsedInColumn.Visibility = VoiceoverColumn.Visibility = shown;
        KeyColumn.Header = database ? L.T("Common.Key") : L.T("GameStringsWindow.KeyHash");
        KeyColumn.Width = database ? 220 : 90;
        ModsCheck.Content = database ? L.T("GameStringsWindow.IncludeInstalledMods") : L.T("GameStringsWindow.IncludeDLCMods");
        ModsCheck.ToolTip = database
            ? L.T("GameStringsWindow.AlsoReadStringsMods")
            : L.T("GameStringsWindow.AlsoReadStringsDLC");
        ModsCheck.IsEnabled = _gameFolder is not null;
        FileItem.Content = SourceTag == "File" && App.Settings.StringsDatabasePath.Length > 0
            ? L.T("GameStringsWindow.File", Path.GetFileName(App.Settings.StringsDatabasePath))
            : L.T("GameStringsWindow.DatabaseFile");
        SearchPlaceholder.Text = database
            ? L.T("GameStringsWindow.SearchTextIDKey2")
            : L.T("GameStringsWindow.SearchTextIDKey");
    }

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        _index = null;
        Grid.ItemsSource = null;
        string? database = Database;
        StatusText.Text = database is null ? L.T("GameStringsWindow.ReadingStrings", PickedLanguage.Handle) : L.T("GameStringsWindow.ReadingStrings2", PickedLanguage.Handle, Path.GetFileName(database));
        try
        {
            var index = await GameStringIndex.GetAsync(_gameFolder, PickedLanguage, ModsCheck.IsChecked == true && _gameFolder is not null, database);
            if (id != _loadId) return;
            _index = index;
            int files = index.Sources.Count - (database is null ? 0 : 1);
            string sources = database is not null
                ? L.T("GameStringsWindow.FromDatabase", L.T("Count.String", index.All.Count), Path.GetFileName(database)) + (files > 0 ? L.T("GameStringsWindow.AndModFiles", L.T("Count.ModFile", files)) : "")
                : index.Sources.Count == 0
                ? L.T("GameStringsWindow.NoW3stringsFilesFound", PickedLanguage.Handle)
                : L.T("GameStringsWindow.From", L.T("Count.String", index.All.Count), L.T("Count.File", index.Sources.Count));
            DetailText.Text = sources + (index.Problems.Count > 0 ? L.T("GameStringsWindow.CouldntRead", L.T("Count.File", index.Problems.Count)) : "");
            RunSearch();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (id != _loadId) return;
            StatusText.Text = database is null ? ex.Message : L.T("GameStringsWindow.CouldntRead2", Path.GetFileName(database), ex.Message);
        }
    }

    private void RunSearch()
    {
        if (_index is null) return;
        string query = SearchBox.Text;
        if (query.Trim().Length == 0)
        {
            Grid.ItemsSource = null;
            StatusText.Text = L.T("GameStringsWindow.ReadySearch", L.T("Count.String", _index.All.Count));
            return;
        }
        var matches = _index.Search(query, WholeWordCheck.IsChecked == true).Take(MaxShown + 1).ToList();
        bool more = matches.Count > MaxShown;
        Grid.ItemsSource = matches.Take(MaxShown).Select(m => new GameStringRow(m)).ToList();
        StatusText.Text = matches.Count == 0 ? L.T("Common.NothingMatches")
            : more ? L.T("GameStringsWindow.FirstMatchesShownAdd", MaxShown)
            : matches.Count == 1 ? L.T("GameStringsWindow.Match") : L.T("GameStringsWindow.Matches", matches.Count);
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

    private string _lastSource = "Game";

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SourceTag == _lastSource && SourceTag != "File") return;
        if (SourceTag == "File" && !PickDatabase())
        {
            // cancelled: back to where it was reading from
            SourceBox.SelectedItem = SourceBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == _lastSource);
            return;
        }
        _lastSource = SourceTag;
        App.Settings.GameStringsSource = SourceTag;
        App.Settings.Save();
        ShowSource();
        if (IsLoaded) _ = LoadAsync();
    }

    /// <summary>Asks for a strings database, such as a copy of REDkit's or one from a mod project.</summary>
    private bool PickDatabase()
    {
        string current = App.Settings.StringsDatabasePath;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("GameStringsWindow.PickREDkitStringsDatabase"),
            Filter = L.T("GameStringsWindow.REDkitStringsDatabaseDb"),
            InitialDirectory = Path.GetDirectoryName(current.Length > 0 ? current : _redkitDatabase ?? "") ?? "",
        };
        if (dialog.ShowDialog(this) != true) return false;
        App.Settings.StringsDatabasePath = dialog.FileName;
        return true;
    }

    // ---------------------------------------------------------------- copying

    private IEnumerable<GameString> Selected() => Grid.SelectedItems.OfType<GameStringRow>().Select(r => r.String);

    private void Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            _ = Message.Show(null, L.T("GameStringsWindow.Copied", what), MessageKind.Info, autoClose: true);
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

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var keys = Selected().Select(s => s.KeyName).OfType<string>().ToList();
        if (keys.Count > 0) Copy(string.Join(Environment.NewLine, keys), keys.Count == 1 ? "the key" : $"{keys.Count} keys");
    }

    private void CopyKeyScript_Click(object sender, RoutedEventArgs e)
    {
        var keys = Selected().Select(s => s.KeyName).OfType<string>().ToList();
        if (keys.Count > 0) Copy(string.Join(Environment.NewLine, keys.Select(k => $"GetLocStringByKeyExt(\"{k}\")")), "GetLocStringByKeyExt");
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
            s.KeyName is not null
                ? $"{s.Id.ToString(CultureInfo.InvariantCulture)}||{s.KeyName}|{s.Text}"
                : $"{s.Id.ToString(CultureInfo.InvariantCulture)}|{(s.KeyHash == 0 ? "" : s.KeyHash.ToString("x8"))}||{s.Text}")), "as strings CSV lines");
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));
}
