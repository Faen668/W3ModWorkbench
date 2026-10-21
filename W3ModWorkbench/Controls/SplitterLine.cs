using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace W3ModWorkbench;

/// <summary>
/// Which way a splitter's line runs: down a splitter taller than it is wide, across one wider than it is tall. Most
/// splitters only say how thick they are and leave the direction to WPF, which goes by the same thing.
/// </summary>
public sealed class SplitterLine : IMultiValueConverter
{
    /// <param name="parameter">"Down" for the line that runs down, "Across" for the one that runs across.</param>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double width = values.Length > 0 && values[0] is double w ? w : 0, height = values.Length > 1 && values[1] is double h ? h : 0;
        bool down = height >= width;
        return (parameter as string == "Down") == down ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
