using System.Windows.Controls;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// The files the search, file type and class filters leave, and how many each choice of type and of class would leave
/// with the other two as they are, so the counts in either list are what picking it would show.
/// </summary>
public sealed record FileFilterResult(List<EntryItem> Shown, Dictionary<string, int> Types, int AnyType, Dictionary<string, int> Classes, int AnyClass)
{
    /// <summary>The filters on, in words, for the status line: <c>"cs403" and .w2scene</c>; empty when none is.</summary>
    public required string Describe { get; init; }

    /// <summary>What to say over an empty list: which filter leaves nothing, and what turning it off would show.</summary>
    public string NoMatches(string? type, string? resourceClass) =>
        type is not null && AnyType > 0 ? $"No {type} files match. {AnyType:N0} of other types do: set File type to (all) to see them."
        : resourceClass is not null && AnyClass > 0 ? $"No {resourceClass} files match. {AnyClass:N0} of other classes do: set Class to (all) to see them."
        : "No files match.";

    /// <summary>
    /// Narrows the files in view by all three filters at once, counting the types among the files the search and class
    /// leave, and the classes among those the search and type leave.
    /// </summary>
    public static FileFilterResult Apply(IReadOnlyList<EntryItem> scope, string search, string? type, string? resourceClass)
    {
        var match = MainWindow.BuildMatcher(search);
        var shown = new List<EntryItem>();
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        int anyType = 0, anyClass = 0;
        foreach (var item in scope)
        {
            if (match is not null && !match(item)) continue;
            string itemType = MainWindow.TypeOf(item);
            string? itemClass = item.ResourceClass;
            bool typeOk = type is null || itemType.Equals(type, StringComparison.OrdinalIgnoreCase);
            bool classOk = resourceClass is null || itemClass == resourceClass;
            if (classOk)
            {
                types[itemType] = types.GetValueOrDefault(itemType) + 1;
                anyType++;
            }
            if (typeOk)
            {
                if (itemClass is not null) classes[itemClass] = classes.GetValueOrDefault(itemClass) + 1;
                anyClass++;
            }
            if (typeOk && classOk) shown.Add(item);
        }
        var on = new List<string>();
        if (search.Trim() is { Length: > 0 } words) on.Add($"\"{words}\"");
        if (type is not null) on.Add(type);
        if (resourceClass is not null) on.Add(resourceClass);
        return new(shown, types, anyType, classes, anyClass) { Describe = string.Join(" and ", on) };
    }

    /// <summary>The choices of a filter list: every one, then each with how many files it leaves, keeping the one picked where it leaves none.</summary>
    public static List<ClassChoice> Choices(Dictionary<string, int> counts, int any, string? picked)
    {
        // the one picked stays in the list where there are none of it, so moving to another folder doesn't drop it
        if (picked is not null) counts.TryAdd(picked, 0);
        var choices = new List<ClassChoice> { new(null, any) };
        choices.AddRange(counts.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new ClassChoice(c.Key, c.Value)));
        return choices;
    }
}

/// <summary>The file list's class and file type filters, as REDkit's asset browser has them.</summary>
public partial class MainWindow
{
    /// <summary>The class the list is narrowed to, or null for every file.</summary>
    private string? _classFilter;

    private bool _fillingClasses;

    /// <summary>The file type the list is narrowed to (".w2mesh"), or null for every type.</summary>
    private string? _typeFilter;

    private bool _fillingTypes;

    /// <summary>A file's type: its extension, or "(none)".</summary>
    internal static string TypeOf(EntryItem item) => System.IO.Path.GetExtension(item.Name) is { Length: > 0 } ext ? ext.ToLowerInvariant() : "(none)";

    /// <summary>Lists the file types and the classes the other filters leave, with how many of each, keeping those picked.</summary>
    private void FillChoices(FileFilterResult result)
    {
        var types = FileFilterResult.Choices(result.Types, result.AnyType, _typeFilter);
        var classes = FileFilterResult.Choices(result.Classes, result.AnyClass, _classFilter);
        _fillingTypes = _fillingClasses = true;
        try
        {
            FileTypeBox.ItemsSource = types;
            FileTypeBox.SelectedItem = types.First(c => string.Equals(c.Class, _typeFilter, StringComparison.OrdinalIgnoreCase));
            ClassBox.ItemsSource = classes;
            ClassBox.SelectedItem = classes.First(c => c.Class == _classFilter);
        }
        finally
        {
            _fillingTypes = _fillingClasses = false;
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
