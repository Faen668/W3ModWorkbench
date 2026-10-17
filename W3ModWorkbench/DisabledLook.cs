using System.Windows;
using System.Windows.Controls.Primitives;

namespace W3ModWorkbench;

/// <summary>
/// The Fluent theme only greys a disabled button's text slightly, which is easy to miss.
/// This fades every button in a window to 40% while it is disabled. It sets opacity on each
/// button rather than adding a Button style, which would replace the theme's own look.
/// </summary>
public static class DisabledLook
{
    private const double DisabledOpacity = 0.4;

    public static void Apply(DependencyObject root)
    {
        foreach (var button in Buttons(root))
        {
            button.IsEnabledChanged -= OnEnabledChanged;
            button.IsEnabledChanged += OnEnabledChanged;
            Update(button);
        }
    }

    private static IEnumerable<ButtonBase> Buttons(DependencyObject node)
    {
        if (node is ButtonBase b) yield return b;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var found in Buttons(child))
                yield return found;
    }

    private static void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((UIElement)sender);

    private static void Update(UIElement button) => button.Opacity = button.IsEnabled ? 1.0 : DisabledOpacity;
}
