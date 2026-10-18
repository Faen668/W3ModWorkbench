using System.Windows.Data;

namespace W3ModWorkbench;

/// <summary>Adds a number to a size: the cell of a thumbnail is the thumbnail and its margins. The number to add is the converter parameter.</summary>
public sealed class OffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Convert.ToDouble(value, culture) + System.Convert.ToDouble(parameter, System.Globalization.CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
