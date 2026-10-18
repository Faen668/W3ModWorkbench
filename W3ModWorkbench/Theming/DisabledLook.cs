using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace W3ModWorkbench;

/// <summary>
/// The Fluent theme greys a disabled button's text only when its content is plain text. Content of our own (an icon
/// and a label in a StackPanel) takes its colour from the button instead, so it stayed as bright as an enabled button,
/// or black on an accent button in dark mode. This puts a style over each of the theme's button styles that gives the
/// button itself the theme's disabled colour while it is disabled.
/// </summary>
/// <remarks>
/// The styles are built here, on the theme's own styles as the app finds them once the theme is applied. Written in
/// App.xaml with BasedOn, they were based on the classic Windows look instead, as the theme wasn't in place yet.
/// </remarks>
public static class DisabledLook
{
    /// <summary>
    /// Makes the styles. <paramref name="set"/> adds each to the app's resources: <see cref="Themes"/> takes them out
    /// again before the next theme is applied, so each is always based on the theme's style and never on itself.
    /// </summary>
    public static void Apply(Application app, Action<object, object> set)
    {
        Add(app, set, typeof(Button), typeof(Button), "ButtonForegroundDisabled");
        Add(app, set, "AccentButtonStyle", typeof(Button), "ButtonForegroundDisabled");
        Add(app, set, typeof(RepeatButton), typeof(RepeatButton), "RepeatButtonForegroundDisabled");

        if (app.TryFindResource(typeof(ToggleButton)) is Style toggle)
        {
            var style = new Style(typeof(ToggleButton), toggle);
            style.Triggers.Add(Disabled(ToggleButton.IsCheckedProperty, true, "ToggleButtonForegroundCheckedDisabled"));
            style.Triggers.Add(Disabled(ToggleButton.IsCheckedProperty, false, "ToggleButtonForegroundDisabled"));
            set(typeof(ToggleButton), style);
        }
    }

    private static void Add(Application app, Action<object, object> set, object key, Type type, string brush)
    {
        if (app.TryFindResource(key) is not Style theme) return;
        var style = new Style(type, theme);
        style.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(Control.ForegroundProperty, new DynamicResourceExtension(brush)) },
        });
        set(key, style);
    }

    private static MultiTrigger Disabled(DependencyProperty property, object value, string brush) => new()
    {
        Conditions =
        {
            new Condition(UIElement.IsEnabledProperty, false),
            new Condition(property, value),
        },
        Setters = { new Setter(Control.ForegroundProperty, new DynamicResourceExtension(brush)) },
    };
}
