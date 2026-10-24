using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Voice lines in and out of speech files (<c>enpc.w3speech</c> and the other languages'): a speech file unpacked into
/// a folder of sounds and lip syncs, and a folder packed into a speech file, as the game lays them out. Build packs a
/// project's <c>speech\&lt;language&gt;</c> folders the same way.
/// </summary>
public partial class SpeechWindow : Window, IMessageHost
{
    private CancellationTokenSource? _running;

    private SpeechWindow(string? file)
    {
        InitializeComponent();
        WavCheck.IsChecked = App.Settings.SpeechUnpackWav;
        ReCheck.IsChecked = App.Settings.SpeechUnpackRe;
        ToReRadio.IsChecked = true;
        foreach (var language in W3Strings.Languages) LanguageBox.Items.Add(language.Handle);
        LanguageBox.SelectedItem = "en";
        if (file is not null) Take(file);
        Closing += (_, e) =>
        {
            if (_running is not null) e.Cancel = true;
        };
    }

    /// <summary>Opens the window, on a speech file to unpack or a folder to pack when one is given.</summary>
    public static void Open(Window owner, string? file = null) => new SpeechWindow(file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    /// <summary>A dropped or given path: a speech file goes to unpacking, a folder to packing, lip sync files to converting.</summary>
    private void Take(string path)
    {
        if (Directory.Exists(path)) PackFolderBox.Text = path;
        else if (LipSyncConverter.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) ConvertPathBox.Text = path;
        else UnpackFileBox.Text = path;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (paths.Length == 1) Take(paths[0]);
        else if (paths.All(p => LipSyncConverter.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))) ConvertPathBox.Text = string.Join("; ", paths);
    }

    private static string Clean(TextBox box) => box.Text.Trim().Trim('"');

    // ---------------------------------------------------------------- unpacking

    private void ChooseUnpackFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("SpeechWindow.ChooseSpeechFile"), Filter = L.T("SpeechWindow.Filter") };
        if (dialog.ShowDialog(this) == true) UnpackFileBox.Text = dialog.FileName;
    }

    private void ChooseUnpackFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("SpeechWindow.ChooseFolder") };
        if (dialog.ShowDialog(this) == true) UnpackFolderBox.Text = dialog.FolderName;
    }

    /// <summary>What the speech file holds, as soon as it's chosen, and a folder for it: speech\language beside it, as Build packs.</summary>
    private void UnpackFile_Changed(object sender, TextChangedEventArgs e)
    {
        string path = Clean(UnpackFileBox);
        UnpackButton.IsEnabled = false;
        UnpackInfo.Text = "";
        if (path.Length == 0) return;
        if (!File.Exists(path))
        {
            UnpackInfo.Text = L.T("SpeechWindow.NoSuchFile");
            return;
        }
        if (SpeechFolder.LanguageOf(path) is not { } language)
        {
            UnpackInfo.Text = L.T("SpeechWindow.NameSaysNoLanguage");
            return;
        }
        try
        {
            var speech = W3Speech.Open(path, language);
            UnpackInfo.Text = L.T("SpeechWindow.SpeechInfo", language.Handle, L.T("Count.VoiceLine", speech.Order.Count), Format.Bytes(new FileInfo(path).Length), speech.Version);
            UnpackFolderBox.Text = Path.Combine(Path.GetDirectoryName(path) ?? "", "speech", language.Handle);
            UnpackButton.IsEnabled = speech.Order.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            UnpackInfo.Text = L.T("SpeechWindow.CantRead", ex.Message);
        }
    }

    private async void Unpack_Click(object sender, RoutedEventArgs e)
    {
        string path = Clean(UnpackFileBox), folder = Clean(UnpackFolderBox);
        if (folder.Length == 0 || SpeechFolder.LanguageOf(path) is not { } language) return;
        bool wav = WavCheck.IsChecked == true, re = ReCheck.IsChecked == true;
        App.Settings.SpeechUnpackWav = wav;
        App.Settings.SpeechUnpackRe = re;
        App.Settings.Save();
        await RunAsync(L.T("SpeechWindow.Unpacking"), progress =>
        {
            var speech = W3Speech.Open(path, language);
            var (lines, noWav, noRe) = SpeechFolder.Unpack(speech, folder, wav, re, progress, _running!.Token);
            var result = new List<string> { L.T("SpeechWindow.Unpacked", L.T("Count.VoiceLine", lines), folder) };
            if (noWav.Count > 0) result.Add(L.T("SpeechWindow.NoWav", string.Join(", ", noWav.Take(20)) + (noWav.Count > 20 ? ", …" : "")));
            if (noRe.Count > 0) result.Add(L.T("SpeechWindow.NoRe", string.Join(", ", noRe.Take(20)) + (noRe.Count > 20 ? ", …" : "")));
            return result;
        });
    }

    // ---------------------------------------------------------------- packing

    private void ChoosePackFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("SpeechWindow.ChooseFolder") };
        if (dialog.ShowDialog(this) == true) PackFolderBox.Text = dialog.FolderName;
    }

    private void ChoosePackFile_Click(object sender, RoutedEventArgs e)
    {
        string handle = LanguageBox.SelectedItem as string ?? "en";
        var dialog = new SaveFileDialog { Title = L.T("SpeechWindow.SaveSpeechFile"), Filter = L.T("SpeechWindow.Filter"), FileName = $"{handle}pc.w3speech" };
        if (dialog.ShowDialog(this) == true) PackFileBox.Text = dialog.FileName;
    }

    /// <summary>What the folder holds, the language its name says (speech\en is English) and where the speech file goes.</summary>
    private void PackFolder_Changed(object sender, TextChangedEventArgs e)
    {
        string folder = Clean(PackFolderBox);
        PackInfo.Text = "";
        if (folder.Length > 0 && !Directory.Exists(folder)) PackInfo.Text = L.T("SpeechWindow.NoSuchFolder");
        else if (folder.Length > 0)
        {
            var (lines, _) = SpeechFolder.Read(folder);
            PackInfo.Text = L.T("SpeechWindow.FolderInfo", L.T("Count.VoiceLine", lines.Count), lines.Count(l => l.LipSync is not null));
            if (W3Strings.Language(Path.GetFileName(folder.TrimEnd('\\', '/'))) is { } language) LanguageBox.SelectedItem = language.Handle;
            SuggestFile();
        }
        UpdatePack();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        SuggestFile();
        UpdatePack();
    }

    private void PackFile_Changed(object sender, TextChangedEventArgs e) => UpdatePack();

    /// <summary>The speech file a folder packs into: the language's, beside the folder's speech folder (or beside the folder).</summary>
    private void SuggestFile()
    {
        string folder = Clean(PackFolderBox);
        if (folder.Length == 0 || LanguageBox.SelectedItem is not string handle) return;
        string parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
        if (Path.GetFileName(parent).Equals("speech", StringComparison.OrdinalIgnoreCase)) parent = Path.GetDirectoryName(parent) ?? parent;
        PackFileBox.Text = Path.Combine(parent, $"{handle}pc.w3speech");
    }

    private void UpdatePack()
    {
        string folder = Clean(PackFolderBox), file = Clean(PackFileBox);
        bool exists = file.Length > 0 && File.Exists(file);
        KeepCheck.IsEnabled = exists;
        if (!exists) KeepCheck.IsChecked = false;
        PackButton.IsEnabled = Directory.Exists(folder) && file.Length > 0 && LanguageBox.SelectedItem is string;
    }

    private async void Pack_Click(object sender, RoutedEventArgs e)
    {
        string folder = Clean(PackFolderBox), file = Clean(PackFileBox);
        if (LanguageBox.SelectedItem is not string handle || W3Strings.Language(handle) is not { } language) return;
        bool keep = KeepCheck.IsChecked == true;
        await RunAsync(L.T("SpeechWindow.Packing"), progress =>
        {
            var (lines, notes) = SpeechFolder.Read(folder);
            var result = new List<string>();
            if (lines.Count == 0)
            {
                result.Add(L.T("SpeechWindow.NoLines"));
                return [.. result, .. notes];
            }
            var startFrom = keep ? W3Speech.Open(file, language) : null;
            int total = SpeechFolder.Pack(lines, language, file, startFrom, progress, _running!.Token, App.Settings.SpeechKbps);
            result.Add(startFrom is null ? L.T("SpeechWindow.Packed", L.T("Count.VoiceLine", total), file)
                : L.T("SpeechWindow.PackedWith", L.T("Count.VoiceLine", total), file, L.T("Count.VoiceLine", lines.Count)));
            return [.. result, .. notes];
        });
    }

    // ---------------------------------------------------------------- converting lip syncs

    /// <summary>The files and folders the convert box names, separated by semicolons.</summary>
    private List<string> ConvertPaths() =>
        [.. ConvertPathBox.Text.Split(';').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0)];

    private void ChooseConvertFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("SpeechWindow.ChooseLipSyncs"), Filter = L.T("SpeechWindow.LipSyncFilter"), Multiselect = true };
        if (dialog.ShowDialog(this) == true) ConvertPathBox.Text = string.Join("; ", dialog.FileNames);
    }

    private void ChooseConvertFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("SpeechWindow.ChooseFolder") };
        if (dialog.ShowDialog(this) == true) ConvertPathBox.Text = dialog.FolderName;
    }

    private void ConvertPath_Changed(object sender, TextChangedEventArgs e) => UpdateConvert();

    private void UpdateConvert()
    {
        var paths = ConvertPaths();
        ConvertButton.IsEnabled = _running is null && paths.Count > 0 && paths.All(p => File.Exists(p) || Directory.Exists(p));
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var paths = ConvertPaths();
        var format = ToRawRadio.IsChecked == true ? LipSyncFormat.Raw : LipSyncFormat.Re;
        bool overwrite = ConvertOverwriteCheck.IsChecked == true;
        await RunAsync(L.T("SpeechWindow.Converting"), progress =>
        {
            // a folder's subfolders too, as a project keeps a language a folder
            var inputs = LipSyncConverter.Inputs(paths, recursive: true);
            if (inputs.Count == 0) return [L.T("SpeechWindow.NoLipSyncs")];
            var results = LipSyncConverter.Convert(inputs, format, null, overwrite, progress, _running!.Token);
            var lines = new List<string>
            {
                L.T("SpeechWindow.Converted", L.T("Count.LipSync", results.Count(r => r.Output is not null)), LipSyncConverter.Extension(format),
                    results.Count(r => r.Ok && r.Output is null), results.Count(r => !r.Ok)),
            };
            foreach (var r in results.Where(r => !r.Ok || r.Note is not null).Take(30))
                lines.Add($"{Path.GetFileName(r.Input)}: {r.Error ?? r.Note}");
            return lines;
        });
    }

    // ---------------------------------------------------------------- running

    /// <summary>Runs one job in the background with its progress shown, then lists what it says (or why it failed).</summary>
    private async Task RunAsync(string busy, Func<IProgress<(int Done, int Total)>, List<string>> job)
    {
        _running = new CancellationTokenSource();
        UnpackButton.IsEnabled = PackButton.IsEnabled = ConvertButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        ResultsText.Text = busy;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            ResultsText.Text = $"{busy} {p.Done:N0} / {p.Total:N0}";
        });
        try
        {
            var lines = await Task.Run(() => job(progress));
            ResultsText.Text = string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            ResultsText.Text = "";
            await ShowMessage(L.T("SpeechWindow.Failed"), ex.Message, MessageKind.Error);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            Progress.Visibility = Visibility.Collapsed;
            UnpackButton.IsEnabled = File.Exists(Clean(UnpackFileBox)) && SpeechFolder.LanguageOf(Clean(UnpackFileBox)) is not null;
            UpdatePack();
            UpdateConvert();
        }
    }
}
