using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>A texture that can be put on a material's chunks: one of the material's own, or any other picked.</summary>
public sealed record TextureChoice(string Label, string? Path, bool Picked = false);

/// <summary>
/// One of a mesh's materials in the mesh viewer: where it's from, its parameters and textures, and the texture its
/// chunks are drawn with. Its parameters can be changed, and another material (.w2mi) can be put in its place; both
/// are saved into the open project, not into the files they came from.
/// </summary>
public sealed class MeshMaterialItem : INotifyPropertyChanged
{
    private readonly Action<MeshMaterialItem> _changed;
    private TextureChoice? _selected;
    private string _status = "";
    private bool _suppress;

    public MeshMaterialItem(int index, MeshMaterial own, Action<MeshMaterialItem> changed)
    {
        Index = index;
        Own = own;
        Current = own;
        _changed = changed;
        Fill(own, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    /// <summary>The mesh's own material, as it was opened or last saved.</summary>
    public MeshMaterial Own { get; private set; }

    /// <summary>The material its chunks are drawn with: its own, changed or not, or one picked in its place.</summary>
    public MeshMaterial Current { get; private set; }

    /// <summary>Its parameters were changed since it was opened or saved.</summary>
    public bool IsEdited { get; private set; }

    /// <summary>Another material file was put in place of the mesh's own.</summary>
    public bool IsSwapped => !Current.Source.Equals(Own.Source, StringComparison.OrdinalIgnoreCase);

    public bool NeedsSave => IsEdited || IsSwapped;

    public string Title => L.T("MeshMaterialItem.Title", Index) + (Current.Shader.Length > 0 ? $"  ·  {Current.Shader}" : "") + (NeedsSave ? L.T("MeshMaterialItem.Changed") : "");
    public string Source => Current.Source == MeshMaterial.InMesh
        ? Current.Bases.FirstOrDefault() is { } b ? L.T("MeshMaterialItem.InMeshBasedOn", System.IO.Path.GetFileName(b)) : L.T("MeshMaterialItem.InMesh")
        : Current.Source;
    /// <summary>The material file to open in the material viewer: its own .w2mi, or for one kept in the mesh, what it's based on.</summary>
    public string? MaterialLink => Current.IsFile || Current.IsGraph ? Current.Source : Current.Bases.FirstOrDefault();
    public string MaterialLinkText => Current.IsFile || Current.IsGraph ? L.T("MeshMaterialItem.OpenMaterial") : MaterialLink is { } b ? L.T("MeshMaterialItem.OpenBase", System.IO.Path.GetFileName(b)) : "";
    public Visibility MaterialLinkVisibility => MaterialLink is null ? Visibility.Collapsed : Visibility.Visible;

    public string SourceTip => string.Join("\n", new[] { Current.Source }.Concat(Current.Bases.Select(b => L.T("MeshMaterialItem.Based", b))));
    public ObservableCollection<TextureChoice> Options { get; } = [];
    public ObservableCollection<MaterialParameterItem> Parameters { get; } = [];
    public string ParametersHeader => L.T("MeshMaterialItem.ParametersOwn", Parameters.Count, Current.Own.Count);
    public List<MaterialTexture> Textures => Current.Textures;
    public Visibility ResetVisibility => !NeedsSave && !Options.Any(o => o.Picked) ? Visibility.Collapsed : Visibility.Visible;

    public TextureChoice? Selected
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value) || value is null) return;
            _selected = value;
            Raise(nameof(Selected));
            if (!_suppress) _changed(this);
        }
    }

    /// <summary>What happened with the texture: found, not found, being read.</summary>
    public string Status
    {
        get => _status;
        set { _status = value; Raise(nameof(Status)); }
    }

    /// <summary>Draws the chunks with another material: its colour texture, with its other textures to pick from.</summary>
    public void Use(MeshMaterial material)
    {
        Current = material;
        IsEdited = false;
        Fill(material, null);
        _changed(this);
    }

    /// <summary>Draws the chunks with a texture picked from anywhere, keeping the material's own in the list.</summary>
    public void UseTexture(string path)
    {
        Fill(Current, path);
        _changed(this);
    }

    /// <summary>Gives a parameter a value of the material's own, in place of what it had or took from its base.</summary>
    public void SetParameter(MaterialParameter parameter, object value)
    {
        var own = Current.Own.ToList();
        int at = own.FindIndex(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
        var changed = parameter with { Value = value, From = Current.Source == MeshMaterial.InMesh ? L.T("MeshMaterialItem.Mesh") : System.IO.Path.GetFileName(Current.Source) };
        if (at >= 0) own[at] = changed; else own.Add(changed);
        Edit(own);
    }

    /// <summary>Takes away the material's own value for a parameter, so it takes its base's again.</summary>
    public void ResetParameter(MaterialParameter parameter) =>
        Edit([.. Current.Own.Where(p => !p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))]);

    private void Edit(List<MaterialParameter> own)
    {
        Current = Current.WithOwn(own);
        IsEdited = true;
        Fill(Current, Options.FirstOrDefault(o => o.Picked) is { } picked && ReferenceEquals(picked, _selected) ? picked.Path : null);
        _changed(this);
    }

    /// <summary>After saving: what's shown is now the mesh's own.</summary>
    public void Saved(MeshMaterial saved)
    {
        Own = saved;
        Current = saved;
        IsEdited = false;
        Fill(saved, null);
    }

    private void Fill(MeshMaterial material, string? picked)
    {
        _suppress = true;
        Options.Clear();
        foreach (var texture in material.Textures.Where(t => t.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)))
            Options.Add(new TextureChoice($"{texture.Parameter}: {System.IO.Path.GetFileName(texture.Path)}", texture.Path));
        if (picked is not null) Options.Insert(0, new TextureChoice(L.T("MeshMaterialItem.Picked", System.IO.Path.GetFileName(picked)), picked, Picked: true));
        Options.Add(new TextureChoice(L.T("MeshMaterialItem.NonePlainGrey"), null));
        var diffuse = material.Diffuse;
        _selected = picked is not null ? Options[0]
            : diffuse is not null ? Options.First(o => o.Path == diffuse.Path) : Options[^1];

        Parameters.Clear();
        foreach (var parameter in material.Parameters)
        {
            bool own = material.Own.Any(o => o.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            bool inBase = material.Inherited.Any(i => i.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            Parameters.Add(new MaterialParameterItem(parameter, own, inBase, (item, value) => SetParameter(item.Parameter, value)));
        }
        _suppress = false;
        foreach (string name in new[] { nameof(Selected), nameof(Title), nameof(Source), nameof(SourceTip), nameof(MaterialLink), nameof(MaterialLinkText), nameof(MaterialLinkVisibility), nameof(Textures), nameof(ResetVisibility), nameof(ParametersHeader) })
            Raise(name);
    }

    /// <summary>Back to the mesh's own material and its colour texture, as it was opened or last saved.</summary>
    public void Reset()
    {
        Current = Own;
        IsEdited = false;
        Fill(Own, null);
        _changed(this);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
