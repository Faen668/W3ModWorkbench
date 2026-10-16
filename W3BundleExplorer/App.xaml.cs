using System.Windows;

namespace W3BundleExplorer;

public partial class App : Application
{
    public static Settings Settings { get; } = Settings.Load();

    /// <param name="theme">System, Light or Dark.</param>
    /// <param name="preset">The name of a <see cref="ThemePreset"/>.</param>
    public static void ApplyTheme(string theme, string preset) => Themes.Apply(theme, preset);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(Settings.Theme, Settings.ThemePreset);

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
                    MessageBox.Show(args.Exception.Message, "W3 Bundle Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                reporting = false;
            }
        };
        new MainWindow(e.Args).Show();
    }
}
