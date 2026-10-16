using System.Windows;

namespace W3BundleExplorer;

public partial class App : Application
{
    public static Settings Settings { get; } = Settings.Load();

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(Settings.Theme);

        // Text boxes give everything inside them the text cursor, their scroll bars included.
        // Answer the cursor query for scroll bars before the text box does; a ScrollBar style here
        // would replace the theme's own scroll bar look.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ScrollBar), System.Windows.Input.Mouse.QueryCursorEvent,
            new System.Windows.Input.QueryCursorEventHandler((_, args) =>
            {
                args.Cursor = System.Windows.Input.Cursors.Arrow;
                args.Handled = true;
            }));
        Core.Scripts.EnumResolver.GameFolder = Settings.GameFolder;
        bool reporting = false;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // the message box keeps the UI running, which can hit the same error again: report it once
            if (reporting) return;
            reporting = true;
            MessageBox.Show(args.Exception.Message, "W3 Bundle Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            reporting = false;
        };
        new MainWindow(e.Args).Show();
    }
}
