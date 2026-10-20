using System.Windows.Controls;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>The file list's class and file type filters, as REDkit's asset browser has them.</summary>
public partial class MainWindow
{
    /// <summary>The class the list is narrowed to, or null for every file.</summary>
    private string? _classFilter;

    private bool _fillingClasses;

    /// <summary>Lists the classes the files in view hold, with how many of each, keeping the one picked.</summary>
    private void FillClassChoices(IReadOnlyList<EntryItem> scope)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in scope)
            if (item.ResourceClass is { } name) counts[name] = counts.GetValueOrDefault(name) + 1;
        // the class picked stays in the list where there are none of it, so moving to another folder doesn't drop it
        if (_classFilter is not null) counts.TryAdd(_classFilter, 0);
        var choices = new List<ClassChoice> { new(null, scope.Count) };
        choices.AddRange(counts.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new ClassChoice(c.Key, c.Value)));
        _fillingClasses = true;
        try
        {
            ClassBox.ItemsSource = choices;
            ClassBox.SelectedItem = choices.First(c => c.Class == _classFilter);
        }
        finally
        {
            _fillingClasses = false;
        }
    }

    /// <summary>The file type the list is narrowed to (".w2mesh"), or null for every type.</summary>
    private string? _typeFilter;

    private bool _fillingTypes;

    /// <summary>A file's type: its extension, or "(none)".</summary>
    private static string TypeOf(EntryItem item) => System.IO.Path.GetExtension(item.Name) is { Length: > 0 } ext ? ext.ToLowerInvariant() : "(none)";

    /// <summary>Lists the file types in view, with how many of each, keeping the one picked.</summary>
    private void FillTypeChoices(IReadOnlyList<EntryItem> scope)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scope) counts[TypeOf(item)] = counts.GetValueOrDefault(TypeOf(item)) + 1;
        if (_typeFilter is not null) counts.TryAdd(_typeFilter, 0);
        var choices = new List<ClassChoice> { new(null, scope.Count) };
        choices.AddRange(counts.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new ClassChoice(c.Key, c.Value)));
        _fillingTypes = true;
        try
        {
            FileTypeBox.ItemsSource = choices;
            FileTypeBox.SelectedItem = choices.First(c => string.Equals(c.Class, _typeFilter, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _fillingTypes = false;
        }
    }

    private void FileTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingTypes || FileTypeBox.SelectedItem is not ClassChoice choice || choice.Class == _typeFilter) return;
        _typeFilter = choice.Class;
        RefreshView();
    }

    private void ClassBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingClasses || ClassBox.SelectedItem is not ClassChoice choice || choice.Class == _classFilter) return;
        _classFilter = choice.Class;
        RefreshView();
    }
}
