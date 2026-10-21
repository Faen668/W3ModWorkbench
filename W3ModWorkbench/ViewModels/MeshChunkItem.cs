using System.ComponentModel;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>A chunk of a mesh in the mesh viewer's list, with a tick for whether it's drawn.</summary>
public sealed class MeshChunkItem : INotifyPropertyChanged
{
    private readonly Action<MeshChunkItem> _changed;
    private bool _isVisible = true;

    public MeshChunkItem(MeshChunk chunk, Action<MeshChunkItem> changed)
    {
        Chunk = chunk;
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MeshChunk Chunk { get; }
    public string Title => L.T("MeshChunkItem.Chunk", Chunk.Index);
    public string Detail => L.T("MeshChunkItem.Detail", Chunk.Material, Chunk.Positions.Length, Chunk.Triangles) + (Chunk.Skinned ? L.T("MeshChunkItem.Skinned") : "");

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            _changed(this);
        }
    }
}
