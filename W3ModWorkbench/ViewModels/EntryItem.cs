using System.ComponentModel;
using W3ModWorkbench.Core;

namespace W3ModWorkbench.ViewModels;

public sealed class EntryItem : INotifyPropertyChanged
{
    private readonly Action<EntryItem, bool> _checkedChanged;
    private bool _isChecked;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ws", ".xml", ".csv", ".txt", ".json", ".ini", ".yml", ".yaml", ".log", ".lua", ".subs", ".list",
    };

    public EntryItem(ArchiveEntry entry, Action<EntryItem, bool> checkedChanged)
    {
        Entry = entry;
        _checkedChanged = checkedChanged;
        int slash = entry.Path.LastIndexOf('\\');
        Name = slash < 0 ? entry.Path : entry.Path[(slash + 1)..];
        Folder = slash < 0 ? "" : entry.Path[..slash];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ArchiveEntry Entry { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Path => Entry.Path;
    public string BundleName => Entry.Archive.Owner + "\\" + Entry.Archive.Name;
    public int Size => Entry.Size;
    public int PackedSize => Entry.PackedSize;
    public string SizeText => Format.Bytes(Entry.Size);
    public string PackedText => Format.Bytes(Entry.PackedSize);
    public string CompressionText => Entry.FormatText;

    /// <summary>What kind of file it is, in a few words, when that's known.</summary>
    public FileKind? Kind => FileKinds.Of(Name);

    /// <summary>The file's path, and what kind of file it is.</summary>
    public string ToolTip => Kind is { } kind ? $"{Path}\n{kind.Name}: {kind.Description}" : Path;

    /// <summary>Text files open in the preview window; everything else is extract-only.</summary>
    public bool IsPreviewable => TextExtensions.Contains(System.IO.Path.GetExtension(Name));

    /// <summary>An icon for the kind of file, shown where a thumbnail isn't (yet).</summary>
    public string Glyph => System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".ws" or ".rsblob" => "",
        ".xml" or ".json" or ".ini" or ".yml" or ".yaml" => "",
        ".txt" or ".csv" or ".log" or ".lua" or ".subs" or ".list" => "",
        ".w3strings" => "",
        ".xbm" or ".texarray" or ".w2cube" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".dds" or ".tif" or ".tiff" => "",
        ".w2mesh" or ".w2ent" or ".w2rig" or ".w2anims" => "",
        ".redswf" or ".swf" => "",
        ".bnk" or ".wem" or ".ogg" or ".wav" => "",
        _ => "",
    };

    /// <summary>Whether a picture of the file's contents can be made: a texture, a picture file, or an .xbm whose texture is in a cache.</summary>
    public bool CanHaveThumbnail => EntryImages.IsPicture(Entry) || IsTextureHeader;

    /// <summary>
    /// An .xbm, .texarray or .w2cube in a bundle: the texture's description, whose pixels are in a texture.cache under the
    /// same path.
    /// </summary>
    public bool IsTextureHeader => Entry is not Core.Textures.TextureEntry and not Core.Scripts.LooseScriptEntry
        && System.IO.Path.GetExtension(Name).ToLowerInvariant() is ".xbm" or ".texarray" or ".w2cube";

    private System.Windows.Media.ImageSource? _thumbnail;

    /// <summary>A small picture of the file, once it has been made.</summary>
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    private bool _isSelected;

    /// <summary>Highlighted in the thumbnail view, which keeps its own selection.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>Set once a thumbnail was asked for, so scrolling past a file again doesn't ask twice.</summary>
    public bool ThumbnailRequested { get; set; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            _checkedChanged(this, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}
