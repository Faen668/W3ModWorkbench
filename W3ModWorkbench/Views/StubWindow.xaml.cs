using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Highlighting;

namespace W3ModWorkbench;

/// <summary>A line in the stub generator's list: a function, or a class to add to.</summary>
public sealed record StubItem(string Title, string Detail, GameFunction? Function, GameClass? Class);

/// <summary>
/// Finds one of the game's functions (or classes) and writes the starting code for changing it: a
/// <c>@wrapMethod</c> that calls the original, a <c>@replaceMethod</c> that starts from its code, or a
/// new method or field on a class.
/// </summary>
public partial class StubWindow : Window, IMessageHost
{
    private const int MaxShown = 400;

    private readonly string _scriptsFolder;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private StubGenerator? _stubs;
    private bool _ready;

    private StubWindow(string scriptsFolder)
    {
        InitializeComponent();
        _scriptsFolder = scriptsFolder;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshList();
        };
        Loaded += (_, _) =>
        {
            bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
                        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
            Output.TextArea.TextView.LineTransformers.Insert(0, new WitcherScriptColorizer(Output.Document, dark) { View = Output.TextArea.TextView });
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
                selection.Freeze();
                Output.TextArea.SelectionBrush = selection;
                Output.TextArea.SelectionBorder = null;
                Output.TextArea.SelectionForeground = null;
            }
            SearchBox.Focus();
            _ = LoadAsync();
        };
        _ready = true;
    }

    /// <summary>Opens the generator on the game's scripts, optionally with a search already typed.</summary>
    public static void Open(Window owner, string? gameFolder, string? query = null, StubKind kind = StubKind.Wrap)
    {
        string? scripts = gameFolder is null ? null : Path.Combine(gameFolder, "content", "content0", "scripts");
        if (scripts is null || !Directory.Exists(scripts))
        {
            if (owner is IMessageHost host)
                _ = host.ShowMessage("The game's scripts weren't found", "The stub generator reads the game's own scripts (content\\content0\\scripts). Set the game folder in Settings.", MessageKind.Info);
            return;
        }
        var window = new StubWindow(scripts).Standalone(owner);
        window.KindRadio(kind).IsChecked = true;
        if (query is not null) window.SearchBox.Text = query;
        window.Show();
    }

    private RadioButton KindRadio(StubKind kind) => kind switch
    {
        StubKind.Replace => ReplaceRadio,
        StubKind.AddMethod => MethodRadio,
        StubKind.AddField => FieldRadio,
        _ => WrapRadio,
    };

    private StubKind Kind => ReplaceRadio.IsChecked == true ? StubKind.Replace
        : MethodRadio.IsChecked == true ? StubKind.AddMethod
        : FieldRadio.IsChecked == true ? StubKind.AddField
        : StubKind.Wrap;

    private async Task LoadAsync()
    {
        StatusText.Text = "Reading the game's scripts…";
        _stubs = await StubGenerator.LoadAsync(_scriptsFolder);
        DetailText.Text = $"{Format.Count(_stubs.Functions.Count, "function")} in {Format.Count(_stubs.Classes.Count, "class", "classes")} read from the game's scripts";
        RefreshList();
    }

    private void RefreshList()
    {
        if (_stubs is null) return;
        string query = SearchBox.Text.Trim();
        var kind = Kind;
        List<StubItem> items;
        bool more;
        if (kind is StubKind.Wrap or StubKind.Replace)
        {
            var found = (query.Length == 0 ? _stubs.Functions : _stubs.Search(query)).Take(MaxShown + 1).ToList();
            more = found.Count > MaxShown;
            items = [.. found.Take(MaxShown).Select(f => new StubItem(f.Title, f.Signature, f, null))];
        }
        else
        {
            var found = (query.Length == 0 ? _stubs.Classes : _stubs.SearchClasses(query)).Take(MaxShown + 1).ToList();
            more = found.Count > MaxShown;
            items = [.. found.Take(MaxShown).Select(c => new StubItem(c.Name, c.Base is null ? (c.IsState ? "state" : "class") : $"extends {c.Base}", null, c))];
        }
        var before = (ResultList.SelectedItem as StubItem)?.Function;
        ResultList.ItemsSource = items;
        // switching between wrap and replace keeps the function that was picked
        int keep = before is null ? -1 : items.FindIndex(i => ReferenceEquals(i.Function, before));
        if (items.Count > 0) ResultList.SelectedIndex = Math.Max(keep, 0);
        // the selection may not have changed, so the output has to be written again for the new kind
        RefreshOutput();
        if (keep > 0) ResultList.ScrollIntoView(items[keep]);
        StatusText.Text = items.Count == 0 ? "Nothing matches"
            : more ? $"First {MaxShown} shown: add more words to narrow it down"
            : (kind is StubKind.Wrap or StubKind.Replace ? Format.Count(items.Count, "function") : Format.Count(items.Count, "class", "classes"));
    }

    private void RefreshOutput()
    {
        if (!_ready) return;
        var kind = Kind;
        bool naming = kind is StubKind.AddMethod or StubKind.AddField;
        NameRow.Visibility = naming ? Visibility.Visible : Visibility.Collapsed;
        TypeLabel.Visibility = TypeBox.Visibility = kind == StubKind.AddField ? Visibility.Visible : Visibility.Collapsed;
        BodyCheck.IsEnabled = kind == StubKind.Replace;

        string text = "";
        if (ResultList.SelectedItem is StubItem item)
        {
            var options = new StubGenerator.Options(CommentCheck.IsChecked == true, BodyCheck.IsChecked == true);
            string name = NameBox.Text.Trim();
            string type = TypeBox.Text.Trim();
            text = kind switch
            {
                StubKind.Wrap or StubKind.Replace when item.Function is { } f => StubGenerator.Write(f, kind, options),
                StubKind.AddMethod when item.Class is { } c => StubGenerator.WriteAddMethod(c.Name, name.Length > 0 ? name : "MyMethod"),
                StubKind.AddField when item.Class is { } c => StubGenerator.WriteAddField(c.Name, name.Length > 0 ? name : "myField", type.Length > 0 ? type : "bool"),
                _ => "",
            };
        }
        Output.Text = text;
        CopyButton.IsEnabled = text.Length > 0;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (Kind == StubKind.AddMethod && NameBox.Text == "myField") NameBox.Text = "MyMethod";
        if (Kind == StubKind.AddField && NameBox.Text == "MyMethod") NameBox.Text = "myField";
        SearchPlaceholder.Text = Kind is StubKind.Wrap or StubKind.Replace
            ? "Search by class or function, e.g. W3PlayerWitcher OnSpawned  (Ctrl+F)"
            : "Search for a class, e.g. CR4Player  (Ctrl+F)";
        RefreshList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshOutput();

    private void Option_Changed(object sender, RoutedEventArgs e) => RefreshOutput();

    private void Names_Changed(object sender, TextChangedEventArgs e) => RefreshOutput();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Output.Text.Replace("\n", Environment.NewLine));
            _ = Message.Show(null, "Copied to the clipboard.", MessageKind.Info, autoClose: true);
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
