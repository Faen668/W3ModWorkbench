using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace W3ModWorkbench;

/// <summary>
/// Lets a list's picked row be unpicked by clicking it again, as REDkit's property grids do. A click on a button in the
/// row (a fold arrow, a file's link) does what the button does, and Ctrl and Shift clicks are left to the list.
/// </summary>
public static class ListSelection
{
    public static readonly DependencyProperty ClickToUnselectProperty = DependencyProperty.RegisterAttached(
        "ClickToUnselect", typeof(bool), typeof(ListSelection), new PropertyMetadata(false, Changed));

    public static bool GetClickToUnselect(DependencyObject o) => (bool)o.GetValue(ClickToUnselectProperty);

    public static void SetClickToUnselect(DependencyObject o, bool value) => o.SetValue(ClickToUnselectProperty, value);

    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ListBox list) return;
        list.PreviewMouseLeftButtonDown -= List_PreviewMouseLeftButtonDown;
        if (e.NewValue is true) list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
    }

    private static void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || Keyboard.Modifiers != ModifierKeys.None) return;
        // a button in the row clicks as itself
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != list; d = Parent(d))
        {
            if (d is ButtonBase) return;
            if (d is ListBoxItem { IsSelected: true } item)
            {
                item.IsSelected = false;
                list.SelectedItem = null;
                e.Handled = true;
                return;
            }
        }
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
