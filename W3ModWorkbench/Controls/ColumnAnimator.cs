using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace W3ModWorkbench;

/// <summary>
/// Slides a grid column's width from one size to another, easing out, as a side pane opens or closes. With Windows'
/// animations turned off it's set at once. A newer slide of the same column takes over from where the older one got to.
/// </summary>
public static class ColumnAnimator
{
    public const double Milliseconds = 180;

    private static readonly Dictionary<ColumnDefinition, EventHandler> Running = [];

    /// <param name="done">Called once the column is at its width, also when it's set at once: where a least width goes back on,
    /// as the column's MinWidth is taken off for the slide.</param>
    public static void Slide(ColumnDefinition column, double to, Action? done = null)
    {
        Stop(column);
        double from = column.ActualWidth;
        if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - to) < 1)
        {
            column.MinWidth = 0;
            column.Width = new GridLength(to);
            done?.Invoke();
            return;
        }

        // the column may go below its usual least width while it moves
        column.MinWidth = 0;
        column.Width = new GridLength(from);
        var watch = Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, watch.Elapsed.TotalMilliseconds / Milliseconds);
            double eased = 1 - Math.Pow(1 - t, 3);
            column.Width = new GridLength(Math.Max(0, from + (to - from) * eased));
            if (t < 1) return;
            Stop(column);
            done?.Invoke();
        };
        Running[column] = tick;
        CompositionTarget.Rendering += tick;
    }

    private static void Stop(ColumnDefinition column)
    {
        if (!Running.Remove(column, out var tick)) return;
        CompositionTarget.Rendering -= tick;
    }
}
