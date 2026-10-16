using System.ComponentModel;
using W3BundleExplorer.Core;

namespace W3BundleExplorer.ViewModels;

public sealed class EntryItem : INotifyPropertyChanged
{
    private readonly Action<EntryItem, bool> _checkedChanged;
    private bool _isChecked;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ws", ".xml", ".csv", ".txt", ".json", ".ini", ".yml", ".yaml", ".log", ".lua",
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

    /// <summary>Text files open in the preview window; everything else is extract-only.</summary>
    public bool IsPreviewable => TextExtensions.Contains(System.IO.Path.GetExtension(Name));

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
