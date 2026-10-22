using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Compiles a folder of loose scripts that isn't a project, from the main window's tree, and lists what the compiler found:
/// the warnings and errors, each opening its line, or that it compiled. The blob is saved beside the folder, so a mod's
/// <c>content\scripts</c> makes <c>content\precompiled.rsblob</c>, where the game reads it.
/// </summary>
public partial class CompileWindow : Window, IMessageHost
{
    private static readonly List<CompileWindow> Open = [];

    private readonly MainWindow _main;
    private readonly ScriptFolder _folder;
    private readonly string _output;
    private readonly string _title;
    private CancellationTokenSource? _compile;

    private CompileWindow(MainWindow main, ScriptFolder folder)
    {
        InitializeComponent();
        _main = main;
        _folder = folder;
        string scripts = Path.GetFullPath(folder.FilePath).TrimEnd('\\');
        _output = Path.Combine(Path.GetDirectoryName(scripts) ?? scripts, "precompiled.rsblob");
        _title = L.T("PreviewWindow.Compile2", ScriptFolder.SuggestedName(scripts));
        Title = _title;
        HeadingText.Text = L.T("CompileWindow.CompileScripts");
        FolderText.Text = L.T("CompileWindow.Compiles", scripts);
        FolderText.ToolTip = scripts;
        Results.HideClose();
        Results.Open += (row, _) => OpenRow(row);
        Loaded += (_, _) => _ = CompileAsync();
        Closed += (_, _) =>
        {
            _compile?.Cancel();
            Open.Remove(this);
        };
    }

    /// <summary>Compiles the folder in a window of its own, or again in the one already showing it.</summary>
    public static void Show(MainWindow main, ScriptFolder folder)
    {
        if (Open.FirstOrDefault(w => w._folder.FilePath.Equals(folder.FilePath, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            open.Activate();
            if (open._compile is null) _ = open.CompileAsync();
            return;
        }
        var window = new CompileWindow(main, folder).Standalone(main);
        Open.Add(window);
        window.Show();
    }

    private async Task CompileAsync()
    {
        if (_compile is not null) return;
        AgainButton.IsEnabled = false;
        if (PreviewWindow.CurrentCompiler() is not { } compiler)
        {
            OutputText.Text = "";
            Results.ShowRows(_title, "", [], CompilerChoice.NothingToCompileWith);
            Results.SetActions();
            AgainButton.IsEnabled = true;
            return;
        }
        OutputText.Text = L.T("CompileWindow.SavedAsWith", _output, compiler.Name);
        OutputText.ToolTip = _output;

        // the game's own folders, an installed mod's and a build's are never written
        if (FileSafety.Protection(_output) is { } protection)
        {
            Results.ShowRows(_title, "", [], L.T("CompileWindow.BlobGoesBesideFolder", _output, protection));
            Results.SetActions();
            StatusText.Text = L.T("CompileWindow.CantSaveBlobThere");
            return;
        }

        string inside = Path.GetFullPath(_folder.FilePath).TrimEnd('\\') + "\\";
        var unsaved = PreviewWindow.UnsavedDocuments()
            .Where(d => d.Item.DiskPath is { } disk && Path.GetFullPath(disk).StartsWith(inside, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await Message.Ask(unsaved.Count == 1 ? L.T("PreviewWindow.SaveFirst", unsaved[0].FileName) : L.T("PreviewWindow.SaveFilesFirst", unsaved.Count),
                L.T(compiler.Kind == CompilerKind.WccLite ? "PreviewWindow.WccCompilesDisk" : "PreviewWindow.CompilerCompilesDisk")
                    + (unsaved.Count == 1 ? "" : L.T("PreviewWindow.HaveChanges", PreviewWindow.Names(unsaved))),
                MessageKind.Question, L.T("PreviewWindow.SaveCompile"), L.T("Common.Cancel"));
            if (choice != MessageChoice.Primary || !PreviewWindow.SaveAll(unsaved))
            {
                AgainButton.IsEnabled = true;
                return;
            }
        }

        // compiled into a file of its own first, so a blob that's there already is only replaced by one that worked
        string compiled = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "compile-" + Guid.NewGuid().ToString("N"), "precompiled.rsblob");
        var cancel = _compile = new CancellationTokenSource();
        StatusText.Text = "";
        Results.SetActions();
        Results.ShowBusy(_title, compiler.Kind == CompilerKind.WccLite ? L.T("PreviewWindow.StartingWccLite") : L.T("PreviewWindow.StartingBuiltCompiler"));
        string slow = compiler.Kind == CompilerKind.WccLite ? L.T("PreviewWindow.TakesAboutMinute") : "";
        var progress = new Progress<string>(text =>
        {
            if (!cancel.IsCancellationRequested) Results.ShowBusy(_title, text + slow);
        });
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await compiler.CompileAsync(_folder.FilePath, compiled, progress, cancel.Token);
            if (result.Success) File.Copy(compiled, _output, overwrite: true);
            ShowResult(result, watch.Elapsed, compiler);
        }
        catch (OperationCanceledException)
        {
            // the window closed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!cancel.IsCancellationRequested)
                Results.ShowRows(_title, L.T("PreviewWindow.Failed"), [], L.T("PreviewWindow.CouldntRun", compiler.Name, ex.Message));
        }
        finally
        {
            _compile = null;
            if (!cancel.IsCancellationRequested) AgainButton.IsEnabled = true;
            TryDelete(Path.GetDirectoryName(compiled)!);
        }
    }

    private void ShowResult(CompileResult result, TimeSpan took, CompilerChoice compiler)
    {
        var (rows, summary, empty) = PreviewWindow.CompileRows(_folder, result, _output, took, compiler);
        Results.ShowRows(_title, summary, rows, empty);
        var actions = new List<(string, Action)>();
        if (result.Success)
            actions.Add((L.T("PreviewWindow.ShowFolder"), () => Process.Start("explorer.exe", $"/select,\"{_output}\"")));
        string logName = compiler.Kind == CompilerKind.WccLite ? "wcc_lite.log" : "compiler.log";
        actions.Add((L.T("PreviewWindow.ShowLog"), () => PreviewWindow.ShowAlone(new PreviewItem(logName, _title, System.Text.Encoding.UTF8.GetBytes(result.Log)), this)));
        // the path is above already, and in the list when nothing's wrong: the strip says only who compiled it
        Results.SetActions(result.Success ? L.T("CompileWindow.CompiledBy", compiler.Name)
            : compiler.Kind == CompilerKind.WccLite ? L.T("PreviewWindow.NothingSavedWccLites") : L.T("PreviewWindow.NothingSavedCompilersLog"), [.. actions]);
    }

    /// <summary>A warning or error opens its line, where scripts are read and written.</summary>
    private async void OpenRow(ResultRow row)
    {
        if (row.File is not { } file) return;
        var entry = _folder.Entries.FirstOrDefault(e => (_folder.FilePath + "|" + e.Path).Equals(file.Key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        try
        {
            byte[] data = await Task.Run(() => entry.Read(null));
            PreviewWindow.ShowAt(_main.PreviewItemFor(entry, data), this, row.Offset, row.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("PreviewWindow.CouldNotRead2", entry.Path), ex.Message, MessageKind.Warning);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a compile's left-over in the temp folder
        }
    }

    private void Again_Click(object sender, RoutedEventArgs e) => _ = CompileAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F7 && AgainButton.IsEnabled)
        {
            _ = CompileAsync();
            e.Handled = true;
        }
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));
}
