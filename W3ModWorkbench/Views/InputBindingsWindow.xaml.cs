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
        DisabledLook.Apply(this);
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
            ? $"{_settingsPath} wasn't found, so taken keys can't be checked. Run the game once, or pick the file."
            : $"Checking keys against {_settingsPath}  ·  {Format.Count(_settings.Bindings.Count, "binding")} in {Format.Count(_settings.Contexts.Count, "context")}";

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
        string settings = b.Contexts.Count == 0 ? "// tick at least one context the key works in\n" : InputSettings.Write(b);
        Output.Text = $"""
            // ---- Documents\The Witcher 3\input.settings: under each heading
            {settings.TrimEnd()}

            // ---- bin\config\r4game\user_config_matrix\pc\input.xml: inside <VisibleVars>
            {InputSettings.MenuEntry(b)}

            // ---- your script
            {InputSettings.Script(b)}
            """;

        bool known = Keys().Contains(b.Key);
        if (!known)
        {
            KeyNote.Text = $"{b.Key} isn't one of the engine's keys.";
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
                KeyNote.Text = b.Contexts.Count == 0 ? "" : $"{b.Key} is free in {string.Join(", ", b.Contexts)}.";
                KeyNote.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            }
            else
            {
                KeyNote.Text = $"{b.Key} is already bound: " + string.Join("; ", taken.GroupBy(t => t.Context)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.Action).Distinct())}"))
                    + ". Both actions would fire when it's pressed.";
                KeyNote.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            }
        }

        var existing = _settings?.ForAction(b.Action) ?? [];
        ActionNote.Text = existing.Count == 0 ? ""
            : $"{b.Action} is in your input.settings already, on {string.Join(", ", existing.Select(e => $"{e.Key} in {e.Context}").Distinct().Take(8))}.";
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
            Title = "Pick input.settings",
            Filter = "Input settings (input.settings)|input.settings|Settings files (*.settings)|*.settings|All files (*.*)|*.*",
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
            _ = Message.Show(null, $"Copied {what}.", MessageKind.Info, autoClose: true);
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
            await ShowMessage("input.settings wasn't found", $"{_settingsPath} doesn't exist. Run the game once to make it, or pick the file with Change.", MessageKind.Warning);
            return;
        }
        // first your own key bindings, in Documents
        if (!await Message.Show($"Add {b.Action} on {b.Key} to your key bindings?",
                $"This adds the lines to {_settingsPath}, keeping a copy as input.settings.bak. The game must be closed: it writes input.settings when it quits.",
                MessageKind.Question, primary: "Add", secondary: "Cancel"))
            return;
        var done = new List<string>();
        string? menu = InputSettings.MenuPath(_gameFolder);
        try
        {
            int added = InputSettings.Add(_settingsPath, b);
            done.Add(added == 0 ? "input.settings had the binding already" : $"{Format.Count(added, "line")} added to input.settings");

            // then, only if asked separately, the game's own file, so the key shows in the key bindings menu
            if (menu is null || !File.Exists(menu))
                done.Add("the game's input.xml wasn't found, so the key won't show in the key bindings menu (set the game folder in Settings)");
            else if (InputSettings.HasMenuEntry(menu, b.Action))
                done.Add("the game's input.xml lists it already");
            else if (await Message.Show("Change one of the game's files?",
                         $"To show {b.Action} in the game's key bindings menu, its entry has to go in the game's own input.xml:\n\n{menu}\n\n"
                         + "This changes a file in the game folder. A copy is kept as input.xml.bak beside it, and a game update may put the original back. "
                         + "Your mod should still ship the entry itself: Copy input.xml has it.",
                         MessageKind.Warning, primary: "Change the game's input.xml", secondary: "Leave it"))
                done.Add(InputSettings.AddToMenu(menu, b) ? "the entry added to the game's input.xml" : "the game's input.xml had no list to add it to");
            else
                done.Add("the game's input.xml was left alone, so the key won't show in the key bindings menu");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The binding could not be added", ex.Message
                + (ex is UnauthorizedAccessException ? "\n\nThe game's folder may need the tool to be run as administrator." : ""), MessageKind.Warning);
            return;
        }
        ReadSettings();
        Refresh();
        await ShowMessage($"{b.Action} is set up", string.Join(", ", done) + ". Start the game to try it.", MessageKind.Info);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !Message.IsOpen && !KeyBox.IsDropDownOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
