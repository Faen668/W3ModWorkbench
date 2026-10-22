using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// The button at the end of a viewer's info line, for what the file shown is. A game file of the game's own gets
/// "Make a mod…": it and what it's made of copied into the project ready to change (<see cref="ModMakerWindow"/>). A
/// copy of a game file (an installed mod's, the project's, or one in an archive of its own) gets "Compare with
/// game's copy", the two side by side (<see cref="FileCompare"/>). A file opened from a folder that isn't a game file
/// gets neither.
/// </summary>
public sealed class MakeModButton : Button
{
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".bundle", ".cache", ".rsblob", ".zip" };

    private readonly TextBlock _icon, _label;
    private string? _path, _origin, _minePath;
    private Func<byte[]?>? _read;
    private bool _compare;

    public MakeModButton()
    {
        // the app's button look is keyed to Button, which a class of its own doesn't pick up by itself
        SetResourceReference(StyleProperty, typeof(Button));
        Padding = new Thickness(10, 3, 10, 3);
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        _icon = new TextBlock { FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_icon);
        content.Children.Add(_label);
        Content = content;
        Click += async (_, _) =>
        {
            if (_path is null) return;
            byte[]? data = null;
            try { data = _read?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // the game's own copy is read instead
            }
            var owner = Window.GetWindow(this);
            if (!_compare) ModMakerWindow.Open(owner, _path, data);
            else if (owner is not null) await FileCompare.CompareWithGameAsync(owner, _path, Where(_origin), data ?? [], Texture(_path, _origin), _minePath);
        };
    }

    /// <summary>
    /// The file the viewer shows: its game path, where it came from (the archive it's in, or the file on disk for one
    /// opened from a folder) and how to read it.
    /// </summary>
    public void Show(string? gamePath, string? origin, Func<byte[]?>? read = null)
    {
        string? game = GameLocator.Resolve(null);
        bool loose = origin is not null && !Archives.Contains(Path.GetExtension(origin));
        string? path = null;
        bool compare = false;
        if (loose)
        {
            // a file of the project's workspace, or of an installed mod's folder, is a copy of a game file
            path = ProjectWorkspace.Current?.GamePathOf(origin!) ?? (InMod(game, origin!) ? FileSafety.GamePathOf(origin!) : null);
            compare = path is not null;
            // a picture in the project stands for the texture of its name, which Build makes from it
            if (path is not null && (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) || Core.Packing.Pictures.Extensions.Contains(Path.GetExtension(path))))
            {
                _minePath = path;
                path = Path.ChangeExtension(path, ".xbm");
            }
            else _minePath = null;
        }
        else if (!string.IsNullOrWhiteSpace(gamePath) && !Path.IsPathRooted(gamePath) && Path.GetExtension(gamePath).Length > 0)
        {
            path = gamePath;
            // the game's own archives make a mod; anyone else's hold a copy to compare
            compare = origin is not null && !InGame(game, origin);
            bool picture = Path.GetExtension(gamePath).Equals(".dds", StringComparison.OrdinalIgnoreCase) || Core.Packing.Pictures.Extensions.Contains(Path.GetExtension(gamePath));
            if (picture && !compare) path = null;
        }
        _path = path?.Replace('/', '\\');
        _origin = origin;
        _read = path is null ? null : read;
        _compare = compare;
        _icon.Text = compare ? "" : "";
        _label.Text = compare ? L.T("CompareWindow.WithGame") : L.T("ModMaker.Button");
        ToolTip = compare ? L.T("CompareWindow.WithGameTip") : L.T("ModMaker.ButtonTip");
        Visibility = _path is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Whether a file is in the game's own folders: content, or one of the official DLC.</summary>
    private static bool InGame(string? game, string file)
    {
        if (game is null) return true;
        string full = Path.GetFullPath(file);
        string content = Path.GetFullPath(Path.Combine(game, "content")) + "\\";
        if (full.StartsWith(content, StringComparison.OrdinalIgnoreCase)) return true;
        string dlc = Path.GetFullPath(Path.Combine(game, "dlc")) + "\\";
        return full.StartsWith(dlc, StringComparison.OrdinalIgnoreCase) && OfficialDlc.Is(game, full[dlc.Length..].Split('\\')[0]);
    }

    /// <summary>Whether a file is in an installed mod's folder (mods, or a DLC folder that isn't the game's).</summary>
    private static bool InMod(string? game, string file) => game is not null && !InGame(game, file)
        && Path.GetFullPath(file).StartsWith(Path.GetFullPath(game) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to call the copy: the mod or project it's from.</summary>
    private static string Where(string? origin) =>
        origin is null ? L.T("CompareWindow.ThisCopy")
        : ProjectWorkspace.Current is { } project && project.GamePathOf(origin) is not null ? project.Name
        : ArchiveEntry.OwnerOf(origin);

    /// <summary>A copy's pixels for a texture: from the cache it was opened from, or the one beside its bundle.</summary>
    private static Func<TextureEntry?>? Texture(string path, string? origin)
    {
        if (origin is null || !FileCompare.IsTexture(path)) return null;
        string cache = origin.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ? origin : Path.Combine(Path.GetDirectoryName(origin) ?? "", "texture.cache");
        return () => ResourceLocator.Cache(cache) is { } c ? ResourceLocator.FindInCache(c, path) : null;
    }
}
