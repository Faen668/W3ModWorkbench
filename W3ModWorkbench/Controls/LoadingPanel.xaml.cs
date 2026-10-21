using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Stands over a viewer's 3D view until what it plays is loaded: first a button to load it (so opening a tab or a file
/// doesn't start a long load the user may not want), then, while it loads, what it's doing, a progress bar, how far
/// through it is and Cancel, in the view rather than in a dialog. It reads the job's <see cref="LoadingReporter"/> a few
/// times a second, as <see cref="LoadingDialog"/> does.
/// </summary>
public partial class LoadingPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private LoadingReporter? _reporter;
    private CancellationTokenSource? _cancel;

    /// <summary>Load preview was clicked.</summary>
    public event EventHandler? LoadRequested;

    public LoadingPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>Whether a load is running in it.</summary>
    public bool IsLoading => _cancel is not null;

    /// <summary>Shows the button that loads it, with what loading does.</summary>
    public void Prompt(string title, string text, string? button = null)
    {
        TitleText.Text = title;
        PromptText.Text = text;
        LoadButtonText.Text = button ?? L.T("LoadingPanel.LoadPreview");
        PromptText.Visibility = LoadButton.Visibility = Visibility.Visible;
        ProgressPart.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Runs <paramref name="work"/> showing how it's getting on, and hides when it's done. Null when it was cancelled
    /// (by Cancel or <paramref name="token"/>); any other failure is thrown on.
    /// </summary>
    public async Task<T?> RunAsync<T>(string title, Func<LoadingReporter, CancellationToken, Task<T>> work, CancellationToken token = default)
        where T : class
    {
        var reporter = _reporter = new LoadingReporter();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _cancel = cancel;
        TitleText.Text = title;
        PromptText.Visibility = LoadButton.Visibility = Visibility.Collapsed;
        ProgressPart.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        Visibility = Visibility.Visible;
        Refresh();
        _timer.Start();
        try
        {
            return await work(reporter, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _timer.Stop();
            _cancel = null;
            Visibility = Visibility.Collapsed;
        }
    }

    private void Refresh()
    {
        if (_reporter is null) return;
        var (step, detail, done, total) = _reporter.Now;
        if (CancelButton.IsEnabled) StepText.Text = step;
        DetailText.Text = detail ?? "";
        Bar.IsIndeterminate = total <= 0;
        if (total > 0) Bar.Value = Math.Clamp(done / (double)total, 0, 1);
        CountText.Text = total > 0 ? L.T("LoadingPanel.Of", done, total) : "";
    }

    private void Load_Click(object sender, RoutedEventArgs e) => LoadRequested?.Invoke(this, EventArgs.Empty);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StepText.Text = L.T("LoadingPanel.Stopping");
        _cancel?.Cancel();
    }
}
