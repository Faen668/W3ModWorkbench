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
    public string Detail => Result.Error ?? (dryRun && Result.Output is not null ? "Would be saved" + (Result.Note is null ? "" : $": {Result.Note}") : Result.Note) ?? "";
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

/// <summary>A bulk edit waiting in the list, with how it reads.</summary>
public sealed record Cr2wEditRow(Cr2wBulkEdit Edit, string Description);

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
    private CancellationTokenSource? _cts;

    private Cr2wJsonWindow()
    {
        InitializeComponent();
        ConvertList.ItemsSource = _convertInputs;
        BulkList.ItemsSource = _bulkInputs;
        EditsList.ItemsSource = _edits;
        var settings = App.Settings;
        ConvertOutputBox.Text = BulkOutputBox.Text = settings.Cr2wJsonOutput;
        ConvertFolderRadio.IsChecked = settings.Cr2wJsonOutput.Length > 0;
        ConvertRecursiveCheck.IsChecked = BulkRecursiveCheck.IsChecked = settings.Cr2wJsonRecursive;
        Kind_Changed(this, null!);
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

    private const string AnyFilter = "Game resource files and their JSON|*.*";

    private void ConvertAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles("Choose files to convert", AnyFilter) is { } files) Add(_convertInputs, files);
    }

    private void ConvertAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose a folder to convert") is { } folder) Add(_convertInputs, [folder]);
    }

    private void ConvertRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected(ConvertList, _convertInputs);
    private void ConvertClear_Click(object sender, RoutedEventArgs e) => _convertInputs.Clear();

    private void BulkAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles("Choose files to edit", AnyFilter) is { } files) Add(_bulkInputs, files);
    }

    private void BulkAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose a folder of files to edit") is { } folder) Add(_bulkInputs, [folder]);
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
        // dropped on the source box: that's the file to copy from
        if (Tabs.SelectedIndex == 1 && IsInside(e.OriginalSource as DependencyObject, SourceBox) && paths.Length == 1)
        {
            SourceBox.Text = paths[0];
            return;
        }
        Add(Tabs.SelectedIndex == 0 ? _convertInputs : _bulkInputs, paths);
        // dropping JSON files on the convert tab means converting them back
        if (Tabs.SelectedIndex == 0 && paths.All(p => p.EndsWith(Cr2wJsonBatch.Suffix, StringComparison.OrdinalIgnoreCase))) FromJsonRadio.IsChecked = true;
        else if (Tabs.SelectedIndex == 0 && paths.All(File.Exists) && paths.All(Cr2wJsonBatch.IsCr2w)) ToJsonRadio.IsChecked = true;
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
        if (PickFolder("Choose where to save the converted files", ConvertOutputBox.Text) is { } folder) ConvertOutputBox.Text = folder;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        if (_convertInputs.Count == 0)
        {
            await ShowMessage("Nothing to convert", "Add files or folders first, or drop them on the window.", MessageKind.Info);
            return;
        }
        bool fromJson = FromJsonRadio.IsChecked == true;
        string? output = OutputFolder(ConvertFolderRadio, ConvertOutputBox);
        if (output == "") return;
        bool recursive = ConvertRecursiveCheck.IsChecked == true, overwrite = OverwriteCheck.IsChecked == true;
        var paths = _convertInputs.Select(r => r.Path).ToList();

        await RunAsync(ConvertButton, ConvertStopButton, ConvertProgress, ConvertStatus, ConvertResults, dryRun: false,
            fromJson ? "Converting back" : "Converting", (progress, token) =>
            {
                var inputs = Cr2wJsonBatch.Inputs(paths, fromJson, recursive);
                return inputs.Count == 0 ? null
                    : fromJson ? Cr2wJsonBatch.FromJson(inputs, output, overwrite, progress, token)
                    : Cr2wJsonBatch.ToJson(inputs, output, overwrite, progress, token);
            },
            fromJson ? "No .json files made by this tool were found there." : "No CR2W files were found there.");
    }

    /// <summary>The output folder chosen, null for beside the files, or "" when one is chosen but not given (and said so).</summary>
    private string? OutputFolder(RadioButton intoFolder, TextBox box)
    {
        if (intoFolder.IsChecked != true) return null;
        string folder = box.Text.Trim();
        if (folder.Length == 0)
        {
            _ = ShowMessage("Choose a folder", "Choose the folder to save into, or save beside the files.", MessageKind.Info);
            return "";
        }
        try { return Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = ShowMessage("That folder can't be used", ex.Message, MessageKind.Warning);
            return "";
        }
    }

    /// <summary>Runs a batch in the background with its progress shown, then lists what happened to each file.</summary>
    private async Task RunAsync(Button start, Button stop, ProgressBar bar, TextBlock status, ListBox results, bool dryRun, string doing,
        Func<IProgress<Cr2wJsonProgress>, CancellationToken, List<Cr2wJsonResult>?> work, string noneFound)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        start.IsEnabled = CheckButton.IsEnabled = ApplyButton.IsEnabled = ConvertButton.IsEnabled = false;
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
            status.Text = $"{doing} {p.Done:N0} of {p.Total:N0}  ·  {Path.GetFileName(p.Current)}";
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
            // failures first, then what was skipped, then the rest, each in the order the files were found
            results.ItemsSource = list.Select((r, i) => (Row: new Cr2wResultRow(r, dryRun), i))
                .OrderBy(x => x.Row.Result.Ok ? x.Row.Result.Output is null ? 1 : 2 : 0).ThenBy(x => x.i).Select(x => x.Row).ToList();
            int failed = list.Count(r => !r.Ok), skipped = list.Count(r => r.Ok && r.Output is null), done = list.Count - failed - skipped;
            string verb = dryRun ? "would be saved" : "saved";
            status.Text = $"{done:N0} {verb}" + (skipped > 0 ? $", {skipped:N0} unchanged" : "") + (failed > 0 ? $", {failed:N0} failed" : "")
                + (token.IsCancellationRequested ? " (stopped)" : "") + $"  ·  {clock.Elapsed.TotalSeconds:F1} s";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Stopped.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            start.IsEnabled = CheckButton.IsEnabled = ApplyButton.IsEnabled = ConvertButton.IsEnabled = true;
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
        AmountLabel.Text = kind == BulkEditKind.Scale ? "Multiply by" : "Add";
        if (kind == BulkEditKind.Scale && AmountBox.Text == "0") AmountBox.Text = "1";
        if (kind == BulkEditKind.Offset && AmountBox.Text == "1") AmountBox.Text = "0";
    }

    private void SourceBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles("Choose the file to copy from", AnyFilter) is [var file, ..]) SourceBox.Text = file;
    }

    private void BulkBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose where to save the edited files", BulkOutputBox.Text) is { } folder) BulkOutputBox.Text = folder;
    }

    /// <summary>Shows the source file's properties (or the first file to edit's) to pick the path from.</summary>
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        string? file = Kind == BulkEditKind.Copy && SourceBox.Text.Trim() is { Length: > 0 } source ? source
            : _bulkInputs.Select(r => r.Path).Select(p => File.Exists(p) ? p : Cr2wJsonBatch.Inputs([p], false, true).Select(i => i.File).FirstOrDefault()
                ?? Cr2wJsonBatch.Inputs([p], true, true).Select(i => i.File).FirstOrDefault()).FirstOrDefault(p => p is not null);
        if (file is null)
        {
            await ShowMessage("No file to choose from", Kind == BulkEditKind.Copy ? "Choose the file to copy from first." : "Add a file to edit first.", MessageKind.Info);
            return;
        }
        PickerTitle.Text = "Reading " + Path.GetFileName(file) + "…";
        PickerPanel.Visibility = Visibility.Visible;
        PickerTree.ItemsSource = null;
        try
        {
            var nodes = await Task.Run(() => Nodes(Cr2wJsonBatch.Load(Path.GetFullPath(file))));
            PickerTree.ItemsSource = nodes;
            PickerTitle.Text = $"Properties of {Path.GetFileName(file)}: pick one";
            if (PickerTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first) first.IsExpanded = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            PickerPanel.Visibility = Visibility.Collapsed;
            await ShowMessage($"{Path.GetFileName(file)} can't be read", ex.Message, MessageKind.Warning);
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
                Summary = obj["$data"] is not null ? "kept as bytes" : $"object {i}",
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
            JsonObject o when o["$raw"] is not null => "kept as bytes",
            JsonObject o when o["dataCurveValues"] is JsonArray points => $"curve, {points.Count} point{(points.Count == 1 ? "" : "s")}",
            JsonObject o => o.Count == 0 ? "{ }" : $"{o.Count(p => !p.Key.StartsWith('$'))} properties",
            JsonArray a => $"{a.Count} item{(a.Count == 1 ? "" : "s")}",
            null => "null",
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
            _edits.Add(new Cr2wEditRow(edit, Describe(edit)));
            PickerPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _ = ShowMessage("That edit can't be made", ex.Message, MessageKind.Warning);
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
                if (source.Length == 0) throw new FormatException("Choose the file to copy from.");
                var document = Cr2wJsonBatch.Load(Path.GetFullPath(source));
                var edit = new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className, Source = document };
                if (edit.SourceObject() is not { } from || Cr2wBulkEdit.Find(from, Cr2wBulkEdit.ParsePath(path)) is null)
                    throw new FormatException($"{Path.GetFileName(source)} has nothing at {path}.");
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
                    throw new FormatException($"\"{AmountBox.Text}\" isn't a number.");
                var fields = FieldsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className, Amount = amount, Fields = fields };
            }
            default:
                return new Cr2wBulkEdit { Kind = kind, Path = path, ClassName = className };
        }
    }

    private string Describe(Cr2wBulkEdit edit)
    {
        string where = edit.ClassName.Length > 0 ? $" in {edit.ClassName}" : "";
        string fields = edit.Fields.Count > 0 ? $" (in {string.Join(", ", edit.Fields)})" : "";
        return edit.Kind switch
        {
            BulkEditKind.Copy => $"Copy {edit.Path} from {Path.GetFileName(SourceBox.Text.Trim())}{where}",
            BulkEditKind.Set => $"Set {edit.Path} to {Shorten(edit.Value?.ToJsonString() ?? "null")}{where}",
            BulkEditKind.Remove => $"Remove {edit.Path}{where}",
            BulkEditKind.Scale => $"Multiply the numbers under {edit.Path}{fields} by {edit.Amount.ToString(CultureInfo.InvariantCulture)}{where}",
            _ => $"Add {edit.Amount.ToString(CultureInfo.InvariantCulture)} to the numbers under {edit.Path}{fields}{where}",
        };
    }

    private void BulkCheck_Click(object sender, RoutedEventArgs e) => _ = BulkAsync(dryRun: true);
    private void BulkApply_Click(object sender, RoutedEventArgs e) => _ = BulkAsync(dryRun: false);

    private async Task BulkAsync(bool dryRun)
    {
        if (_cts is not null) return;
        if (_bulkInputs.Count == 0)
        {
            await ShowMessage("No files to edit", "Add files or folders to edit first, or drop them on the window.", MessageKind.Info);
            return;
        }
        var edits = _edits.Select(r => r.Edit).ToList();
        if (edits.Count == 0)
        {
            // the edit in the boxes, when it hasn't been added to the list
            try { edits.Add(MakeEdit()); }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await ShowMessage("No edits to make", "Add an edit to the list first.\n\n" + ex.Message, MessageKind.Info);
                return;
            }
        }
        string? output = OutputFolder(BulkFolderRadio, BulkOutputBox);
        if (output == "") return;
        if (!dryRun && output is null && !await Message.Show("Save over the files?",
                $"The edits are saved into the files themselves, which can't be undone. Keep a copy, or save into a folder instead.",
                MessageKind.Question, primary: "Save over them", secondary: "Cancel"))
            return;
        bool recursive = BulkRecursiveCheck.IsChecked == true;
        var paths = _bulkInputs.Select(r => r.Path).ToList();
        await RunAsync(ApplyButton, BulkStopButton, BulkProgress, BulkStatus, BulkResults, dryRun, dryRun ? "Checking" : "Editing", (progress, token) =>
        {
            // CR2W files and converted JSON files alike
            var inputs = Cr2wJsonBatch.Inputs(paths, fromJson: false, recursive);
            inputs.AddRange(Cr2wJsonBatch.Inputs(paths.Where(Directory.Exists), fromJson: true, recursive).Where(j => !inputs.Any(i => i.File.Equals(j.File, StringComparison.OrdinalIgnoreCase))));
            return inputs.Count == 0 ? null : Cr2wJsonBatch.BulkEdit(inputs, edits, output, progress, token, dryRun);
        }, "No CR2W or converted JSON files were found there.");
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
