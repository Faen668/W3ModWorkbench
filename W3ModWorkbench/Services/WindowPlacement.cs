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
