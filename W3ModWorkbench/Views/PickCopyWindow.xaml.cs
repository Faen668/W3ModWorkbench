using System.Windows;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>One copy to pick from: where it's from (a mod, a DLC, the game), its place in the load order, and its archive.</summary>
public sealed record CopyChoice(ArchiveEntry Entry, string Rank, string Name, string Kind, string Detail, bool Wins)
{
    public Visibility WinsVisibility => Wins ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Asks which copy of a file to open when more than one archive has it (a mod's, a DLC's, the game's own), listed in the
/// order the game looks for it, the one it loads first and picked. What the copy opened loads comes from its own mod
/// and then the game, so picking a copy picks whose version of everything it uses is seen too.
/// </summary>
public partial class PickCopyWindow : Window
{
    private PickCopyWindow(string name, IReadOnlyList<ArchiveEntry> copies, ArchiveEntry picked)
    {
        InitializeComponent();
        FileTitle.Text = name;
        Summary.Text = L.T("PickCopyWindow.ArchivesHavePickCopy", copies.Count, copies[0].Path);
        var settings = ModScanner.ReadSettings(ModScanner.FindSettingsFile());
        string? modio = ModioLocator.Root();
        Copies.ItemsSource = copies.Select((c, i) =>
        {
            var place = OtherCopiesWindow.PlaceOf(c.Archive.FilePath, settings, modio);
            return new CopyChoice(c, $"#{i + 1}", place.Name, place.Kind, c.Archive.FilePath, i == 0);
        }).ToList();
        // the copy double-clicked (the game's own, from Game files) is picked to start with
        int start = Math.Max(0, copies.ToList().FindIndex(c => ReferenceEquals(c, picked)));
        _picked = start;
        Copies.SelectedIndex = start;
        Loaded += (_, _) => (Copies.ItemContainerGenerator.ContainerFromIndex(start) as UIElement)?.Focus();
    }

    // the copy that was double-clicked
    private readonly int _picked;

    /// <summary>
    /// The copy picked, or null when the dialog was cancelled. <paramref name="picked"/> is the copy double-clicked,
    /// picked to start with; <paramref name="alwaysWinner"/> says whether to open that one without asking from now on.
    /// </summary>
    public static ArchiveEntry? Pick(Window owner, string name, IReadOnlyList<ArchiveEntry> copies, ArchiveEntry picked, out bool alwaysWinner)
    {
        var dialog = new PickCopyWindow(name, copies, picked) { Owner = owner };
        bool opened = dialog.ShowDialog() == true;
        alwaysWinner = opened && dialog.AlwaysWinner.IsChecked == true;
        return opened && dialog.Copies.SelectedItem is CopyChoice choice ? choice.Entry : null;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Copies.SelectedItem is null) return;
        // not asking again means opening the copy double-clicked
        if (AlwaysWinner.IsChecked == true) Copies.SelectedIndex = _picked;
        DialogResult = true;
    }

    private void Copies_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Copies.SelectedItem is not null) Open_Click(sender, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
