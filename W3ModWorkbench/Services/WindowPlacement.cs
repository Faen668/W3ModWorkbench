using System.Windows;

namespace W3ModWorkbench;

/// <summary>
/// Places tool windows on their own: no owner, so each has its own taskbar button and stays put when the
/// main window is minimised, closed behind or covered. They still open centred over the window they came from.
/// </summary>
public static class WindowPlacement
{
    public static T Standalone<T>(this T window, Window? from) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = true;
        window.Loaded += (_, _) => CentreOver(window, from);
        return window;
    }

    /// <summary>
    /// A standalone tool window that opens where it was last left, the same size (and maximised if it was), when
    /// that's still on a screen; otherwise centred over the window it came from, as <see cref="Standalone"/> does.
    /// </summary>
    public static T Remembered<T>(this T window, Window? from, string key) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = true;
        var bounds = App.Settings.WindowBounds;
        bool placed = false, maximized = false;
        if (bounds.TryGetValue(key, out string? saved) && saved.Split(',') is [var l, var t, var w, var h, var m]
            && double.TryParse(l, System.Globalization.CultureInfo.InvariantCulture, out double left) && double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out double top)
            && double.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out double width) && double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out double height)
            && width >= window.MinWidth && height >= window.MinHeight
            && new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                .IntersectsWith(new Rect(left + 40, top, Math.Max(1, width - 80), 40)))
        {
            (window.Left, window.Top, window.Width, window.Height) = (left, top, width, height);
            placed = true;
            maximized = m == "1";
        }
        window.Loaded += (_, _) =>
        {
            if (!placed) CentreOver(window, from);
            else if (maximized) window.WindowState = WindowState.Maximized;
        };
        window.Closing += (_, _) =>
        {
            var r = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
            if (r.IsEmpty) return;
            bounds[key] = string.Join(',', new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))
                + (window.WindowState == WindowState.Maximized ? ",1" : ",0");
            App.Settings.Save();
        };
        return window;
    }

    private static void CentreOver(Window window, Window? from)
    {
        double w = window.ActualWidth, h = window.ActualHeight;
        Rect area = from is { IsLoaded: true }
            ? (from.WindowState == WindowState.Normal ? new Rect(from.Left, from.Top, from.ActualWidth, from.ActualHeight) : from.RestoreBounds)
            : new Rect(SystemParameters.WorkArea.Location, SystemParameters.WorkArea.Size);
        double left = area.Left + (area.Width - w) / 2, top = area.Top + (area.Height - h) / 2;
        var screen = SystemParameters.WorkArea;
        window.Left = Math.Max(screen.Left, Math.Min(left, screen.Right - w));
        window.Top = Math.Max(screen.Top, Math.Min(top, screen.Bottom - h));
    }
}
