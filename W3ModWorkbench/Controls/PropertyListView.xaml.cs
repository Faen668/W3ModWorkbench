using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// An object's properties as a list, as the entity viewer shows them: under the class that brings each in, with
/// structs, arrays and the objects they point to folding open beneath them, defaults dimmed, files as links. A search
/// narrows it to what matches. Right-click a row to copy its value or name.
/// </summary>
public partial class PropertyListView : UserControl
{
    private List<PropertyRow> _rows = [];
    private string _search = "";

    public static readonly DependencyProperty NameColumnWidthProperty =
        DependencyProperty.Register(nameof(NameColumnWidth), typeof(GridLength), typeof(PropertyListView), new(new GridLength(220)));

    public static readonly DependencyProperty TypeColumnWidthProperty =
        DependencyProperty.Register(nameof(TypeColumnWidth), typeof(GridLength), typeof(PropertyListView), new(new GridLength(150)));

    /// <summary>How wide the names are.</summary>
    public GridLength NameColumnWidth
    {
        get => (GridLength)GetValue(NameColumnWidthProperty);
        set => SetValue(NameColumnWidthProperty, value);
    }

    /// <summary>How wide the types are; 0 leaves them out, as REDkit's narrower lists do, for more room for the values.</summary>
    public GridLength TypeColumnWidth
    {
        get => (GridLength)GetValue(TypeColumnWidthProperty);
        set => SetValue(TypeColumnWidthProperty, value);
    }

    /// <summary>Raised when a file a property refers to is clicked, with its game path.</summary>
    public event EventHandler<string>? LinkClicked;

    public PropertyListView()
    {
        InitializeComponent();
        var menu = new ContextMenu();
        foreach (var (header, copy) in new (string, Func<PropertyRow, string>)[]
                 {
                     (L.T("PropertyListView.CopyValue"), r => r.Value), (L.T("Common.CopyName"), r => r.Name), (L.T("PropertyListView.CopyNameValue"), r => $"{r.Name} = {r.Value}"),
                 })
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (List.SelectedItem is not PropertyRow row) return;
                try { Clipboard.SetText(copy(row)); }
                catch (System.Runtime.InteropServices.ExternalException) { } // another program has the clipboard open
            };
            menu.Items.Add(item);
        }
        List.ContextMenu = menu;
    }

    /// <summary>Shows these rows, narrowed to the search there is.</summary>
    public void Show(List<PropertyRow> rows)
    {
        _rows = rows;
        PropertyRow.Search(rows, _search);
        List.ItemsSource = PropertyRow.View(rows, () => _search);
    }

    /// <summary>Narrows the list to the rows (and what they're in) whose name or value has the text; empty shows them all.</summary>
    public string Search
    {
        get => _search;
        set
        {
            _search = value.Trim();
            PropertyRow.Search(_rows, _search);
            (List.ItemsSource as ICollectionView)?.Refresh();
        }
    }

    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PropertyRow { HasChildren: true } row) return;
        row.IsExpanded = !row.IsExpanded;
        (List.ItemsSource as ICollectionView)?.Refresh();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) LinkClicked?.Invoke(this, path);
    }
}
