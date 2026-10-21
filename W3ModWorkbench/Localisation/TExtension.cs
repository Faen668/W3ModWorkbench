using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace W3ModWorkbench.Localisation;

/// <summary>
/// A string of the app's own in XAML: <c>Text="{l:T Settings.General.Title}"</c>, with
/// <c>xmlns:l="clr-namespace:W3ModWorkbench.Localisation"</c>. It's a binding, so it changes when the language does.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Binding(Key).ProvideValue(serviceProvider);

    /// <summary>A one-way binding to a key's string, for code that builds elements: <c>text.SetBinding(TextBlock.TextProperty, TExtension.Binding(key))</c>.</summary>
    public static Binding Binding(string key) =>
        new($"[{key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}

/// <summary>
/// A bound value put into a string of the tables, in place of a binding's StringFormat:
/// <c>Text="{Binding Name, Converter={x:Static l:LocFormat.Instance}, ConverterParameter=SceneWindow.OpenCutscene}"</c>.
/// </summary>
public sealed class LocFormat : IValueConverter
{
    public static LocFormat Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        L.T(parameter as string ?? "", value);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Binds element text to the app's strings from code, so it follows the language as XAML's do.</summary>
public static class LocBinding
{
    /// <summary>Sets <paramref name="property"/> of <paramref name="element"/> to the string for <paramref name="key"/>, live.</summary>
    public static T Bind<T>(this T element, DependencyProperty property, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, property, TExtension.Binding(key));
        return element;
    }
}
