using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench;

/// <summary>
/// The console commands (exec functions) the game, the installed mods and the open project declare, searchable, with
/// what to type for each and a way to open where it's declared.
/// </summary>
public partial class ConsoleCommandsWindow : Window, IMessageHost
{
    private const int MaxShown = 500;

    private static ConsoleCommandsWindow? _instance;

    private readonly string? _gameFolder;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private List<ConsoleCommand> _commands = [];
    private CancellationTokenSource? _cts;

    private ConsoleCommandsWindow()
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _gameFolder = GameLocator.Resolve(null);
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshList();
        };
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            _ = LoadAsync();
        };
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _instance = null;
        };
    }

    public static void Open(Window owner)
    {
        if (_instance is null)
        {
            _instance = new ConsoleCommandsWindow().Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var options = new ScriptCorpus.Options(_gameFolder, ModsCheck.IsChecked == true);
        StatusText.Text = "Reading every script…";
        ModsCheck.IsEnabled = false;
        try
        {
            _commands = await Task.Run(() => ConsoleCommands.Find(ScriptCorpus.Gather(options, cts.Token), cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            ModsCheck.IsEnabled = true;
        }
        if (cts != _cts) return;
        if (_gameFolder is null)
            DetailText.Text = "The game folder isn't set, so only the open project's commands are listed. Set it in Settings to see the game's and the mods'.";
        else
        {
            int game = _commands.Count(c => c.File.Origin == CorpusOrigin.Game);
            DetailText.Text = $"{Format.Count(game, "command")} from the game and {Format.Count(_commands.Count - game, "command")} from the project and mods";
        }
        RefreshList();
    }

    private void RefreshList()
    {
        string[] words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = _commands.Where(c => words.All(w => c.Signature.Contains(w, StringComparison.OrdinalIgnoreCase)
                || c.File.Label.Contains(w, StringComparison.OrdinalIgnoreCase)))
            // names that start with what's typed come first
            .OrderBy(c => words.Length > 0 && c.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(MaxShown + 1).ToList();
        bool more = found.Count > MaxShown;
        var shown = found.Take(MaxShown).ToList();
        var before = ResultList.SelectedItem as ConsoleCommand;
        ResultList.ItemsSource = shown;
        if (shown.Count > 0) ResultList.SelectedIndex = before is null ? 0 : Math.Max(0, shown.IndexOf(before));
        else ShowCommand(null);
        StatusText.Text = shown.Count == 0 ? (_commands.Count == 0 ? "No commands found" : "Nothing matches")
            : more ? $"First {MaxShown} shown: add more words to narrow it down"
            : Format.Count(shown.Count, "command");
    }

    private void ShowCommand(ConsoleCommand? command)
    {
        DetailPanel.Visibility = command is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = command is null ? Visibility.Visible : Visibility.Collapsed;
        if (command is null) return;
        NameText.Text = command.Name;
        WhereText.Text = $"{command.File.File.Source}  ·  {command.File.File.Path} : {command.Line}";
        CommandBox.Text = command.Command(OptionalCheck.IsChecked == true);
        OptionalCheck.IsEnabled = command.Parameters.Any(p => p.Optional);
        ParameterList.ItemsSource = command.Parameters;
        NoParameters.Visibility = command.Parameters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Sources_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCommand(ResultList.SelectedItem as ConsoleCommand);

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSource();

    private void Optional_Click(object sender, RoutedEventArgs e) => ShowCommand(ResultList.SelectedItem as ConsoleCommand);

    private void OpenSource_Click(object sender, RoutedEventArgs e) => OpenSource();

    private void OpenSource()
    {
        if (ResultList.SelectedItem is ConsoleCommand command)
            _ = ScriptOpener.OpenAsync(command.File, command.Offset, command.Name.Length, this);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CommandBox.Text);
            _ = Message.Show(null, "Copied. Open the console in the game with ` or ~ and paste it.", MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && ResultList.SelectedIndex < ResultList.Items.Count - 1) ResultList.SelectedIndex++;
        else if (e.Key == Key.Up && SearchBox.IsKeyboardFocused && ResultList.SelectedIndex > 0) ResultList.SelectedIndex--;
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused) OpenSource();
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
