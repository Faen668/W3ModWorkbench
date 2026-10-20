using System.Windows;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;

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
    private PickCopyWindow(string name, IReadOnlyList<ArchiveEntry> copies)
    {
        InitializeComponent();
        FileTitle.Text = name;
        Summary.Text = $"{copies.Count} archives have {copies[0].Path}. Pick the copy to open: what it loads (meshes, materials, "
            + "textures, animations) comes from that copy's mod first, then the game.";
        var settings = ModScanner.ReadSettings(ModScanner.FindSettingsFile());
        string? modio = ModioLocator.Root();
        Copies.ItemsSource = copies.Select((c, i) =>
        {
            var place = OtherCopiesWindow.PlaceOf(c.Archive.FilePath, settings, modio);
            return new CopyChoice(c, $"#{i + 1}", place.Name, place.Kind, c.Archive.FilePath, i == 0);
        }).ToList();
        Copies.SelectedIndex = 0;
        Loaded += (_, _) => (Copies.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
    }

    /// <summary>The copy picked, or null when the dialog was cancelled. <paramref name="alwaysWinner"/> says whether not to ask again.</summary>
    public static ArchiveEntry? Pick(Window owner, string name, IReadOnlyList<ArchiveEntry> copies, out bool alwaysWinner)
    {
        var dialog = new PickCopyWindow(name, copies) { Owner = owner };
        bool picked = dialog.ShowDialog() == true;
        alwaysWinner = picked && dialog.AlwaysWinner.IsChecked == true;
        return picked && dialog.Copies.SelectedItem is CopyChoice choice ? choice.Entry : null;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Copies.SelectedItem is null) return;
        // always opening the game's choice means the first
        if (AlwaysWinner.IsChecked == true) Copies.SelectedIndex = 0;
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
