using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A file in one of the lists of what uses a file and what it uses: its path, where it's from, and the asset browser's item for it when there is one.</summary>
public sealed record LinkRow(string Path, string From, EntryItem? Item)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// What uses a file and what it uses: on the left every file whose links name it (an entity using a mesh, a mesh its
/// materials, a material its textures…), from the game, the installed mods and REDkit, found with the reference index;
/// on the right the files it names itself, read from it. Either opens with a double-click or goes to it in the browser.
/// </summary>
public partial class UsesWindow : Window
{
    private readonly AssetBrowserWindow _browser;
    private readonly EntryItem _item;

    private UsesWindow(AssetBrowserWindow browser, EntryItem item)
    {
        InitializeComponent();
        _browser = browser;
        _item = item;
        Title = L.T("UsesWindow.WhatUses", item.Name);
        FileTitle.Text = item.Name;
        FilePath.Text = $"{item.Path}   ·   {item.BundleName}";
        AssetBrowserWindow.IndexProgress += ShowProgress;
        Closed += (_, _) => AssetBrowserWindow.IndexProgress -= ShowProgress;
        Loaded += async (_, _) =>
        {
            await ShowUsesAsync();
            await ShowUsedByAsync(rebuild: false);
        };
    }

    /// <summary>Opens the window for a file in the asset browser.</summary>
    public static void Show(AssetBrowserWindow browser, EntryItem item) => new UsesWindow(browser, item).Standalone(browser).Show();

    /// <summary>The files this one names, read from it.</summary>
    private async Task ShowUsesAsync()
    {
        List<string> links;
        try
        {
            byte[] data = await MainWindow.ReadAsync(_item.Entry);
            links = await Task.Run(() => ReferenceIndex.LinksOf(data));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            UsesEmpty.Text = L.T("UsesWindow.CouldntRead", _item.Name, ex.Message);
            UsesEmpty.Visibility = Visibility.Visible;
            return;
        }
        var rows = links.Order(StringComparer.OrdinalIgnoreCase).Select(path => new LinkRow(path, "", _browser.ItemForPath(path))).ToList();
        UsesList.ItemsSource = rows;
        UsesTitle.Text = L.T("UsesWindow.Uses2", rows.Count);
        UsesEmpty.Text = L.T("UsesWindow.DoesntNameAnyOther");
        UsesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The files that name this one, from the index (made first, the first time).</summary>
    private async Task ShowUsedByAsync(bool rebuild)
    {
        UsedByList.ItemsSource = null;
        UsedByEmpty.Visibility = Visibility.Collapsed;
        RebuildButton.IsEnabled = false;
        IndexPanel.Visibility = Visibility.Visible;
        IndexText.Text = L.T("UsesWindow.FindingWhichFilesUse");
        ReferenceIndex index;
        try
        {
            index = await _browser.ReferencesAsync(rebuild);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            IndexPanel.Visibility = Visibility.Collapsed;
            UsedByEmpty.Text = L.T("UsesWindow.FilesCouldntAllRead", ex.Message);
            UsedByEmpty.Visibility = Visibility.Visible;
            RebuildButton.IsEnabled = true;
            return;
        }
        IndexPanel.Visibility = Visibility.Collapsed;
        RebuildButton.IsEnabled = true;
        var rows = index.UsedBy(_item.Path)
            .Select(s => _browser.ItemFor(s) is { } item ? new LinkRow(s.Path, item.BundleName, item) : new LinkRow(s.Path, Path.GetFileName(s.ArchivePath), null))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.From, StringComparer.OrdinalIgnoreCase).ToList();
        UsedByList.ItemsSource = rows;
        UsedByTitle.Text = L.T("UsesWindow.Used2", rows.Count);
        UsedByEmpty.Text = L.T("UsesWindow.NoFileNamesMay", _item.Name);
        UsedByEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowProgress(int done, int total, string name)
    {
        IndexBar.Value = total == 0 ? 0 : (double)done / total;
        IndexDetail.Text = name.Length > 0 ? L.T("UsesWindow.Files", done, total, name) : L.T("UsesWindow.Files2", done, total);
    }

    // the list a file was last highlighted in, which the buttons act on
    private DataGrid? _lastList;

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => _lastList = sender as DataGrid;

    private LinkRow? Highlighted => (_lastList ?? UsedByList).SelectedItem as LinkRow;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as DataGrid)?.SelectedItem is LinkRow row) Open(row);
    }

    private void Open(LinkRow row)
    {
        if (row.Item is { } item) _browser.OpenFromElsewhere(item);
        else MessageBox.Show(this, L.T("UsesWindow.IsntAmongFilesAsset", row.Path), Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted is { } row) Open(row);
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted is { } row) _ = _browser.GoToAsync(row.Path, row.Item);
    }

    private void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        ReferenceIndex.ClearCache();
        _ = ShowUsedByAsync(rebuild: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Highlighted is { } row)
        {
            Open(row);
            e.Handled = true;
        }
    }
}
