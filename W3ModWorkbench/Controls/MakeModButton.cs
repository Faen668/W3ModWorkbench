using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// "Make a mod…", at the end of a viewer's info line: copies the game file shown, and what it's made of, into the
/// project ready to change (<see cref="ModMakerWindow"/>). Hidden for a file that isn't one of the game's, such as one
/// of the project's own or a file opened from disk.
/// </summary>
public sealed class MakeModButton : Button
{
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".bundle", ".cache", ".rsblob", ".zip" };

    private string? _path;
    private Func<byte[]?>? _read;

    public MakeModButton()
    {
        // the app's button look is keyed to Button, which a class of its own doesn't pick up by itself
        SetResourceReference(StyleProperty, typeof(Button));
        Padding = new Thickness(10, 3, 10, 3);
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        ToolTip = L.T("ModMaker.ButtonTip");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = L.T("ModMaker.Button"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Content = content;
        Click += (_, _) =>
        {
            if (_path is null) return;
            byte[]? data = null;
            try { data = _read?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // the game's own copy is read instead
            }
            ModMakerWindow.Open(Window.GetWindow(this), _path, data);
        };
    }

    /// <summary>
    /// The file the viewer shows: its game path, where it came from (the archive it's in, or the file on disk for one
    /// opened from a folder) and how to read it. Shown only for a file of the game's, in an archive.
    /// </summary>
    public void Show(string? gamePath, string? origin, Func<byte[]?>? read = null)
    {
        // a file opened from a folder (the project's own, or one extracted) is no game file to copy
        bool loose = origin is not null && !Archives.Contains(Path.GetExtension(origin));
        bool game = !loose && !string.IsNullOrWhiteSpace(gamePath) && !Path.IsPathRooted(gamePath)
            && Path.GetExtension(gamePath) is { Length: > 0 } ext && !ext.Equals(".dds", StringComparison.OrdinalIgnoreCase)
            && !W3ModWorkbench.Core.Packing.Pictures.Extensions.Contains(ext);
        _path = game ? gamePath!.Replace('/', '\\') : null;
        _read = game ? read : null;
        Visibility = game ? Visibility.Visible : Visibility.Collapsed;
    }
}
