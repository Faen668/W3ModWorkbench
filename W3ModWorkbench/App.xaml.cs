using System.Windows;

namespace W3ModWorkbench;

public partial class App : Application
{
    public static Settings Settings { get; } = Settings.Load();

    /// <param name="theme">System, Light or Dark.</param>
    /// <param name="preset">The name of a <see cref="ThemePreset"/>.</param>
    public static void ApplyTheme(string theme, string preset) => Themes.Apply(theme, preset);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // run from a script (--to-json, --from-json, --edit): do the work and exit, with no window
        if (CommandLine.Handles(e.Args))
        {
            Core.Scripts.GameScripts.GameFolder = Settings.GameFolder;
            Shutdown(CommandLine.Run(e.Args));
            return;
        }
        // a link to a game file, from a web page (w3modworkbench://asset?path=…): the copy already running shows it, when there is one
        if (e.Args is [var arg] && AppLinks.Parse(arg) is not null && AppLinks.SendToRunning(arg))
        {
            Shutdown(0);
            return;
        }
        ApplyTheme(Settings.Theme, Settings.ThemePreset);

        // F1 in any window opens the guide at that window's page (the main window opens it at the start)
        EventManager.RegisterClassHandler(typeof(Window), System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler((sender, args) =>
            {
                if (args.Key != System.Windows.Input.Key.F1 || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None
                    || sender is not Window window || window is Help.HelpWindow || args.Handled) return;
                Help.HelpWindow.ShowHelpFor(window);
                args.Handled = true;
            }));

        // Text boxes give everything inside them the text cursor, their scroll bars included.
        // Answer the cursor query for scroll bars before the text box does; a ScrollBar style here
        // would replace the theme's own scroll bar look.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ScrollBar), System.Windows.Input.Mouse.QueryCursorEvent,
            new System.Windows.Input.QueryCursorEventHandler((_, args) =>
            {
                args.Cursor = System.Windows.Input.Cursors.Arrow;
                args.Handled = true;
            }));
        Core.Scripts.GameScripts.GameFolder = Settings.GameFolder;
        bool reporting = false;
        DispatcherUnhandledException += async (_, args) =>
        {
            args.Handled = true;
            // the UI keeps running while the error shows, and can hit the same error again: report it once
            if (reporting) return;
            reporting = true;
            try
            {
                // in the window in use; a native box only when there's none to show it in
                var window = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow;
                if (window is IMessageHost host && window.IsLoaded && window.IsVisible)
                    await host.ShowMessage("Something went wrong", args.Exception.Message, MessageKind.Error);
                else
                    MessageBox.Show(args.Exception.Message, "W3 Mod Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // failing to show it: thrown on out of this async void handler, it would come straight back here, forever
            }
            finally
            {
                reporting = false;
            }
        };
        var main = new MainWindow(e.Args);
        main.Show();
        AppLinks.Listen(link => Dispatcher.InvokeAsync(() => main.OpenLink(link)));
        AppLinks.KeepRegistrationCurrent();
    }
}
