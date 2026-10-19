using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a scene (.w2scene) as its flow: every part as a card in the order the flow reaches it, sections with their
/// lines in words (from the game's strings, in the language picked), choices with their options, branches with their
/// conditions, and links to follow from one part to the next. Who's in it and the files it uses are listed beside, and
/// open in their viewers where the tool has one, cutscenes included.
/// </summary>
public partial class SceneWindow : Window, IMessageHost
{
    // the language the lines were last shown in, kept for the next scene opened
    private static string _language = "en";

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private SceneFile? _scene;
    private GameStringIndex? _strings;
    private List<SceneNodeItem> _items = [];
    private readonly Stack<SceneNode> _back = new();
    private bool _syncing;
    private int _stringsLoad;

    private SceneWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        Title = $"{Path.GetFileName(path)} - Scene";
        InfoText.Text = $"{path}   ·   {source}";
        LanguageBox.ItemsSource = W3Strings.Languages;
        LanguageBox.SelectedItem = W3Strings.Language(_language) ?? W3Strings.Languages[0];
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => StopReading();
    }

    /// <summary>Whether a file's name says it's a scene this window can show.</summary>
    public static bool CanShow(string name) => SceneFile.IsScene(name);

    /// <summary>Opens a scene in a window of its own. The files it uses are looked for with <paramref name="locator"/>.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner) =>
        new SceneWindow(path, source, () => data, locator).Standalone(owner).Show();

    /// <summary>Opens a scene file on disk: one in the project's game files, or one extracted from a bundle.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new SceneWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    /// <summary>
    /// The words of the game's strings in the language last picked here, for the preview pane; or why there are none
    /// (no game folder, or the files couldn't be read).
    /// </summary>
    public static async Task<(Func<uint, string?>? Text, string? Note)> LinesAsync()
    {
        if (GameLocator.Resolve(null) is not { } game) return (null, "The game folder isn't set, so lines show as string IDs.");
        if (W3Strings.Language(_language) is not { } language) return (null, null);
        try
        {
            var strings = await GameStringIndex.GetAsync(game, language);
            return (id => strings.WithId(id) is { Count: > 0 } found ? found[^1].Text : null, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "The game's strings could not be read, so lines show as string IDs.");
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _scene = await Task.Run(() => SceneFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The scene could not be read.\n\n" + ex.Message;
            return;
        }
        var s = _scene;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        LinkList.ItemsSource = s.Links.Select(l => new CutsceneLinkItem(l)).ToList();
        var spoken = s.Lines.GroupBy(l => l.Speaker ?? "").ToDictionary(g => g.Key, g => g.Count());
        ActorList.ItemsSource = s.Actors.Select(a => new SceneActorItem(a, spoken.GetValueOrDefault(a.Name))).ToList();
        Rebuild();
        int lines = s.Lines.Count(), choices = s.Nodes.SelectMany(n => n.Elements).Count(e => e.Kind == SceneElementKind.Choice);
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {s.File.Version}, {Format.Count(s.Nodes.Count, "part")}, "
                      + $"{Format.Count(lines, "line")}, {Format.Count(choices, "choice")}, {Format.Count(s.Actors.Count, "actor")}";
        await LoadStringsAsync();
    }

    /// <summary>Reads the game's strings in the language picked, then shows the lines in words.</summary>
    private async Task LoadStringsAsync()
    {
        if (_scene is null || LanguageBox.SelectedItem is not W3Language language) return;
        int load = ++_stringsLoad;
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            SearchPlaceholder.Text = "The game folder isn't set, so lines show as string IDs. Set it in Settings.";
            return;
        }
        SearchPlaceholder.Text = $"Reading the {language.Handle} strings…";
        try
        {
            var index = await GameStringIndex.GetAsync(game, language);
            if (load != _stringsLoad) return;
            _strings = index;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SearchPlaceholder.Text = "The game's strings could not be read, so lines show as string IDs.";
            return;
        }
        SearchPlaceholder.Text = "Search lines, speakers and sections  (Ctrl+F)";
        Rebuild();
    }

    private string? Text(uint id) => _strings?.WithId(id) is { Count: > 0 } found ? found[^1].Text : null;

    /// <summary>Makes the cards again, after the strings arrive or the pauses are shown or hidden, keeping the one selected.</summary>
    private void Rebuild()
    {
        if (_scene is null) return;
        var selected = (Cards.SelectedItem as SceneNodeItem)?.Node;
        var speakers = SceneNodeItem.SpeakerBrushes(_scene);
        _items = _scene.Nodes.Select(n => new SceneNodeItem(n, Text, PausesToggle.IsChecked == true, speakers)).ToList();
        FlowList.ItemsSource = _items;
        ApplyFilter();
        if (selected is not null) Select(selected, scroll: false);
    }

    // ---------------------------------------------------------------- search

    private void ApplyFilter()
    {
        string search = SearchBox.Text.Trim();
        Cards.ItemsSource = search.Length == 0 ? _items : _items.Where(i => i.Matches(search)).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not W3Language language) return;
        _language = language.Handle;
        if (IsLoaded) _ = LoadStringsAsync();
    }

    private void Pauses_Click(object sender, RoutedEventArgs e) => Rebuild();

    // ---------------------------------------------------------------- following the flow

    /// <summary>Selects a part's card and scrolls to it, showing all the cards again if the search hides it.</summary>
    private void Select(SceneNode node, bool scroll = true)
    {
        var item = _items.FirstOrDefault(i => i.Node == node);
        if (item is null) return;
        if (Cards.ItemsSource is List<SceneNodeItem> shown && !shown.Contains(item)) SearchBox.Clear();
        _syncing = true;
        Cards.SelectedItem = item;
        FlowList.SelectedItem = item;
        _syncing = false;
        if (!scroll) return;
        Cards.ScrollIntoView(item);
        FlowList.ScrollIntoView(item);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        // an option's button carries its link item, a part's way out the link itself
        var link = (sender as FrameworkElement)?.Tag switch { SceneLinkItem item => item.Link, SceneLink l => l, _ => null };
        if (link?.Target is not { } target) return;
        if (FindCard(sender as DependencyObject) is { } from) _back.Push(from.Node);
        BackButton.IsEnabled = _back.Count > 0;
        Select(target);
        // reading through, the way picked is where it goes on
        if (_reading) _ = ReadFromAsync(target);
    }

    /// <summary>The card a button is on.</summary>
    private static SceneNodeItem? FindCard(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ListBoxItem { DataContext: SceneNodeItem item }) return item;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_back.Count == 0) return;
        Select(_back.Pop());
        BackButton.IsEnabled = _back.Count > 0;
    }

    private void FlowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FlowList.SelectedItem is not SceneNodeItem item) return;
        Select(item.Node);
    }

    // ---------------------------------------------------------------- voices

    private bool _reading;
    private CancellationTokenSource? _playing;
    private System.Media.SoundPlayer? _player;
    private readonly Random _random = new();

    /// <summary>The speech files in the language picked, then the English ones for lines it has no voice for.</summary>
    private async Task<List<W3Speech>> SpeechAsync()
    {
        if (GameLocator.Resolve(null) is not { } game) return [];
        var language = LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];
        var speech = await W3Speech.ForLanguage(game, language);
        // the game installs one language's voices (and a mod may bring a few lines in others), so a line not voiced in
        // the language picked is looked for in English after
        if (W3Strings.Language("en") is { } en && en != language) speech = [.. speech, .. await W3Speech.ForLanguage(game, en)];
        return speech;
    }

    /// <summary>
    /// Plays a line's voice and waits for it to end. A line with no voice is shown for a moment instead. False when
    /// it was stopped.
    /// </summary>
    private async Task<bool> PlayAsync(SceneLineRow row, CancellationToken token)
    {
        row.IsPlaying = true;
        try
        {
            var speech = await SpeechAsync();
            byte[]? wav = row.StringId is not { } id ? null : await Task.Run(() =>
            {
                if (speech.FirstOrDefault(s => s.Has(id))?.Sound(id) is not { } riff) return null;
                var (samples, channels) = WwiseOpus.Decode(riff);
                return WwiseOpus.ToWav(samples, channels);
            }, token);
            if (token.IsCancellationRequested) return false;
            if (wav is null)
            {
                await Task.Delay(1500, token);
                return true;
            }
            using var player = new System.Media.SoundPlayer(new MemoryStream(wav));
            _player = player;
            using (token.Register(player.Stop))
                await Task.Run(player.PlaySync);
            return !token.IsCancellationRequested;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
        finally
        {
            row.IsPlaying = false;
            _player = null;
        }
    }

    /// <summary>Stops what's playing; the next play starts afresh.</summary>
    private CancellationToken Restart()
    {
        _playing?.Cancel();
        _player?.Stop();
        _playing = new CancellationTokenSource();
        return _playing.Token;
    }

    private void PlayLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SceneLineRow row) return;
        StopReading();
        _ = PlayAsync(row, Restart());
    }

    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) StopReading();
        else if (((Cards.SelectedItem as SceneNodeItem)?.Node ?? _scene?.Nodes.FirstOrDefault()) is { } from) _ = ReadFromAsync(from);
    }

    /// <summary>
    /// Plays the voices from a part on, following the flow: through sections, scripts and links, a random way at a
    /// random pick. At a choice or a condition it waits for a way to be clicked, and it stops at an end.
    /// </summary>
    private async Task ReadFromAsync(SceneNode node)
    {
        var token = Restart();
        SetReading(true);
        for (int hops = 0; hops < 1000; hops++)
        {
            Select(node);
            if (_items.FirstOrDefault(i => i.Node == node) is { } item)
                foreach (var row in item.Rows.OfType<SceneLineRow>())
                    if (!await PlayAsync(row, token)) return;
            if (token.IsCancellationRequested) return;
            bool waits = node.Kind is SceneNodeKind.Condition or SceneNodeKind.Switch || node.Elements.Any(e => e.Options.Count > 0);
            if (waits)
            {
                ReadLabel.Text = "Pick a way…";
                return; // a click on one of its links goes on
            }
            var ways = node.Next.Where(l => l.Target is not null).ToList();
            if (ways.Count == 0) break;
            node = node.Kind == SceneNodeKind.Random ? ways[_random.Next(ways.Count)].Target! : ways[0].Target!;
        }
        SetReading(false);
    }

    private void StopReading()
    {
        _playing?.Cancel();
        _player?.Stop();
        SetReading(false);
    }

    private void SetReading(bool reading)
    {
        _reading = reading;
        ReadIcon.Text = char.ConvertFromUtf32(reading ? 0xE71A : 0xE768); // stop, play
        ReadLabel.Text = reading ? "Stop" : "Read through";
    }

    // ---------------------------------------------------------------- files

    private void File_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenLinkAsync(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy((sender as FrameworkElement)?.Tag as string);

    /// <summary>
    /// Opens a file the scene uses in its viewer, found the way the game would find it. A kind of file the tool can't
    /// show has its path copied instead.
    /// </summary>
    private async Task OpenLinkAsync(string path)
    {
        bool viewable = EntityWindow.CanShow(path) || MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path);
        if (!viewable)
        {
            string kind = FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "this kind of";
            Copy(path, $"There's no viewer for {kind} files yet, so its path is copied:\n{path}");
            return;
        }
        byte[]? data;
        ArchiveEntry? entry;
        try
        {
            (data, entry) = await Task.Run(() => (_locator.ReadFile(path), _locator.Find(path)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowMessage("It could not be read", $"{path}\n\n{ex.Message}", MessageKind.Error);
            return;
        }
        if (data is null)
        {
            await ShowMessage("Not found", $"{path}\n\nIt isn't in the open project, the archives open in the tool, or the game's bundles.", MessageKind.Warning);
            return;
        }
        string source = entry is null ? "project" : entry.Archive.Name;
        string name = Path.GetFileName(path);
        if (EntityWindow.CanShow(path)) EntityWindow.Open(path, source, data, _locator, this);
        else if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, _locator, this);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.Open(path, source, data, _locator, this);
        else if (CanShow(path)) Open(path, source, data, _locator, this);
        else if (XmlWindow.CanOpen(path)) XmlWindow.OpenData(name, $"{source}  ·  {path}", data, this, path);
        else if (!TextureWindow.Open(name, $"{source}  ·  {path}", data, this))
            await ShowMessage("It can't be shown", $"{path}\n\nIt isn't a picture the texture viewer understands.", MessageKind.Warning);
    }

    private void Copy(string? text, string? message = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            // a short "Copied" goes by itself; a longer message waits for OK
            _ = Message.Show(null, message ?? $"Copied {text}", MessageKind.Info, primary: message is null ? null : "OK", autoClose: message is null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    /// <summary>A list inside the scrolling side panel passes the wheel on to the panel, as it never scrolls itself.</summary>
    private void Nested_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = (UIElement)((FrameworkElement)sender).Parent;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || Message.IsOpen) return;
        GoBack();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused) Read_Click(this, e);
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) GoBack();
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }
}
