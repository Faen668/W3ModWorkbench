using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A file or folder to convert or edit.</summary>
public sealed record Cr2wInputRow(string Path)
{
    public string Glyph => Directory.Exists(Path) ? "" : "";
}

/// <summary>What happened to one file, for the results list.</summary>
public sealed class Cr2wResultRow(Cr2wJsonResult result, bool dryRun = false)
{
    public Cr2wJsonResult Result { get; } = result;
    public string Glyph => !Result.Ok ? "" : Result.Output is null ? "" : "";
    public Brush? Brush => Application.Current.TryFindResource(!Result.Ok ? "SystemFillColorCriticalBrush"
        : Result.Output is null ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush") as Brush;
    public string Name => System.IO.Path.GetFileName(Result.Output ?? Result.Input);
    public string Detail => Result.Error ?? (dryRun && Result.Output is not null ? Result.Note is null ? L.T("Cr2wJsonWindow.WouldBeSaved") : L.T("Cr2wJsonWindow.WouldBeSavedNote", Result.Note) : Result.Note) ?? "";
    public bool HasDetail => Detail.Length > 0;
    public string Tip => Result.Output is null ? Result.Input : $"{Result.Input}\n→ {Result.Output}";
}

/// <summary>A property in the path picker: its name, a hint of its value, and its path from the object.</summary>
public sealed class Cr2wPathNode
{
    public required string Name { get; init; }
    public string Summary { get; init; } = "";
    public string Path { get; init; } = "";
    public string ClassName { get; init; } = "";
    public List<Cr2wPathNode> Children { get; init; } = [];
}

/// <summary>A bulk edit waiting in the list, with how it reads, and for a copy the file it copies from.</summary>
public sealed record Cr2wEditRow(Cr2wBulkEdit Edit, string Description, string? Source = null);

/// <summary>A saved recipe in the list: its name, and what it does in a line.</summary>
public sealed class Cr2wRecipeRow(Cr2wRecipe recipe, string summary)
{
    public Cr2wRecipe Recipe { get; } = recipe;
    public string Name => Recipe.Name;
    public string Summary { get; } = summary;
    public string Glyph => Recipe.Kind == Cr2wRecipeKind.Edit ? "" : "";
    public string Tip => Recipe.FilePath ?? Recipe.Name;
}

/// <summary>
/// Converts CR2W files to JSON and back, many at once, and makes the same edit to many files: a value copied from
/// another file, set, removed, or numbers scaled. The work is <see cref="Cr2wJsonBatch"/>'s, as on the command line.
/// </summary>
public partial class Cr2wJsonWindow : Window, IMessageHost
{
    private static Cr2wJsonWindow? _instance;
    private readonly ObservableCollection<Cr2wInputRow> _convertInputs = [];
    private readonly ObservableCollection<Cr2wInputRow> _bulkInputs = [];
    private readonly ObservableCollection<Cr2wEditRow> _edits = [];
    private readonly ObservableCollection<Cr2wRecipeRow> _recipes = [];
    private CancellationTokenSource? _cts;

    private Cr2wJsonWindow()
    {
        InitializeComponent();
        ConvertList.ItemsSource = _convertInputs;
        BulkList.ItemsSource = _bulkInputs;
        EditsList.ItemsSource = _edits;
        RecipeList.ItemsSource = _recipes;
        var settings = App.Settings;
        ConvertOutputBox.Text = BulkOutputBox.Text = settings.Cr2wJsonOutput;
        ConvertFolderRadio.IsChecked = settings.Cr2wJsonOutput.Length > 0;
        ConvertRecursiveCheck.IsChecked = BulkRecursiveCheck.IsChecked = settings.Cr2wJsonRecursive;
        Kind_Changed(this, null!);
        LoadRecipes();
        // the recipes folder may have been changed in Settings meanwhile
        Activated += (_, _) =>
        {
            if (!RecipesFolder.Equals(_recipesFolder, StringComparison.OrdinalIgnoreCase)) LoadRecipes();
        };
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _instance = null;
            settings.Cr2wJsonOutput = (ConvertFolderRadio.IsChecked == true ? ConvertOutputBox.Text : BulkOutputBox.Text).Trim();
            settings.Cr2wJsonRecursive = ConvertRecursiveCheck.IsChecked == true;
            settings.Save();
        };
    }

    /// <summary>Opens the window, or brings it forward, with files to convert when they're given.</summary>
    public static void Open(Window owner, IEnumerable<string>? files = null)
    {
        if (_instance is null)
        {
            _instance = new Cr2wJsonWindow().Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
        if (files is not null) _instance.Add(_instance._convertInputs, files);
    }

    // ---------------------------------------------------------------- the files lists

    private void Add(ObservableCollection<Cr2wInputRow> list, IEnumerable<string> paths)
    {
        foreach (string path in paths)
            if (!list.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) list.Add(new Cr2wInputRow(path));
    }

    private string[]? PickFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Multiselect = true, Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    private string? PickFolder(string title, string start = "")
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(start) ? start : "" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static string AnyFilter => L.T("Cr2wJsonWindow.AnyFilter");

    private void ConvertAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(L.T("Cr2wJsonWindow.ChooseFilesConvert"), AnyFilter) is { } files) Add(_convertInputs, files);
    }

    private void ConvertAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(L.T("Cr2wJsonWindow.ChooseFolderConvert")) is { } folder) Add(_convertInputs, [folder]);
    }

    private void ConvertRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected(ConvertList, _convertInputs);
    private void ConvertClear_Click(object sender, RoutedEventArgs e) => _convertInputs.Clear();

    private void BulkAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(L.T("Cr2wJsonWindow.ChooseFilesEdit"), AnyFilter) is { } files) Add(_bulkInputs, files);
    }

    private void BulkAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(L.T("Cr2wJsonWindow.ChooseFolderFilesEdit")) is { } folder) Add(_bulkInputs, [folder]);
    }

    private void BulkRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected(BulkList, _bulkInputs);
    private void BulkClear_Click(object sender, RoutedEventArgs e) => _bulkInputs.Clear();

    private static void RemoveSelected(ListBox list, ObservableCollection<Cr2wInputRow> rows)
    {
        foreach (var row in list.SelectedItems.Cast<Cr2wInputRow>().ToList()) rows.Remove(row);
    }

    private void InputList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not ListBox list) return;
        RemoveSelected(list, list == ConvertList ? _convertInputs : _bulkInputs);
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (Tabs.SelectedIndex == 2)
        {
            ImportRecipes(paths.Where(p => p.EndsWith(Cr2wRecipe.Extension, StringComparison.OrdinalIgnoreCase)));
            return;
        }
        // dropped on the source box: that's the file to copy from
        if (Tabs.SelectedIndex == 1 && IsInside(e.OriginalSource as DependencyObject, SourceBox) && paths.Length == 1)
        {
            SourceBox.Text = paths[0];
            return;
        }
        Add(Tabs.SelectedIndex == 0 ? _convertInputs : _bulkInputs, paths);
        // dropping JSON files on the convert tab means converting them back
        if (Tabs.SelectedIndex == 0 && paths.All(p => p.EndsWith(Cr2wJsonBatch.Suffix, StringComparison.OrdinalIgnoreCase))) FromJsonRadio.IsChecked = true;
        else if (Tabs.SelectedIndex == 0 && ResaveRadio.IsChecked != true && paths.All(File.Exists) && paths.All(Cr2wJsonBatch.IsCr2w)) ToJsonRadio.IsChecked = true;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (; element is not null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element == container) return true;
        return false;
    }

    // ---------------------------------------------------------------- converting

    private void ConvertBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(L.T("Cr2wJsonWindow.ChooseWhereSaveConverted"), ConvertOutputBox.Text) is { } folder) ConvertOutputBox.Text = folder;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (_convertInputs.Count == 0)
        {
            await ShowMessage(L.T("Cr2wJsonWindow.NothingConvert"), L.T("Cr2wJsonWindow.AddFilesFoldersFirst"), MessageKind.Info);
            return;
        }
        bool fromJson = FromJsonRadio.IsChecked == true, resave = ResaveRadio.IsChecked == true, asLatest = AsLatestCheck.IsChecked == true;
        string? output = OutputFolder(ConvertFolderRadio, ConvertOutputBox);
        if (output == "") return;
        bool recursive = ConvertRecursiveCheck.IsChecked == true, overwrite = OverwriteCheck.IsChecked == true;
        var paths = _convertInputs.Select(r => r.Path).ToList();

        await RunAsync(ConvertButton, ConvertStopButton, ConvertProgress, ConvertStatus, ConvertResults, dryRun: false,
            resave ? L.T("Cr2wJsonWindow.Resaving") : fromJson ? L.T("Cr2wJsonWindow.ConvertingBack") : L.T("Cr2wJsonWindow.Converting"), (progress, token) =>
            {
                var inputs = Cr2wJsonBatch.Inputs(paths, fromJson, recursive);
                return inputs.Count == 0 ? null
                    : resave ? Cr2wJsonBatch.ResaveAsLatest(inputs, output, overwrite, progress, token)
                    : fromJson ? Cr2wJsonBatch.FromJson(inputs, output, overwrite, progress, token, asLatest)
                    : Cr2wJsonBatch.ToJson(inputs, output, overwrite, progress, token);
            },
            fromJson ? L.T("Cr2wJsonWindow.NoJsonFilesMade") : L.T("Cr2wJsonWindow.NoCR2WFilesWere"));
    }

    /// <summary>The output folder chosen, null for beside the files, or "" when one is chosen but not given (and said so).</summary>
    private string? OutputFolder(RadioButton intoFolder, TextBox box)
    {
        if (intoFolder.IsChecked != true) return null;
        string folder = box.Text.Trim();
        if (folder.Length == 0)
        {
            _ = ShowMessage(L.T("Common.ChooseFolder"), L.T("Cr2wJsonWindow.ChooseFolderSaveInto"), MessageKind.Info);
            return "";
        }
        try { return Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.FolderCantUsed"), ex.Message, MessageKind.Warning);
            return "";
        }
    }

    /// <summary>Runs a batch in the background with its progress shown, then lists what happened to each file.</summary>
    private async Task RunAsync(Button start, Button stop, ProgressBar bar, TextBlock status, ListBox results, bool dryRun, string doing,
        Func<IProgress<Cr2wJsonProgress>, CancellationToken, List<Cr2wJsonResult>?> work, string noneFound, Action? done = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        start.IsEnabled = CheckButton.IsEnabled = ApplyButton.IsEnabled = ConvertButton.IsEnabled = RecipeRunButton.IsEnabled = RecipeCheckButton.IsEnabled = false;
        stop.Visibility = Visibility.Visible;
        bar.Visibility = Visibility.Visible;
        bar.IsIndeterminate = true;
        status.Text = $"{doing}…";
        results.ItemsSource = null;
        var progress = new Progress<Cr2wJsonProgress>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Maximum = p.Total;
            bar.Value = p.Done;
            status.Text = L.T("Cr2wJsonWindow.Of", doing, p.Done, p.Total, Path.GetFileName(p.Current));
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await Task.Run(() => work(progress, token), token);
            if (list is null)
            {
                status.Text = noneFound;
                return;
            }
            done?.Invoke();
            // failures first, then what was skipped, then the rest, each in the order the files were found
            results.ItemsSource = list.Select((r, i) => (Row: new Cr2wResultRow(r, dryRun), i))
                .OrderBy(x => x.Row.Result.Ok ? x.Row.Result.Output is null ? 1 : 2 : 0).ThenBy(x => x.i).Select(x => x.Row).ToList();
            int failed = list.Count(r => !r.Ok), skipped = list.Count(r => r.Ok && r.Output is null), doneCount = list.Count - failed - skipped;
            string verb = dryRun ? L.T("Cr2wJsonWindow.WouldSaved") : L.T("Cr2wJsonWindow.Saved");
            status.Text = L.T("Cr2wJsonWindow.S", doneCount, verb, (skipped > 0 ? L.T("Cr2wJsonWindow.Unchanged", skipped) : ""), (failed > 0 ? L.T("Cr2wJsonWindow.Failed", failed) : ""), (token.IsCancellationRequested ? L.T("Cr2wJsonWindow.Stopped") : ""), clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            status.Text = L.T("Cr2wJsonWindow.Stopped2");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            // a recipe whose copy source has gone, say
            status.Text = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            start.IsEnabled = CheckButton.IsEnabled = ApplyButton.IsEnabled = ConvertButton.IsEnabled = RecipeRunButton.IsEnabled = true;
            RecipeCheckButton.IsEnabled = SelectedRecipe?.Kind == Cr2wRecipeKind.Edit;
            stop.Visibility = Visibility.Collapsed;
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Cr2wResultRow row }) ShellReveal.Show([row.Result.Output ?? row.Result.Input]);
    }

    private void ConvertReveal_Click(object sender, RoutedEventArgs e) => Reveal(ConvertResults);
    private void BulkReveal_Click(object sender, RoutedEventArgs e) => Reveal(BulkResults);
    private void RecipeReveal_Click(object sender, RoutedEventArgs e) => Reveal(RecipeResults);

    private static void Reveal(ListBox results)
    {
        var rows = results.SelectedItems.Count > 0 ? results.SelectedItems.Cast<Cr2wResultRow>() : results.Items.Cast<Cr2wResultRow>();
        ShellReveal.Show(rows.Select(r => r.Result.Output ?? r.Result.Input));
    }

    // ---------------------------------------------------------------- bulk edits

    private BulkEditKind Kind => (BulkEditKind)Math.Max(0, KindBox.SelectedIndex);

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SourceRow is null) return;
        var kind = Kind;
        SourceRow.Visibility = kind == BulkEditKind.Copy ? Visibility.Visible : Visibility.Collapsed;
        ValueRow.Visibility = kind == BulkEditKind.Set ? Visibility.Visible : Visibility.Collapsed;
        AmountRow.Visibility = kind is BulkEditKind.Scale or BulkEditKind.Offset ? Visibility.Visible : Visibility.Collapsed;
        AmountLabel.Text = kind == BulkEditKind.Scale ? L.T("Cr2wJsonWindow.Multiply") : L.T("Common.Add");
        if (kind == BulkEditKind.Scale && AmountBox.Text == "0") AmountBox.Text = "1";
        if (kind == BulkEditKind.Offset && AmountBox.Text == "1") AmountBox.Text = "0";
    }

    private void SourceBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(L.T("Cr2wJsonWindow.ChooseFileCopy"), AnyFilter) is [var file, ..]) SourceBox.Text = file;
    }

    private void BulkBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(L.T("Cr2wJsonWindow.ChooseWhereSaveEdited"), BulkOutputBox.Text) is { } folder) BulkOutputBox.Text = folder;
    }

    /// <summary>Shows the source file's properties (or the first file to edit's) to pick the path from.</summary>
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        string? file = Kind == BulkEditKind.Copy && SourceBox.Text.Trim() is { Length: > 0 } source ? source
            : _bulkInputs.Select(r => r.Path).Select(p => File.Exists(p) ? p : Cr2wJsonBatch.Inputs([p], false, true).Select(i => i.File).FirstOrDefault()
                ?? Cr2wJsonBatch.Inputs([p], true, true).Select(i => i.File).FirstOrDefault()).FirstOrDefault(p => p is not null);
        if (file is null)
        {
            await ShowMessage(L.T("Cr2wJsonWindow.NoFileChoose"), Kind == BulkEditKind.Copy ? L.T("Cr2wJsonWindow.ChooseFileCopyFirst") : L.T("Cr2wJsonWindow.AddFileEditFirst"), MessageKind.Info);
            return;
        }
        PickerTitle.Text = L.T("Cr2wJsonWindow.Reading", Path.GetFileName(file));
        PickerPanel.Visibility = Visibility.Visible;
        PickerTree.ItemsSource = null;
        try
        {
            var nodes = await Task.Run(() => Nodes(Cr2wJsonBatch.Load(Path.GetFullPath(file))));
            PickerTree.ItemsSource = nodes;
            PickerTitle.Text = L.T("Cr2wJsonWindow.PropertiesPickOne", Path.GetFileName(file));
            if (PickerTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first) first.IsExpanded = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            PickerPanel.Visibility = Visibility.Collapsed;
            await ShowMessage(L.T("Cr2wJsonWindow.CantRead", Path.GetFileName(file)), ex.Message, MessageKind.Warning);
        }
    }

    private void PickerClose_Click(object sender, RoutedEventArgs e) => PickerPanel.Visibility = Visibility.Collapsed;

    private void Picker_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not Cr2wPathNode node || node.Path.Length == 0) return;
        PathBox.Text = node.Path;
        ClassBox.Text = node.ClassName;
    }

    /// <summary>The picker's tree: each object of the file, and its properties down.</summary>
    private static List<Cr2wPathNode> Nodes(JsonObject document)
    {
        var result = new List<Cr2wPathNode>();
        var objects = document["objects"] as JsonArray ?? [];
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not JsonObject obj) continue;
            string className = (string?)obj["$class"] ?? "";
            result.Add(new Cr2wPathNode
            {
                Name = $"{className}",
                Summary = obj["$data"] is not null ? L.T("Cr2wJsonWindow.KeptBytes") : L.T("Cr2wJsonWindow.Object", i),
                ClassName = className,
                Children = Children(obj, "", className, 0),
            });
        }
        return result;
    }

    private static List<Cr2wPathNode> Children(JsonNode node, string path, string className, int depth)
    {
        var result = new List<Cr2wPathNode>();
        if (depth > 12) return result;
        if (node is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                if (key.StartsWith('$')) continue;
                string here = path.Length == 0 ? key : $"{path}.{key}";
                result.Add(Make(key, value, here, className, depth));
            }
        }
        else if (node is JsonArray a)
        {
            for (int i = 0; i < Math.Min(a.Count, 200); i++) result.Add(Make($"[{i}]", a[i], $"{path}[{i}]", className, depth));
        }
        return result;
    }

    private static Cr2wPathNode Make(string name, JsonNode? value, string path, string className, int depth) => new()
    {
        Name = name,
        Path = path,
        ClassName = className,
        Summary = value switch
        {
            JsonObject o when o["$raw"] is not null => L.T("Cr2wJsonWindow.KeptBytes"),
            JsonObject o when o["dataCurveValues"] is JsonArray points => L.T("Cr2wJsonWindow.Curve", points.Count),
            JsonObject o => o.Count == 0 ? "{ }" : L.T("Cr2wJsonWindow.Properties2", o.Count(p => !p.Key.StartsWith('$'))),
            JsonArray a => L.T("Cr2wJsonWindow.Items", a.Count),
            null => L.T("Cr2wJsonWindow.Null"),
            _ => Shorten(value.ToJsonString()),
        },
        Children = value is JsonObject { } obj && obj["$raw"] is null || value is JsonArray ? Children(value!, path, className, depth + 1) : [],
    };

    private static string Shorten(string text) => text.Length > 60 ? text[..57] + "…" : text;

    private void AddEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var edit = MakeEdit();
            string? source = edit.Kind == BulkEditKind.Copy ? Path.GetFullPath(SourceBox.Text.Trim()) : null;
            _edits.Add(new Cr2wEditRow(edit, Describe(Cr2wRecipeEdit.From(edit, source)), source));
            PickerPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.EditCantMade"), ex.Message, MessageKind.Warning);
        }
    }

    private void RemoveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (EditsList.SelectedItem is Cr2wEditRow row) _edits.Remove(row);
        else if (_edits.Count > 0) _edits.RemoveAt(_edits.Count - 1);
    }

    /// <summary>The edit the boxes describe, checked.</summary>
    private Cr2wBulkEdit MakeEdit()
    {
        string path = PathBox.Text.Trim();
        Cr2wBulkEdit.ParsePath(path);
        string className = ClassBox.Text.Trim();
        var kind = Kind;
        switch (kind)
        {
            case BulkEditKind.Copy:
            {
                string source = SourceBox.Text.Trim();
                if (source.Length == 0) throw new FormatException(L.T("Cr2wJsonWindow.ChooseFileCopy2"));
                var document = Cr2wJsonBatch.Load(Path.GetFullPath(source));
                var edit = new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className, Source = document };
                if (edit.SourceObject() is not { } from || Cr2wBulkEdit.Find(from, Cr2wBulkEdit.ParsePath(path)) is null)
                    throw new FormatException(L.T("Cr2wJsonWindow.HasNothing", Path.GetFileName(source), path));
                return edit;
            }
            case BulkEditKind.Set:
            {
                string text = ValueBox.Text.Trim();
                JsonNode? value;
                try { value = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { value = JsonValue.Create(ValueBox.Text); }
                return new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className, Value = value };
            }
            case BulkEditKind.Scale or BulkEditKind.Offset:
            {
                if (!double.TryParse(AmountBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                    throw new FormatException(L.T("Cr2wJsonWindow.IsntNumber", AmountBox.Text));
                var fields = FieldsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className, Amount = amount, Fields = fields };
            }
            default:
                return new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className };
        }
    }

    /// <summary>An edit in a line, as the list and a recipe's steps show it.</summary>
    private static string Describe(Cr2wRecipeEdit edit)
    {
        string where = edit.ClassName.Length > 0 ? L.T("Cr2wJsonWindow.In", edit.ClassName) : "";
        string fields = edit.Fields.Count > 0 ? L.T("Cr2wJsonWindow.In2", string.Join(", ", edit.Fields)) : "";
        return edit.Kind switch
        {
            BulkEditKind.Copy => L.T("Cr2wJsonWindow.Copy", edit.Path, Path.GetFileName(edit.Source ?? ""), where),
            BulkEditKind.Set => L.T("Cr2wJsonWindow.SetTo", edit.Path, Shorten(edit.Value?.ToJsonString() ?? "null"), where),
            BulkEditKind.Remove => L.T("Cr2wJsonWindow.Remove", edit.Path, where),
            BulkEditKind.Scale => L.T("Cr2wJsonWindow.MultiplyNumbersUnder", edit.Path, fields, edit.Amount.ToString(CultureInfo.InvariantCulture), where),
            _ => L.T("Cr2wJsonWindow.AddNumbersUnder", edit.Amount.ToString(CultureInfo.InvariantCulture), edit.Path, fields, where),
        };
    }

    private void BulkCheck_Click(object sender, RoutedEventArgs e) => _ = BulkAsync(dryRun: true);
    private void BulkApply_Click(object sender, RoutedEventArgs e) => _ = BulkAsync(dryRun: false);

    private async Task BulkAsync(bool dryRun)
    {
        if (_cts is not null) return;
        if (_bulkInputs.Count == 0)
        {
            await ShowMessage(L.T("Cr2wJsonWindow.NoFilesEdit"), L.T("Cr2wJsonWindow.AddFilesFoldersEdit"), MessageKind.Info);
            return;
        }
        var edits = _edits.Select(r => r.Edit).ToList();
        if (edits.Count == 0)
        {
            // the edit in the boxes, when it hasn't been added to the list
            try { edits.Add(MakeEdit()); }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await ShowMessage(L.T("Cr2wJsonWindow.NoEditsMake"), L.T("Cr2wJsonWindow.AddEditListFirst", ex.Message), MessageKind.Info);
                return;
            }
        }
        string? output = OutputFolder(BulkFolderRadio, BulkOutputBox);
        if (output == "") return;
        if (!dryRun && output is null && !await Message.Show(L.T("Cr2wJsonWindow.SaveOverFiles"),
                L.T("Cr2wJsonWindow.EditsSavedIntoFiles"),
                MessageKind.Question, primary: L.T("Cr2wJsonWindow.SaveOverThem"), secondary: L.T("Common.Cancel")))
            return;
        bool recursive = BulkRecursiveCheck.IsChecked == true;
        var paths = _bulkInputs.Select(r => r.Path).ToList();
        await RunAsync(ApplyButton, BulkStopButton, BulkProgress, BulkStatus, BulkResults, dryRun, dryRun ? L.T("Cr2wJsonWindow.Checking") : L.T("Cr2wJsonWindow.Editing"), (progress, token) =>
        {
            // CR2W files and converted JSON files alike
            var inputs = Cr2wRecipe.EditInputs(paths, recursive);
            return inputs.Count == 0 ? null : Cr2wJsonBatch.BulkEdit(inputs, edits, output, progress, token, dryRun);
        }, L.T("Cr2wJsonWindow.NoCR2WConvertedJson"));
    }

    // ---------------------------------------------------------------- recipes

    private static string RecipesFolder => Cr2wRecipe.Folder(App.Settings.Cr2wRecipesFolder);

    /// <summary>The folder the recipes list was read from.</summary>
    private string _recipesFolder = "";

    private Cr2wRecipe? SelectedRecipe => (RecipeList.SelectedItem as Cr2wRecipeRow)?.Recipe;

    /// <summary>Reads the recipes folder again, keeping the recipe that was selected (or selecting <paramref name="select"/>).</summary>
    private void LoadRecipes(string? select = null)
    {
        select ??= SelectedRecipe?.FilePath;
        _recipesFolder = RecipesFolder;
        _recipes.Clear();
        foreach (var recipe in Cr2wRecipe.LoadAll(RecipesFolder)) _recipes.Add(new Cr2wRecipeRow(recipe, Summary(recipe)));
        RecipesEmpty.Visibility = _recipes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipeList.SelectedItem = _recipes.FirstOrDefault(r => r.Recipe.FilePath?.Equals(select, StringComparison.OrdinalIgnoreCase) == true);
        ShowRecipe();
    }

    private static string KindName(Cr2wRecipeKind kind) => kind switch
    {
        Cr2wRecipeKind.FromJson => L.T("Cr2wJsonWindow.JsonBackCR2W"),
        Cr2wRecipeKind.Resave => L.T("Cr2wJsonWindow.ResaveVersion"),
        Cr2wRecipeKind.Edit => L.T("Cr2wJsonWindow.BulkEdit"),
        _ => L.T("Cr2wJsonWindow.CR2WJson2"),
    };

    /// <summary>A recipe in a line for the list: what it does, and to how many files and folders.</summary>
    private static string Summary(Cr2wRecipe recipe)
    {
        var parts = new List<string> { KindName(recipe.Kind) };
        if (recipe.Kind == Cr2wRecipeKind.Edit) parts.Add(L.T("Count.Edit", recipe.Edits.Count));
        parts.Add(L.T("Count.FileOrFolder", recipe.Inputs.Count));
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The recipe's steps as aligned text for the steps box: a label column (what, files, save to, options, last run)
    /// with the values lined up after it, then the edits, each its action and path with the details under them.
    /// The columns are as wide as the longest label, so they stay aligned in any language.
    /// </summary>
    private static string Steps(Cr2wRecipe recipe)
    {
        static string Label(string key) => L.T(key).ToUpper(CultureInfo.CurrentUICulture);
        var rows = new List<(string Label, string Value)> { (Label("Cr2wJsonWindow.StepWhat"), KindName(recipe.Kind)) };
        for (int i = 0; i < recipe.Inputs.Count; i++) rows.Add((i == 0 ? Label("Cr2wJsonWindow.StepFiles") : "", recipe.Inputs[i]));
        if (recipe.Recursive) rows.Add(("", L.T("Cr2wJsonWindow.StepSubfolders")));
        rows.Add((Label("Cr2wJsonWindow.StepSaveTo"), !string.IsNullOrWhiteSpace(recipe.Output) ? recipe.Output
            : recipe.Kind == Cr2wRecipeKind.Edit ? L.T("Cr2wJsonWindow.StepOverFiles") : L.T("Cr2wJsonWindow.StepBeside")));
        var options = new List<string>();
        if (recipe.Overwrite && recipe.Kind != Cr2wRecipeKind.Edit) options.Add(L.T("Cr2wJsonWindow.StepOverwrite"));
        if (recipe.AsLatest && recipe.Kind == Cr2wRecipeKind.FromJson) options.Add(L.T("Cr2wJsonWindow.StepAs164"));
        for (int i = 0; i < options.Count; i++) rows.Add((i == 0 ? Label("Cr2wJsonWindow.StepOptions") : "", options[i]));
        rows.Add((Label("Cr2wJsonWindow.StepLastRun"), recipe.LastRun is { } ran ? ran.ToString("g", CultureInfo.CurrentCulture) : L.T("Cr2wJsonWindow.StepNeverRun")));

        int labelWidth = rows.Max(r => r.Label.Length) + 3;
        var s = new System.Text.StringBuilder();
        foreach (var (label, value) in rows) s.Append(label.PadRight(labelWidth)).AppendLine(value);
        if (recipe.Kind != Cr2wRecipeKind.Edit || recipe.Edits.Count == 0) return s.ToString().TrimEnd();

        // each edit: its number, its action and its path, then its details a line each under the path
        var edits = recipe.Edits.Select(e => (Action: L.T(e.Kind switch
        {
            BulkEditKind.Copy => "Cr2wJsonWindow.StepCopy",
            BulkEditKind.Set => "Cr2wJsonWindow.StepSet",
            BulkEditKind.Remove => "Cr2wJsonWindow.StepRemove",
            BulkEditKind.Scale => "Cr2wJsonWindow.StepMultiply",
            _ => "Cr2wJsonWindow.StepAdd",
        }), Edit: e, Details: EditDetails(e))).ToList();
        int number = recipe.Edits.Count.ToString(CultureInfo.InvariantCulture).Length;
        int actionWidth = edits.Max(e => e.Action.Length) + 2;
        int detailWidth = edits.SelectMany(e => e.Details).Select(d => d.Label.Length).DefaultIfEmpty(0).Max() + 3;
        string indent = new(' ', 2 + number + 2 + actionWidth);
        s.AppendLine().AppendLine(Label("Cr2wJsonWindow.StepEdits"));
        for (int i = 0; i < edits.Count; i++)
        {
            s.Append("  ").Append((i + 1).ToString(CultureInfo.CurrentCulture).PadLeft(number)).Append("  ")
                .Append(edits[i].Action.PadRight(actionWidth)).AppendLine(edits[i].Edit.Path);
            foreach (var (label, value) in edits[i].Details) s.Append(indent).Append(label.PadRight(detailWidth)).AppendLine(value);
        }
        return s.ToString().TrimEnd();
    }

    /// <summary>What an edit needs besides its path, as labelled lines: where it copies from, the value, the amount, the properties, the class.</summary>
    private static List<(string Label, string Value)> EditDetails(Cr2wRecipeEdit edit)
    {
        var details = new List<(string, string)>();
        switch (edit.Kind)
        {
            case BulkEditKind.Copy: details.Add((L.T("Cr2wJsonWindow.StepFrom"), edit.Source ?? "")); break;
            case BulkEditKind.Set: details.Add((L.T("Cr2wJsonWindow.StepTo"), edit.Value?.ToJsonString() ?? "null")); break;
            case BulkEditKind.Scale: details.Add((L.T("Cr2wJsonWindow.StepBy"), Cr2wRecipe.Number(edit.Amount))); break;
            case BulkEditKind.Offset: details.Add((L.T("Cr2wJsonWindow.StepAmount"), Cr2wRecipe.Number(edit.Amount))); break;
        }
        if (edit.Kind is BulkEditKind.Scale or BulkEditKind.Offset && edit.Fields.Count > 0) details.Add((L.T("Cr2wJsonWindow.StepIn"), string.Join(", ", edit.Fields)));
        if (edit.ClassName.Length > 0) details.Add((L.T("Cr2wJsonWindow.StepOnly"), edit.ClassName));
        return details;
    }

    private void RecipeList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowRecipe();

    private void ShowRecipe()
    {
        var recipe = SelectedRecipe;
        RecipeDetails.Visibility = RecipeActions.Visibility = recipe is null ? Visibility.Collapsed : Visibility.Visible;
        RecipeNone.Visibility = recipe is null && _recipes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (recipe is null) return;
        RecipeNameBox.Text = recipe.Name;
        RecipeNotesBox.Text = recipe.Notes;
        RecipeStepsText.Text = Steps(recipe);
        RecipeStepsScroller.ScrollToHome();
        var problems = recipe.Problems();
        RecipeProblemsText.Text = string.Join("\n", problems);
        RecipeProblemsText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipeCheckButton.IsEnabled = recipe.Kind == Cr2wRecipeKind.Edit && _cts is null;
        RecipeCheckButton.Visibility = recipe.Kind == Cr2wRecipeKind.Edit ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Saves a recipe made from a tab's form, and shows it in the recipes tab with its name ready to change.</summary>
    private void AddRecipe(Cr2wRecipe recipe)
    {
        recipe.Name = Cr2wRecipe.UniqueName(recipe.Name, _recipes.Select(r => r.Recipe));
        if (!TrySave(recipe)) return;
        LoadRecipes(recipe.FilePath);
        Tabs.SelectedIndex = 2;
        RecipeStatus.Text = L.T("Cr2wJsonWindow.RecipeSaved", recipe.Name);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            RecipeNameBox.Focus();
            RecipeNameBox.SelectAll();
        });
    }

    private bool TrySave(Cr2wRecipe recipe)
    {
        try
        {
            recipe.Save(RecipesFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.RecipeCantSave"), ex.Message, MessageKind.Warning);
            return false;
        }
    }

    /// <summary>A name for a new recipe: what it does, and the first file or folder it works on.</summary>
    private static string DefaultName(Cr2wRecipeKind kind, IEnumerable<string> inputs) =>
        inputs.FirstOrDefault() is { } first ? L.T("Cr2wJsonWindow.RecipeDefaultName", KindName(kind), Path.GetFileName(first.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : first) : KindName(kind);

    private void ConvertSaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (_convertInputs.Count == 0)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.NothingSaveRecipe"), L.T("Cr2wJsonWindow.AddFilesFoldersFirst"), MessageKind.Info);
            return;
        }
        string? output = OutputFolder(ConvertFolderRadio, ConvertOutputBox);
        if (output == "") return;
        var kind = ResaveRadio.IsChecked == true ? Cr2wRecipeKind.Resave : FromJsonRadio.IsChecked == true ? Cr2wRecipeKind.FromJson : Cr2wRecipeKind.ToJson;
        var inputs = _convertInputs.Select(r => Path.GetFullPath(r.Path)).ToList();
        AddRecipe(new Cr2wRecipe
        {
            Name = DefaultName(kind, inputs),
            Kind = kind,
            Inputs = inputs,
            Recursive = ConvertRecursiveCheck.IsChecked == true,
            Overwrite = OverwriteCheck.IsChecked == true,
            AsLatest = kind == Cr2wRecipeKind.FromJson && AsLatestCheck.IsChecked == true,
            Output = output,
        });
    }

    private void BulkSaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (_bulkInputs.Count == 0)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.NothingSaveRecipe"), L.T("Cr2wJsonWindow.AddFilesFoldersEdit"), MessageKind.Info);
            return;
        }
        var edits = _edits.Select(r => Cr2wRecipeEdit.From(r.Edit, r.Source)).ToList();
        if (edits.Count == 0)
        {
            // the edit in the boxes, when it hasn't been added to the list
            try
            {
                var edit = MakeEdit();
                edits.Add(Cr2wRecipeEdit.From(edit, edit.Kind == BulkEditKind.Copy ? Path.GetFullPath(SourceBox.Text.Trim()) : null));
            }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _ = ShowMessage(L.T("Cr2wJsonWindow.NoEditsMake"), L.T("Cr2wJsonWindow.AddEditListFirst", ex.Message), MessageKind.Info);
                return;
            }
        }
        string? output = OutputFolder(BulkFolderRadio, BulkOutputBox);
        if (output == "") return;
        var inputs = _bulkInputs.Select(r => Path.GetFullPath(r.Path)).ToList();
        AddRecipe(new Cr2wRecipe
        {
            Name = DefaultName(Cr2wRecipeKind.Edit, inputs),
            Kind = Cr2wRecipeKind.Edit,
            Inputs = inputs,
            Recursive = BulkRecursiveCheck.IsChecked == true,
            Output = output,
            Edits = edits,
        });
    }

    /// <summary>The name and notes are saved as they're left; a new name renames the file.</summary>
    private void RecipeText_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveRecipeText();

    private void RecipeNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveRecipeText();
        e.Handled = true;
    }

    private void SaveRecipeText()
    {
        if (SelectedRecipe is not { } recipe) return;
        string name = RecipeNameBox.Text.Trim(), notes = RecipeNotesBox.Text.TrimEnd();
        if (name.Length == 0) name = recipe.Name;
        if (name == recipe.Name && notes == recipe.Notes) return;
        if (name != recipe.Name) name = Cr2wRecipe.UniqueName(name, _recipes.Select(r => r.Recipe).Where(r => r != recipe));
        recipe.Name = name;
        recipe.Notes = notes;
        if (TrySave(recipe)) LoadRecipes(recipe.FilePath);
    }

    private void RecipeRun_Click(object sender, RoutedEventArgs e) => _ = RunRecipeAsync(dryRun: false);
    private void RecipeCheck_Click(object sender, RoutedEventArgs e) => _ = RunRecipeAsync(dryRun: true);

    private void RecipeList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecipeList, source) is ListBoxItem) _ = RunRecipeAsync(dryRun: false);
    }

    private async Task RunRecipeAsync(bool dryRun)
    {
        if (_cts is not null || SelectedRecipe is not { } recipe) return;
        SaveRecipeText();
        if (!dryRun && recipe.Kind == Cr2wRecipeKind.Edit && string.IsNullOrWhiteSpace(recipe.Output) && !await Message.Show(L.T("Cr2wJsonWindow.SaveOverFiles"),
                L.T("Cr2wJsonWindow.EditsSavedIntoFiles"),
                MessageKind.Question, primary: L.T("Cr2wJsonWindow.SaveOverThem"), secondary: L.T("Common.Cancel")))
            return;
        string doing = dryRun ? L.T("Cr2wJsonWindow.Checking") : recipe.Kind switch
        {
            Cr2wRecipeKind.Edit => L.T("Cr2wJsonWindow.Editing"),
            Cr2wRecipeKind.Resave => L.T("Cr2wJsonWindow.Resaving"),
            Cr2wRecipeKind.FromJson => L.T("Cr2wJsonWindow.ConvertingBack"),
            _ => L.T("Cr2wJsonWindow.Converting"),
        };
        string noneFound = recipe.Kind switch
        {
            Cr2wRecipeKind.Edit => L.T("Cr2wJsonWindow.NoCR2WConvertedJson"),
            Cr2wRecipeKind.FromJson => L.T("Cr2wJsonWindow.NoJsonFilesMade"),
            _ => L.T("Cr2wJsonWindow.NoCR2WFilesWere"),
        };
        await RunAsync(RecipeRunButton, RecipeStopButton, RecipeProgress, RecipeStatus, RecipeResults, dryRun, doing,
            (progress, token) => recipe.Run(progress, token, dryRun), noneFound, () =>
            {
                if (dryRun) return;
                recipe.LastRun = DateTime.Now;
                // only when it was the last run that changed: a failed save says so, and this isn't worth a message
                try { recipe.Save(RecipesFolder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (RecipeList.SelectedItem is Cr2wRecipeRow row && row.Recipe == recipe)
                {
                    int index = _recipes.IndexOf(row);
                    _recipes[index] = new Cr2wRecipeRow(recipe, Summary(recipe));
                    RecipeList.SelectedIndex = index;
                }
            });
    }

    /// <summary>Puts the recipe back in its tab's form, to change it or run it from there.</summary>
    private async void RecipeLoad_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe) return;
        if (recipe.Kind != Cr2wRecipeKind.Edit)
        {
            _convertInputs.Clear();
            Add(_convertInputs, recipe.Inputs);
            (recipe.Kind switch { Cr2wRecipeKind.FromJson => FromJsonRadio, Cr2wRecipeKind.Resave => ResaveRadio, _ => ToJsonRadio }).IsChecked = true;
            AsLatestCheck.IsChecked = recipe.AsLatest;
            ConvertRecursiveCheck.IsChecked = recipe.Recursive;
            OverwriteCheck.IsChecked = recipe.Overwrite;
            SetOutput(ConvertFolderRadio, ConvertBesideRadio, ConvertOutputBox, recipe.Output);
            Tabs.SelectedIndex = 0;
            return;
        }
        var rows = new List<Cr2wEditRow>();
        var failed = new List<string>();
        foreach (var saved in recipe.Edits)
        {
            try { rows.Add(new Cr2wEditRow(saved.ToEdit(), Describe(saved), saved.Source)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
            {
                failed.Add($"{Describe(saved)}: {ex.Message}");
            }
        }
        _bulkInputs.Clear();
        Add(_bulkInputs, recipe.Inputs);
        _edits.Clear();
        foreach (var row in rows) _edits.Add(row);
        BulkRecursiveCheck.IsChecked = recipe.Recursive;
        SetOutput(BulkFolderRadio, BulkInPlaceRadio, BulkOutputBox, recipe.Output);
        Tabs.SelectedIndex = 1;
        if (failed.Count > 0)
            await ShowMessage(L.T("Cr2wJsonWindow.RecipeEditsNotLoaded", failed.Count), string.Join("\n\n", failed), MessageKind.Warning);
    }

    private static void SetOutput(RadioButton intoFolder, RadioButton other, TextBox box, string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) other.IsChecked = true;
        else
        {
            intoFolder.IsChecked = true;
            box.Text = output;
        }
    }

    private void RecipePython_Click(object sender, RoutedEventArgs e) => Export(python: true);
    private void RecipeBatch_Click(object sender, RoutedEventArgs e) => Export(python: false);

    /// <summary>Writes the recipe as a Python script or a batch file that runs this copy of the app.</summary>
    private async void Export(bool python)
    {
        if (SelectedRecipe is not { } recipe) return;
        SaveRecipeText();
        var dialog = new SaveFileDialog
        {
            Title = python ? L.T("Cr2wJsonWindow.RecipeExportPythonTitle") : L.T("Cr2wJsonWindow.RecipeExportBatchTitle"),
            Filter = python ? L.T("Cr2wJsonWindow.PythonFilter") : L.T("Cr2wJsonWindow.BatchFilter"),
            FileName = ScriptName(recipe.Name) + (python ? ".py" : ".bat"),
            InitialDirectory = Directory.Exists(RecipesFolder) ? RecipesFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        string program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "W3ModWorkbench.exe");
        try
        {
            string text = python ? Cr2wRecipeExport.ToPython(recipe, program) : Cr2wRecipeExport.ToBatch(recipe, program);
            await File.WriteAllTextAsync(dialog.FileName, text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("Cr2wJsonWindow.RecipeCantExport"), ex.Message, MessageKind.Warning);
            return;
        }
        if (await Message.Show(L.T("Cr2wJsonWindow.RecipeExported", Path.GetFileName(dialog.FileName)),
                python ? L.T("Cr2wJsonWindow.RecipeExportedPython") : L.T("Cr2wJsonWindow.RecipeExportedBatch"),
                MessageKind.Info, primary: L.T("Common.ShowExplorer"), secondary: L.T("Common.OK")))
            ShellReveal.Show([dialog.FileName]);
    }

    /// <summary>A recipe's name as a script's file name: letters, digits and underscores.</summary>
    private static string ScriptName(string name)
    {
        string result = new([.. name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')]);
        while (result.Contains("__")) result = result.Replace("__", "_");
        result = result.Trim('_');
        return result.Length == 0 ? "recipe" : result;
    }

    /// <summary>The recipe's command line, to paste into a terminal or a script of one's own.</summary>
    private void RecipeCopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe) return;
        string program = Environment.ProcessPath ?? "W3ModWorkbench.exe";
        string command = string.Join(" ", new[] { program }.Concat(Cr2wRecipeExport.Arguments(recipe)).Select(Cr2wRecipeExport.Cmd));
        try
        {
            Clipboard.SetText(command);
            RecipeStatus.Text = L.T("Common.CopiedClipboard");
        }
        catch (System.Runtime.InteropServices.ExternalException ex) { RecipeStatus.Text = ex.Message; }
    }

    private async void RecipeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe) return;
        if (!await Message.Show(L.T("Cr2wJsonWindow.RecipeDeleteTitle", recipe.Name), L.T("Cr2wJsonWindow.RecipeDeleteMessage"),
                MessageKind.Question, primary: L.T("Cr2wJsonWindow.RecipeDelete"), secondary: L.T("Common.Cancel")))
            return;
        try { recipe.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("Cr2wJsonWindow.RecipeCantDelete"), ex.Message, MessageKind.Warning);
            return;
        }
        int index = RecipeList.SelectedIndex;
        LoadRecipes();
        if (_recipes.Count > 0) RecipeList.SelectedIndex = Math.Min(index, _recipes.Count - 1);
    }

    private void RecipeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RecipeDelete_Click(sender, e);
        else if (e.Key == Key.F2)
        {
            RecipeNameBox.Focus();
            RecipeNameBox.SelectAll();
        }
        else if (e.Key == Key.Enter) _ = RunRecipeAsync(dryRun: false);
        else return;
        e.Handled = true;
    }

    private void RecipeFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecipesFolder);
            ShellReveal.Show([SelectedRecipe?.FilePath ?? RecipesFolder]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = ShowMessage(L.T("Cr2wJsonWindow.FolderCantUsed"), ex.Message, MessageKind.Warning);
        }
    }

    private void RecipeImport_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(L.T("Cr2wJsonWindow.RecipeImportTitle"), L.T("Cr2wJsonWindow.RecipeFilter")) is { } files) ImportRecipes(files);
    }

    /// <summary>Copies recipe files (someone else's, or kept elsewhere) into the recipes folder.</summary>
    private void ImportRecipes(IEnumerable<string> files)
    {
        string? last = null;
        var failed = new List<string>();
        foreach (string file in files)
        {
            try
            {
                var recipe = Cr2wRecipe.Load(file);
                if (Path.GetDirectoryName(recipe.FilePath)?.Equals(Path.GetFullPath(RecipesFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
                {
                    last = recipe.FilePath;
                    continue;
                }
                // a new recipe rather than the one read, which saving would move out of where it came from
                var fresh = new Cr2wRecipe
                {
                    Name = Cr2wRecipe.UniqueName(recipe.Name, _recipes.Select(r => r.Recipe)), Notes = recipe.Notes, Kind = recipe.Kind, Inputs = recipe.Inputs,
                    Recursive = recipe.Recursive, Overwrite = recipe.Overwrite, AsLatest = recipe.AsLatest, Output = recipe.Output, Edits = recipe.Edits, LastRun = recipe.LastRun,
                };
                fresh.Save(RecipesFolder);
                last = fresh.FilePath;
                _recipes.Add(new Cr2wRecipeRow(fresh, Summary(fresh)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        LoadRecipes(last);
        if (failed.Count > 0) _ = ShowMessage(L.T("Cr2wJsonWindow.RecipeImportFailed", failed.Count), string.Join("\n\n", failed), MessageKind.Warning);
    }

    // ---------------------------------------------------------------- window

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !Message.IsOpen)
        {
            if (PickerPanel.IsVisible) PickerPanel.Visibility = Visibility.Collapsed;
            else Close();
            e.Handled = true;
        }
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));
}
