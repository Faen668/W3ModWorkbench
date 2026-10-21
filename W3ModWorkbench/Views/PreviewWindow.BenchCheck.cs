using System.IO;
using System.Windows;
using System.Windows.Threading;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Script Bench checks your project's scripts as you type: a moment after typing stops (or a save), the built-in
/// compiler checks them as Compile would, with the unsaved text of the open tabs, and what's wrong is underlined in each
/// open script and counted in the status bar, which lists them all when clicked. The game's scripts and the installed
/// mods are read once, when the bench opens; Refresh reads them again.
/// </summary>
public partial class PreviewWindow
{
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private CancellationTokenSource? _checkCts;

    /// <summary>What the last check found, by file: its path under the scripts folder.</summary>
    private Dictionary<string, List<CompileMessage>> _problems = new(StringComparer.OrdinalIgnoreCase);

    private bool _checking;

    private void SetUpBenchCheck()
    {
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            _ = CheckProjectAsync();
        };
        PreviewDocument.AnyTextEdited += OnScriptChanged;
        PreviewDocument.AnySaved += OnScriptChanged;
        Closed += (_, _) =>
        {
            PreviewDocument.AnyTextEdited -= OnScriptChanged;
            PreviewDocument.AnySaved -= OnScriptChanged;
            _checkCts?.Cancel();
        };
    }

    private void OnScriptChanged(PreviewDocument doc)
    {
        if (doc.Host == this && UnderScripts(doc) is not null) ScheduleCheck();
    }

    /// <summary>Checks the project once typing has stopped for a moment.</summary>
    private void ScheduleCheck()
    {
        if (!_isBench) return;
        _checkTimer.Stop();
        if (!App.Settings.CheckScriptsAsYouType)
        {
            ClearProblems();
            return;
        }
        _checkTimer.Start();
    }

    /// <summary>A project script's path under the project's scripts folder, or null for any other file.</summary>
    private static string? UnderScripts(PreviewDocument doc)
    {
        if (ProjectWorkspace.Current is not { } project || doc.Item.DiskPath is not { } disk || !doc.Item.IsScript) return null;
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(disk);
        return full.StartsWith(scripts, StringComparison.OrdinalIgnoreCase) ? full[scripts.Length..] : null;
    }

    private async Task CheckProjectAsync()
    {
        if (!App.Settings.CheckScriptsAsYouType || ProjectWorkspace.Current is not { } project || _gameScripts is null)
        {
            ClearProblems();
            return;
        }
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        // what's typed and not saved yet is what's checked
        var unsaved = AllDocuments.Where(d => d.IsDirty).Select(d => (Path: UnderScripts(d), d.Text)).Where(d => d.Path is not null)
            .GroupBy(d => d.Path!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
        string? own = Path.GetFileName(project.BuiltModPath);
        _checking = true;
        UpdateProblemsButton();
        try
        {
            var messages = await Core.Scripts.Standalone.ScriptChecker.CheckAsync(project.ScriptsPath, unsaved, _gameScripts, _modsFolder, own, cts.Token);
            if (cts.IsCancellationRequested) return;
            _problems = messages.GroupBy(m => m.File ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            ApplyProblems();
        }
        catch (OperationCanceledException)
        {
            // a newer check took over
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // a file that can't be read now is read on the next check
        }
        finally
        {
            if (_checkCts == cts)
            {
                _checking = false;
                UpdateProblemsButton();
            }
        }
    }

    /// <summary>Every open script of the project gets its problems underlined; any other file, none.</summary>
    private void ApplyProblems()
    {
        foreach (var doc in AllDocuments) ApplyProblems(doc);
        UpdateProblemsButton();
    }

    private void ApplyProblems(PreviewDocument? doc)
    {
        if (doc?.Item.IsScript != true) return;
        string? path = UnderScripts(doc);
        doc.SetProblems(path is not null && _problems.TryGetValue(path, out var list) ? list : []);
    }

    private void ClearProblems()
    {
        _checkCts?.Cancel();
        _checking = false;
        _problems = new(StringComparer.OrdinalIgnoreCase);
        ApplyProblems();
    }

    /// <summary>The status bar's count of problems, or that it's checking, or that all is well.</summary>
    private void UpdateProblemsButton()
    {
        if (!_isBench) return;
        if (!App.Settings.CheckScriptsAsYouType || ProjectWorkspace.Current is null)
        {
            ProblemsButton.Visibility = Visibility.Collapsed;
            return;
        }
        int count = _problems.Values.Sum(l => l.Count);
        ProblemsButton.Visibility = Visibility.Visible;
        ProblemsIcon.Text = _checking ? "" : count > 0 ? "" : "";
        if (count > 0) ProblemsIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        else ProblemsIcon.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        ProblemsText.Text = _checking && count == 0 ? L.T("ScriptBench.Check.Checking") : count == 0 ? L.T("ScriptBench.Check.NoProblems") : L.T("ScriptBench.Check.Problems", count);
        ProblemsButton.ToolTip = L.T("ScriptBench.Check.Tip");
    }

    /// <summary>The status bar's count clicked: every problem, by file, in the list under the tabs.</summary>
    private void Problems_Click(object sender, RoutedEventArgs e)
    {
        if (_benchMain?.ProjectArchive is not { } project) return;
        var messages = _problems.Values.SelectMany(l => l).ToList();
        var compiler = CompilerChoice.Pick(null, App.Settings.GameFolder, preferBuiltIn: true) ?? CurrentCompiler();
        if (compiler is null) return;
        ShowCompileResult(L.T("ScriptBench.Check.Title"), project, new CompileResult(messages.Count == 0, messages, ""), "", TimeSpan.Zero, compiler, live: true);
    }
}
