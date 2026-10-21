using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Input;
using W3ModWorkbench.Core.Scripts.Standalone;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>An input context to tick in the key binding helper.</summary>
public sealed class ContextChoice(string name, bool isChecked)
{
    public string Name { get; } = name;
    public bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// Writes what a mod needs for a key of its own: the lines for input.settings in each context, the input.xml entry that
/// shows it in the key bindings menu, and the script that listens for it. Reads the player's input.settings to say whether
/// the key is taken already, and can add the binding to it (and to the game's input.xml) to try it out.
/// </summary>
public partial class InputBindingsWindow : Window, IMessageHost
{
    private static InputBindingsWindow? _instance;
    private static List<string>? _keys;

    private readonly string? _gameFolder;
    private readonly List<ContextChoice> _contexts = [];
    private InputSettings? _settings;
    private string _settingsPath = InputSettings.DefaultPath;
    private bool _ready;

    private InputBindingsWindow()
    {
        InitializeComponent();
        _gameFolder = GameLocator.Resolve(null);
        KeyBox.ItemsSource = Keys();
        KeyBox.Text = "IK_K";
        Loaded += (_, _) =>
        {
            bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
                        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
            Output.TextArea.TextView.LineTransformers.Insert(0, new WitcherScriptColorizer(Output.Document, dark) { View = Output.TextArea.TextView });
            ActionBox.Focus();
            ActionBox.SelectAll();
        };
        Closed += (_, _) => _instance = null;
        ReadSettings();
        _ready = true;
        Refresh();
    }

    public static void Open(Window owner)
    {
        if (_instance is null)
        {
            _instance = new InputBindingsWindow().Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    /// <summary>Every key the engine knows (its EInputKey enum), keyboard and mouse first, then pads and the rest.</summary>
    private static List<string> Keys()
    {
        if (_keys is not null) return _keys;
        var all = Rtti.LoadBuiltIn().Enums.FirstOrDefault(e => e.Name == "EInputKey")?.Options.Select(o => o.Name).Where(n => n != "IK_None").ToList() ?? [];
        static bool Pad(string k) => k.StartsWith("IK_Pad", StringComparison.Ordinal) || k.StartsWith("IK_PS4", StringComparison.Ordinal)
            || k.StartsWith("IK_Joy", StringComparison.Ordinal) || k.StartsWith("IK_Pattern", StringComparison.Ordinal)
            || k.StartsWith("IK_Gyro", StringComparison.Ordinal) || k.StartsWith("IK_Jcms", StringComparison.Ordinal) || k.StartsWith("IK_Steam", StringComparison.Ordinal);
        return _keys = [.. all.Where(k => !Pad(k)), .. all.Where(Pad)];
    }

    private void ReadSettings()
    {
        var ticked = _contexts.Where(c => c.IsChecked).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ticked.Count == 0) ticked.UnionWith(InputSettings.CommonContexts.Take(5));
        try
        {
            _settings = File.Exists(_settingsPath) ? InputSettings.Read(_settingsPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings = null;
        }
        FileText.Text = _settings is null
            ? L.T("InputBindingsWindow.WasntFoundSoTaken", _settingsPath)
            : L.T("InputBindingsWindow.CheckingKeysAgainst", _settingsPath, L.T("Count.Binding", _settings.Bindings.Count), L.T("Count.Context", _settings.Contexts.Count));

        var names = new List<string>(InputSettings.CommonContexts);
        if (AllContextsCheck.IsChecked == true && _settings is not null)
            names.AddRange(_settings.Contexts.Where(c => !names.Contains(c, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
        _contexts.Clear();
        _contexts.AddRange(names.Select(n => new ContextChoice(n, ticked.Contains(n))));
        ContextList.ItemsSource = null;
        ContextList.ItemsSource = _contexts;
    }

    private NewBinding Binding()
    {
        string action = Clean(ActionBox.Text, "MyModAction");
        string key = KeyBox.Text.Trim();
        if (key.Length > 0 && !key.StartsWith("IK_", StringComparison.OrdinalIgnoreCase)) key = "IK_" + key;
        // the engine's own spelling, when it's one of its keys
        key = Keys().FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        return new NewBinding(action, key.Length > 0 ? key : "IK_K", [.. _contexts.Where(c => c.IsChecked).Select(c => c.Name)],
            DisplayBox.Text.Trim().Length > 0 ? DisplayBox.Text.Trim() : action.ToLowerInvariant(),
            HoldCheck.IsChecked == true, Clean(ListenerBox.Text, "On" + action));
    }

    private static string Clean(string text, string fallback)
    {
        string word = Regex.Replace(text.Trim(), @"[^A-Za-z0-9_]", "");
        return word.Length > 0 ? word : fallback;
    }

    private void Refresh()
    {
        if (!_ready) return;
        var b = Binding();
        string settings = b.Contexts.Count == 0 ? L.T("InputBindingsWindow.TickLeastOneContext") : InputSettings.Write(b);
        Output.Text = L.T("InputBindingsWindow.DocumentsWitcherInputSettings", settings.TrimEnd(), InputSettings.MenuEntry(b), InputSettings.Script(b));

        bool known = Keys().Contains(b.Key);
        if (!known)
        {
            KeyNote.Text = L.T("InputBindingsWindow.IsntOneEnginesKeys", b.Key);
            KeyNote.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        else if (_settings is null)
        {
            KeyNote.Text = "";
        }
        else
        {
            var taken = _settings.OnKey(b.Key, b.Contexts).Where(t => !t.Action.Equals(b.Action, StringComparison.OrdinalIgnoreCase)).ToList();
            if (taken.Count == 0)
            {
                KeyNote.Text = b.Contexts.Count == 0 ? "" : L.T("InputBindingsWindow.Free", b.Key, string.Join(", ", b.Contexts));
                KeyNote.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            }
            else
            {
                KeyNote.Text = L.T("InputBindingsWindow.AlreadyBound", b.Key, string.Join("; ", taken.GroupBy(t => t.Context)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.Action).Distinct())}")));
                KeyNote.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            }
        }

        var existing = _settings?.ForAction(b.Action) ?? [];
        ActionNote.Text = existing.Count == 0 ? ""
            : L.T("InputBindingsWindow.AlreadyIn", b.Action, string.Join(", ", existing.Select(e => L.T("InputBindingsWindow.KeyIn", e.Key, e.Context)).Distinct().Take(8)));
        AddButton.IsEnabled = b.Contexts.Count > 0 && known;
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Key_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // the text follows the pick after this event
        Dispatcher.BeginInvoke(Refresh);
    }

    private void AllContexts_Click(object sender, RoutedEventArgs e)
    {
        ReadSettings();
        Refresh();
    }

    private void ChangeFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("InputBindingsWindow.PickInputSettings"),
            Filter = L.T("InputBindingsWindow.InputSettingsInputSettings"),
            InitialDirectory = Path.GetDirectoryName(_settingsPath) is { } dir && Directory.Exists(dir) ? dir : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settingsPath = dialog.FileName;
        ReadSettings();
        Refresh();
    }

    // ---------------------------------------------------------------- copying and adding

    private void CopySettings_Click(object sender, RoutedEventArgs e) => Copy(InputSettings.Write(Binding()), "the input.settings lines");

    private void CopyMenu_Click(object sender, RoutedEventArgs e) => Copy(InputSettings.MenuEntry(Binding()), "the input.xml entry");

    private void CopyScript_Click(object sender, RoutedEventArgs e) => Copy(InputSettings.Script(Binding()), "the script");

    private void Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            _ = Message.Show(null, L.T("InputBindingsWindow.Copied", what), MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var b = Binding();
        if (!File.Exists(_settingsPath))
        {
            await ShowMessage(L.T("InputBindingsWindow.InputSettingsWasntFound"), L.T("InputBindingsWindow.DoesntExistRunGame", _settingsPath), MessageKind.Warning);
            return;
        }
        // first your own key bindings, in Documents
        if (!await Message.Show(L.T("InputBindingsWindow.AddYourKeyBindings", b.Action, b.Key),
                L.T("InputBindingsWindow.AddsLinesKeepingCopy", _settingsPath),
                MessageKind.Question, primary: L.T("Common.Add"), secondary: L.T("Common.Cancel")))
            return;
        var done = new List<string>();
        string? menu = InputSettings.MenuPath(_gameFolder);
        try
        {
            int added = InputSettings.Add(_settingsPath, b);
            done.Add(added == 0 ? L.T("InputBindingsWindow.InputSettingsHadBinding") : L.T("InputBindingsWindow.AddedInputSettings", L.T("Count.Line", added)));

            // then, only if asked separately, the game's own file, so the key shows in the key bindings menu
            if (menu is null || !File.Exists(menu))
                done.Add(L.T("InputBindingsWindow.GamesInputXmlWasnt"));
            else if (InputSettings.HasMenuEntry(menu, b.Action))
                done.Add(L.T("InputBindingsWindow.GamesInputXmlLists"));
            else if (await Message.Show(L.T("InputBindingsWindow.ChangeOneGamesFiles"),
                         L.T("InputBindingsWindow.ShowGamesKeyBindings", b.Action, menu),
                         MessageKind.Warning, primary: L.T("InputBindingsWindow.ChangeGamesInputXml"), secondary: L.T("InputBindingsWindow.Leave")))
                done.Add(InputSettings.AddToMenu(menu, b) ? L.T("InputBindingsWindow.EntryAddedGamesInput") : L.T("InputBindingsWindow.GamesInputXmlHad"));
            else
                done.Add(L.T("InputBindingsWindow.GamesInputXmlLeft"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("InputBindingsWindow.BindingCouldNotAdded"), ex.Message
                + (ex is UnauthorizedAccessException ? L.T("InputBindingsWindow.GamesFolderMayNeed") : ""), MessageKind.Warning);
            return;
        }
        ReadSettings();
        Refresh();
        await ShowMessage(L.T("InputBindingsWindow.SetUp", b.Action), L.T("InputBindingsWindow.StartGameTry", string.Join(", ", done)), MessageKind.Info);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !Message.IsOpen && !KeyBox.IsDropDownOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));
}
