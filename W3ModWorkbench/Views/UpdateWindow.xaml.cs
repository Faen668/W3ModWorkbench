using System.IO;
using System.Net.Http;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Checks GitHub for a newer release of the app, shows its notes, downloads it and puts it in place of this copy, then
/// restarts into it. A copy that can't replace itself (a build from source, or a folder it can't write to) downloads
/// the zip for the user to unpack. Opened from the About card, Settings and the main window's Update button.
/// </summary>
public partial class UpdateWindow : Window
{
    private enum State { Checking, Latest, Ahead, Available, Downloading, Installed, Downloaded, Failed }

    private static UpdateWindow? _open;

    /// <summary>The updated exe to start once this copy has closed (Restart now), or null.</summary>
    private static string? _restartInto;

    private State _state;
    private AppRelease? _release;
    private string? _zip;
    private CancellationTokenSource? _cancel;
    private readonly InstallBlock _block = AppUpdates.CanInstall();

    private UpdateWindow()
    {
        InitializeComponent();
        AutoCheck.IsChecked = App.Settings.CheckForUpdates;
        Closing += (_, e) =>
        {
            // closing while downloading stops the download
            _cancel?.Cancel();
        };
        Closed += (_, _) => _open = null;
    }

    /// <summary>Opens the window (or brings the open one forward) and checks, or shows a release the start-up check found.</summary>
    /// <param name="modal">Over a dialog (Settings), so it waits there rather than closing with it.</param>
    public static void Open(Window owner, AppRelease? known = null, bool modal = false)
    {
        if (_open is { } open)
        {
            open.Activate();
            if (known is not null && open._state is State.Latest or State.Failed) open.Show(known);
            return;
        }
        var window = new UpdateWindow { Owner = owner };
        _open = window;
        window.Loaded += (_, _) =>
        {
            if (known is not null) window.Show(known);
            else _ = window.CheckAsync();
        };
        if (modal) window.ShowDialog();
        else window.Show();
    }

    // ---------------------------------------------------------------- checking

    private async Task CheckAsync()
    {
        SetState(State.Checking);
        try
        {
            var release = await AppUpdates.LatestAsync();
            Remember(release);
            Show(release);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            SubText.Text = L.T("UpdateWindow.FailedSub", e.Message);
            SetState(State.Failed);
        }
    }

    private static void Remember(AppRelease release)
    {
        App.Settings.UpdateLastCheck = DateTime.Now.ToString("s");
        App.Settings.UpdateLatest = release.Tag;
        App.Settings.Save();
    }

    private void Show(AppRelease release)
    {
        _release = release;
        ShowNotes(release);
        SetState(AppUpdates.IsNewer(release) ? State.Available : release.Version == AppUpdates.Current ? State.Latest : State.Ahead);
    }

    private void ShowNotes(AppRelease release)
    {
        NotesTitle.Text = L.T("UpdateWindow.NotesTitle", release.Version.ToString(3));
        // the notes start with the release's name as a heading, which the title above already says
        var lines = release.Notes.Split('\n').ToList();
        while (lines.Count > 0 && (lines[0].Trim().Length == 0 || lines[0].StartsWith("## ", StringComparison.Ordinal) && lines[0].Contains(release.Version.ToString(3))))
            lines.RemoveAt(0);
        string notes = string.Join('\n', lines).Trim();
        if (notes.Length == 0) notes = L.T("UpdateWindow.NoNotes");
        new Help.HelpRenderer(this, _ => { }, (_, _) => { }).Render(notes, NotesPanel);
    }

    // ---------------------------------------------------------------- what the window shows

    private void SetState(State state)
    {
        _state = state;
        var r = _release;
        string version = r?.Version.ToString(3) ?? "";
        string current = AppUpdates.Current.ToString(3);
        bool hasZip = r?.ZipUrl is { Length: > 0 };

        StateIcon.Text = state switch
        {
            State.Latest or State.Ahead or State.Installed => "",
            State.Downloaded => "",
            State.Failed => "",
            _ => "",
        };
        HeadText.Text = state switch
        {
            State.Checking => L.T("UpdateWindow.Checking"),
            State.Latest => L.T("UpdateWindow.Latest"),
            State.Ahead => L.T("UpdateWindow.Ahead"),
            State.Available => L.T("UpdateWindow.Available", version),
            State.Downloading => L.T("UpdateWindow.Downloading", version),
            State.Installed => L.T("UpdateWindow.Installed", version),
            State.Downloaded => L.T("UpdateWindow.Downloaded", version),
            _ => L.T("UpdateWindow.Failed"),
        };
        if (state != State.Failed)
            SubText.Text = state switch
            {
                State.Checking => L.T("UpdateWindow.CheckingSub"),
                State.Latest => L.T("UpdateWindow.LatestSub", current),
                State.Ahead => L.T("UpdateWindow.AheadSub", current, version),
                State.Available or State.Downloading => hasZip
                    ? L.T("UpdateWindow.AvailableSub", current, r!.Published.LocalDateTime, Format.Bytes(r.ZipSize))
                    : L.T("UpdateWindow.AvailableNoZip", current, r!.Published.LocalDateTime),
                State.Installed => L.T("UpdateWindow.InstalledSub"),
                State.Downloaded => L.T("UpdateWindow.DownloadedSub", _zip ?? ""),
                _ => "",
            };

        NotesCard.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = state is State.Checking or State.Downloading ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = state == State.Checking;
        if (state == State.Checking) ProgressText.Text = "";

        // why this copy only downloads, shown while there's something to download
        BlockedPanel.Visibility = _block != InstallBlock.None && state is State.Available or State.Downloading ? Visibility.Visible : Visibility.Collapsed;
        BlockedText.Text = _block == InstallBlock.NotSingleFile ? L.T("UpdateWindow.BlockedBuild") : L.T("UpdateWindow.BlockedFolder", AppContext.BaseDirectory);

        PageButton.Visibility = r?.Page is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = state == State.Available ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Content = r is not null && App.Settings.UpdateSkipped == r.Tag ? L.T("UpdateWindow.Skipped") : L.T("UpdateWindow.Skip");
        SkipButton.IsEnabled = r is null || App.Settings.UpdateSkipped != r.Tag;

        CloseButton.Content = state switch
        {
            State.Downloading => L.T("Common.Cancel"),
            State.Installed => L.T("UpdateWindow.Later"),
            _ => L.T("UpdateWindow.Close"),
        };
        (string? action, string icon) = state switch
        {
            State.Available when !hasZip => (null, ""),
            State.Available => (_block == InstallBlock.None ? L.T("UpdateWindow.Install") : L.T("UpdateWindow.Download"), ""),
            State.Installed => (L.T("UpdateWindow.Restart"), ""),
            State.Downloaded => (L.T("UpdateWindow.ShowZip"), ""),
            State.Failed => (L.T("UpdateWindow.TryAgain"), ""),
            _ => (null, ""),
        };
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        ActionText.Text = action ?? "";
        ActionIcon.Text = icon;
        // with nothing to do here, Enter closes
        ActionButton.IsDefault = action is not null;
        CloseButton.IsDefault = action is null;
    }

    // ---------------------------------------------------------------- downloading and installing

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case State.Available: if (await NothingPendingAsync()) await DownloadAsync(); break;
            case State.Installed: if (await NothingPendingAsync()) Restart(); break;
            case State.Downloaded when _zip is not null: ShellReveal.Show([_zip]); break;
            case State.Failed: await CheckAsync(); break;
        }
    }

    private async Task DownloadAsync()
    {
        if (_release is not { } release) return;
        _cancel = new CancellationTokenSource();
        SetState(State.Downloading);
        Progress.Value = 0;
        Progress.Maximum = 1;
        ProgressText.Text = "";
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            Progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            ProgressText.Text = L.T("UpdateWindow.Progress", Format.Bytes(p.Done), Format.Bytes(p.Total));
        });
        try
        {
            _zip = await AppUpdates.DownloadAsync(release, progress, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded) SetState(State.Available);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetState(State.Available);
            await Message.Show(L.T("UpdateWindow.DownloadFailed"),
                ex is InvalidDataException ? L.T("UpdateWindow.BadDownload") : ex.Message, MessageKind.Error, primary: L.T("Common.OK"));
            return;
        }
        finally
        {
            _cancel = null;
        }

        if (_block != InstallBlock.None)
        {
            SetState(State.Downloaded);
            return;
        }
        ProgressText.Text = L.T("UpdateWindow.Installing", release.Version.ToString(3));
        Progress.IsIndeterminate = true;
        try
        {
            string zip = _zip;
            string exe = await Task.Run(() => AppUpdates.Install(zip, AppContext.BaseDirectory, Environment.ProcessPath!));
            _restartInto = exe;
            // said once by the updated copy, whether it starts now or later
            App.Settings.UpdateInstalledOver = AppUpdates.Current.ToString(3);
            App.Settings.Save();
            SetState(State.Installed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetState(State.Available);
            await Message.Show(L.T("UpdateWindow.InstallFailed"), L.T("UpdateWindow.InstallFailedText", ex.Message), MessageKind.Error, primary: L.T("Common.OK"));
        }
    }

    /// <summary>
    /// Whether nothing in the app has changes not saved or work still running. An update waits until there's none,
    /// as restarting closes every window: the user saves or closes them first, and the window says which.
    /// </summary>
    private async Task<bool> NothingPendingAsync()
    {
        var pending = SessionSnapshot.PendingChanges();
        if (pending.Count == 0) return true;
        await Message.Show(L.T("UpdateWindow.PendingTitle"),
            L.T("UpdateWindow.PendingText", string.Join("\n", pending.Take(12).Select(p => "•  " + p))), MessageKind.Warning, primary: L.T("Common.OK"));
        return false;
    }

    /// <summary>Writes down what's open and closes the app; the updated copy starts once it has, and opens it all again.</summary>
    private void Restart()
    {
        if (_restartInto is null) return;
        if (Application.Current.MainWindow is MainWindow mainWindow) mainWindow.SaveSession();
        RestartOnExit = true;
        Close();
        // other windows of the app's (standalone viewers) would keep it running: it exits once the main window has closed
        if (Application.Current.MainWindow is not { } main) { Application.Current.Shutdown(); return; }
        main.Closed += (_, _) => Application.Current.Shutdown();
        main.Close();
    }

    /// <summary>Set by Restart now: the updated copy starts when this one exits.</summary>
    private static bool RestartOnExit { get; set; }

    /// <summary>Called as the app exits: starts the updated copy when Restart now was chosen.</summary>
    public static void OnExit()
    {
        if (!RestartOnExit || _restartInto is null) return;
        try { AppUpdates.Start(_restartInto); }
        catch { /* it starts the next time the user does */ }
    }

    // ---------------------------------------------------------------- the rest

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_state == State.Downloading) _cancel?.Cancel();
        else Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        App.Settings.UpdateSkipped = _release.Tag;
        App.Settings.Save();
        if (Owner is MainWindow main) main.ShowUpdateAvailable(null, null);
        SetState(_state);
    }

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (_release?.Page is { Length: > 0 } page) AssetBrowserWindow.OpenLink(page);
    }

    private void AutoCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.CheckForUpdates = AutoCheck.IsChecked == true;
        App.Settings.Save();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowPrivacy(this);

    // ---------------------------------------------------------------- at start-up

    /// <summary>
    /// At start-up: tidies what an update left, says once that the app was updated, and, when the user turned it on,
    /// asks GitHub (at most once a day) whether there's a newer release, which the main window's Update button then shows.
    /// </summary>
    public static async Task StartupAsync(MainWindow main)
    {
        var settings = App.Settings;
        _ = Task.Run(() => AppUpdates.CleanUp(AppContext.BaseDirectory));

        if (settings.UpdateInstalledOver is { Length: > 0 } over)
        {
            settings.UpdateInstalledOver = "";
            settings.Save();
            if (AppUpdates.ParseTag(over) is { } old && old < AppUpdates.Current
                && await main.Message.Show(L.T("UpdateWindow.UpdatedTitle", AppUpdates.Current.ToString(3)),
                    L.T("UpdateWindow.UpdatedText", over), MessageKind.Info, L.T("UpdateWindow.WhatsNew"), L.T("Common.OK")))
                Open(main);
        }

        if (!settings.CheckForUpdates) return;
        // asked less than a day ago: what it said then
        if (DateTime.TryParse(settings.UpdateLastCheck, out var last) && DateTime.Now - last < TimeSpan.FromHours(20))
        {
            if (AppUpdates.ParseTag(settings.UpdateLatest) is { } known && known > AppUpdates.Current && settings.UpdateLatest != settings.UpdateSkipped)
                main.ShowUpdateAvailable(known, null);
            return;
        }
        try
        {
            var release = await AppUpdates.LatestAsync();
            Remember(release);
            if (AppUpdates.IsNewer(release) && release.Tag != settings.UpdateSkipped) main.ShowUpdateAvailable(release.Version, release);
        }
        catch
        {
            // offline, or GitHub busy: nothing to say at start-up; checking by hand says why
        }
    }
}
