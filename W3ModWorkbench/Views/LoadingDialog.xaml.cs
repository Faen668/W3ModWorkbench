using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace W3ModWorkbench;

/// <summary>
/// The dialog <see cref="Loading.RunAsync"/> shows while a long job runs: its title, the step it's on, a progress bar
/// (a count when the job knows how many, a moving bar when it doesn't), a detail and how far through it is, and Cancel.
/// Closing it cancels too. It reads the job's <see cref="LoadingReporter"/> a few times a second.
/// </summary>
public partial class LoadingDialog : Window
{
    private readonly LoadingReporter _reporter;
    private readonly CancellationTokenSource _cancel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _finished;

    public LoadingDialog(string title, LoadingReporter reporter, CancellationTokenSource cancel)
    {
        InitializeComponent();
        _reporter = reporter;
        _cancel = cancel;
        Title = title;
        TitleText.Text = title;
        _timer.Tick += (_, _) => Refresh();
        Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        var (step, detail, done, total) = _reporter.Now;
        StepText.Text = step;
        DetailText.Text = detail ?? "";
        Bar.IsIndeterminate = total <= 0;
        if (total > 0) Bar.Value = Math.Clamp(done / (double)total, 0, 1);
        CountText.Text = total > 0 ? $"{done} of {total}" : "";
    }

    /// <summary>Closes it when the job is done, without cancelling anything.</summary>
    public void Finish()
    {
        _finished = true;
        _timer.Stop();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StepText.Text = "Stopping…";
        _cancel.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // closed by the user: the job is stopped, and the dialog closes when it has
        if (!_finished)
        {
            e.Cancel = true;
            Cancel_Click(this, new RoutedEventArgs());
        }
        base.OnClosing(e);
    }
}
