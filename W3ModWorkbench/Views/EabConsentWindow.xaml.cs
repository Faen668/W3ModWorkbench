using System.Windows;
using W3ModWorkbench.Core;

namespace W3ModWorkbench;

/// <summary>
/// Asks before EasyAssetBrowser's tags are turned on, saying in plain words what that means: the tool goes online for
/// them (and for nothing else), what's downloaded and from where, what GitHub sees, what's never sent, what's kept, and
/// how to turn it off. Off is the default; turning it on is the user's choice, and when they made it is kept.
/// </summary>
public partial class EabConsentWindow : Window
{
    public const string GitHubPrivacy = "https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement";

    private EabConsentWindow() => InitializeComponent();

    /// <summary>Asks whether to turn EasyAssetBrowser's tags on; true when the user agreed (and it's now on).</summary>
    public static bool Ask(Window owner)
    {
        var window = new EabConsentWindow { Owner = owner };
        if (window.ShowDialog() != true) return false;
        App.Settings.EasyAssetBrowserOn = true;
        App.Settings.EasyAssetBrowserAgreed = DateTime.Now.ToString("s");
        App.Settings.Save();
        return true;
    }

    private void TurnOn_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void NotNow_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void GitHubPrivacy_Click(object sender, RoutedEventArgs e) => AssetBrowserWindow.OpenLink(GitHubPrivacy);
    private void Site_Click(object sender, RoutedEventArgs e) => AssetBrowserWindow.OpenLink(EasyAssetBrowser.Site);
    private void Privacy_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowPrivacy(this);
}
