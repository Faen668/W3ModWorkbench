using System.Windows;

namespace W3ModWorkbench;

/// <summary>
/// What a long job says about how it's getting on, for <see cref="LoadingDialog"/>: the step it's on, a detail under
/// it, and how many of how many it has done (none known shows a bar that moves without a count). Safe to report from
/// any thread; the dialog reads it as it goes.
/// </summary>
public sealed class LoadingReporter
{
    private readonly object _lock = new();
    private string _step = "Loading…";
    private string? _detail;
    private int _done, _total;

    /// <summary>Starts a step, with no count until <see cref="Count"/> gives one.</summary>
    public void Step(string step, string? detail = null)
    {
        lock (_lock) (_step, _detail, _done, _total) = (step, detail, 0, 0);
    }

    /// <summary>The line under the step: which file, which actor.</summary>
    public void Detail(string? detail)
    {
        lock (_lock) _detail = detail;
    }

    /// <summary>How many of how many the step has done.</summary>
    public void Count(int done, int total)
    {
        lock (_lock) (_done, _total) = (done, total);
    }

    /// <summary>The count as a progress to hand on, for work that reports one (<see cref="StageTextures.LoadAsync"/>).</summary>
    public IProgress<(int Done, int Total)> Counter => new Reporting(this);

    public (string Step, string? Detail, int Done, int Total) Now
    {
        get { lock (_lock) return (_step, _detail, _done, _total); }
    }

    // reports straight away on the thread it's called from, unlike Progress, which posts to the thread that made it
    private sealed class Reporting(LoadingReporter reporter) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => reporter.Count(value.Done, value.Total);
    }
}

/// <summary>
/// Runs a long job, such as loading every actor and texture a scene needs before it plays, with a dialog over the
/// window that waits on it: what it's doing, a progress bar, and Cancel. The dialog only comes up when the job takes
/// longer than a moment, so a quick one doesn't flash it, and the window is disabled while it runs.
/// </summary>
public static class Loading
{
    /// <summary>
    /// Runs <paramref name="work"/> with the dialog. Null when it was cancelled (by Cancel, closing the dialog or
    /// <paramref name="token"/>); any other failure is thrown on.
    /// </summary>
    public static async Task<T?> RunAsync<T>(Window? owner, string title, Func<LoadingReporter, CancellationToken, Task<T>> work, CancellationToken token = default)
        where T : class
    {
        var reporter = new LoadingReporter();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var job = work(reporter, cancel.Token);
        LoadingDialog? dialog = null;
        bool wasEnabled = owner?.IsEnabled ?? true;
        if (await Task.WhenAny(job, Task.Delay(350, CancellationToken.None)) != job && !job.IsCompleted)
        {
            dialog = new LoadingDialog(title, reporter, cancel);
            if (owner is { IsLoaded: true }) dialog.Owner = owner;
            if (owner is not null) owner.IsEnabled = false;
            dialog.Show();
        }
        try
        {
            return await job;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (dialog is not null)
            {
                if (owner is not null) owner.IsEnabled = wasEnabled;
                dialog.Finish();
                owner?.Activate();
            }
        }
    }
}
