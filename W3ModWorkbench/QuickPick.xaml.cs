using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace W3ModWorkbench;

/// <summary>A choice in a <see cref="QuickPick"/>: what it is, where it is, and what it stands for.</summary>
public sealed record QuickPickItem(string Glyph, string Title, string Detail, object? Tag);

/// <summary>
/// A box to type in over the top of a preview window, with a list that narrows as you type: go to symbol,
/// the file's outline, and the places jumped from. Up and Down move, Enter picks, Esc or a click elsewhere closes it.
/// </summary>
public partial class QuickPick : UserControl
{
    private Func<string, IReadOnlyList<QuickPickItem>> _filter = _ => [];
    private Action<QuickPickItem>? _chosen;
    private IInputElement? _returnFocus;

    public QuickPick()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <param name="filter">The items for what's typed, best first.</param>
    /// <param name="note">A line under the list, such as a hint on what can be typed.</param>
    public void Show(string placeholder, Func<string, IReadOnlyList<QuickPickItem>> filter, Action<QuickPickItem> chosen, string? note = null)
    {
        _filter = filter;
        _chosen = chosen;
        _returnFocus = Keyboard.FocusedElement;
        Placeholder.Text = placeholder;
        Note.Text = note ?? "";
        Note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        Box.Text = "";
        Refresh();
        Dispatcher.BeginInvoke(() => Box.Focus(), DispatcherPriority.Input);
    }

    public void Hide()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        List.ItemsSource = null;
        _chosen = null;
        if (_returnFocus is UIElement { IsVisible: true } back) back.Focus();
    }

    private void Refresh()
    {
        Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = _filter(Box.Text.Trim());
        List.ItemsSource = items;
        if (items.Count > 0)
        {
            List.SelectedIndex = 0;
            List.ScrollIntoView(items[0]);
        }
    }

    private void Choose()
    {
        if (List.SelectedItem is not QuickPickItem item || _chosen is not { } chosen) return;
        _returnFocus = null; // the choice decides where the keyboard goes
        Hide();
        chosen(item);
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = List.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Enter:
                Choose();
                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when count > 0:
                int step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 };
                List.SelectedIndex = Math.Clamp(List.SelectedIndex + step, 0, count - 1);
                List.ScrollIntoView(List.SelectedItem);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void List_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem item)
        {
            List.SelectedItem = item.DataContext;
            Choose();
        }
    }

    private void Box_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // a click on the list keeps it open; anywhere else closes it
        if (e.NewFocus is DependencyObject to && IsAncestorOf(to)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsKeyboardFocusWithin && !List.IsMouseOver) Hide();
        }, DispatcherPriority.Input);
    }
}
