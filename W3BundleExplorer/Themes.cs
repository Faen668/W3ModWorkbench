using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace W3BundleExplorer;

/// <summary>
/// A colour theme on top of the light or dark mode: an accent colour, and a background tint for each mode.
/// Null colours keep what Windows gives: its accent colour and the Mica window backdrop.
/// </summary>
public sealed record ThemePreset(string Name, Color? Accent, Color? DarkBackground, Color? LightBackground)
{
    public Color? Background(bool dark) => dark ? DarkBackground : LightBackground;
}

/// <summary>
/// Applies the mode (System, Light, Dark) and a <see cref="ThemePreset"/>. The Fluent theme takes its accent
/// from Windows, so a preset puts its own shades in the app's resources under every key the theme fills
/// with an accent shade, and gives windows a solid background in place of the backdrop.
/// </summary>
public static class Themes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static IReadOnlyList<ThemePreset> Presets { get; } =
    [
        new("Windows", null, null, null),
        new("Wolf", C("#6E9BD1"), C("#161A20"), C("#F1F4F8")),
        new("Cat", C("#D4A72C"), C("#1C1913"), C("#FAF6EB")),
        new("Griffin", C("#3FA7A0"), C("#121B1B"), C("#EEF7F6")),
        new("Bear", C("#D0643E"), C("#1E1614"), C("#FAF1ED")),
        new("Viper", C("#5DB35D"), C("#131A14"), C("#EFF7EF")),
        new("Manticore", C("#AE6FD8"), C("#1A1520"), C("#F6F1FA")),
        new("Graphite", C("#9AA5B4"), C("#121212"), C("#F4F4F4")),
    ];

    /// <summary>The key windows take their background from while a preset with a background is in use.</summary>
    private const string WindowBackgroundKey = "W3WindowBackgroundBrush";

    private static readonly List<object> Overridden = [];
    private static readonly Dictionary<bool, Dictionary<string, int>> AccentKeys = [];
    private static bool _hooked;

    public static string Mode { get; private set; } = "System";

    public static ThemePreset Preset { get; private set; } = Presets[0];

    public static ThemePreset Find(string name) => Presets.FirstOrDefault(p => p.Name == name) ?? Presets[0];

    /// <summary>Whether <paramref name="mode"/> ends up dark, following Windows for System.</summary>
    public static bool IsDark(string mode) => mode switch
    {
        "Dark" => true,
        "Light" => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };

    public static void Apply(string mode, string preset)
    {
        var app = Application.Current;
        Mode = mode;
        Preset = Find(preset);
        app.ThemeMode = mode switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        foreach (var key in Overridden) app.Resources.Remove(key);
        Overridden.Clear();
        bool dark = IsDark(mode);

        if (Preset.Accent is { } accent)
        {
            foreach (var (key, shade) in AccentKeysFor(dark))
                Set(key, Brush(Shade(accent, shade)));
            for (int shade = -3; shade <= 3; shade++)
                Set("SystemAccentColor" + (shade < 0 ? $"Dark{-shade}" : shade > 0 ? $"Light{shade}" : ""), Shade(accent, shade));
        }

        if (Preset.Background(dark) is { } background)
        {
            var window = Brush(background);
            var raised = Brush(Mix(background, Colors.White, dark ? 0.06 : 0.5));
            var flyout = Brush(Mix(background, Colors.White, dark ? 0.09 : 0.7));
            Set(WindowBackgroundKey, window);
            Set("ApplicationBackgroundBrush", window);
            Set("WindowBackground", window);
            Set("SolidBackgroundFillColorBaseBrush", raised);
            foreach (var key in new[] { "ContextMenuBackground", "FlyoutBackground", "ToolTipBackground", "ComboBoxDropDownBackground", "SolidBackgroundFillColorQuarternaryBrush" })
                Set(key, flyout);
        }

        foreach (Window w in app.Windows) StyleWindow(w);

        if (!_hooked)
        {
            _hooked = true;
            // windows opened later get the background as they load
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => StyleWindow((Window)sender)));
            // following Windows: a switch between light and dark needs the other mode's shades
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == "System")
                    app.Dispatcher.BeginInvoke(() => Apply(Mode, Preset.Name));
            };
        }
    }

    private static void Set(object key, object value)
    {
        Application.Current.Resources[key] = value;
        Overridden.Add(key);
    }

    /// <summary>A solid background covers the Mica backdrop; without a preset background the window keeps it.</summary>
    private static void StyleWindow(Window window)
    {
        if (Application.Current.Resources.Contains(WindowBackgroundKey))
            window.SetResourceReference(Window.BackgroundProperty, WindowBackgroundKey);
        else
            window.ClearValue(Window.BackgroundProperty);
    }

    /// <summary>
    /// The theme's brushes that hold one of Windows' accent shades, and which shade: -3 to -1 for the darker
    /// ones, 0 for the accent itself, 1 to 3 for the lighter ones. Found by comparing colours, so every
    /// control the theme colours with the accent follows the preset.
    /// </summary>
    private static Dictionary<string, int> AccentKeysFor(bool dark)
    {
        if (AccentKeys.TryGetValue(dark, out var found)) return found;
        var shades = new Dictionary<Color, int>();
        void Add(Color c, int shade) => shades.TryAdd(c, shade);
        Add(SystemColors.AccentColor, 0);
        Add(SystemColors.AccentColorLight1, 1);
        Add(SystemColors.AccentColorLight2, 2);
        Add(SystemColors.AccentColorLight3, 3);
        Add(SystemColors.AccentColorDark1, -1);
        Add(SystemColors.AccentColorDark2, -2);
        Add(SystemColors.AccentColorDark3, -3);

        found = [];
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Resources/Theme/{(dark ? "Dark" : "Light")}.xaml"),
        };
        foreach (var key in theme.Keys.OfType<string>())
        {
            object? value;
            try { value = theme[key]; }
            catch (Exception) { continue; }
            if (value is SolidColorBrush brush && shades.TryGetValue(brush.Color, out int shade)) found[key] = shade;
        }
        AccentKeys[dark] = found;
        return found;
    }

    /// <summary>The accent made lighter (positive) or darker (negative), keeping its hue and saturation.</summary>
    public static Color Shade(Color accent, int shade)
    {
        var (h, s, l) = ToHsl(accent);
        l = shade switch
        {
            > 0 => l + (1 - l) * new[] { 0, 0.25, 0.42, 0.66 }[shade],
            < 0 => l * new[] { 1, 0.8, 0.6, 0.42 }[-shade],
            _ => l,
        };
        return FromHsl(h, s, l);
    }

    /// <summary>The accent shade the theme uses for fills: lighter on dark, darker on light.</summary>
    public static Color FillShade(Color accent, bool dark) => Shade(accent, dark ? 2 : -1);

    public static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s == 0) return Color.FromRgb((byte)(l * 255), (byte)(l * 255), (byte)(l * 255));
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromRgb((byte)Math.Round(Channel(h + 1 / 3.0) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1 / 3.0) * 255));
    }
}
