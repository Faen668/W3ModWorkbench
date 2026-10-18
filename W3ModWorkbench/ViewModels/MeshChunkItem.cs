using System.ComponentModel;
using W3ModWorkbench.Core.Meshes;

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
    public string Title => $"Chunk {Chunk.Index}";
    public string Detail => $"  material {Chunk.Material}  ·  {Chunk.Positions.Length:N0} vertices, {Chunk.Triangles:N0} triangles" + (Chunk.Skinned ? "  ·  skinned" : "");

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
