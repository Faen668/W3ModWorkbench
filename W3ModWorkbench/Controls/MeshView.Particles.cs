using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Color4 = HelixToolkit.Maths.Color4;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

/// <summary>A particle to draw: where (the game's space), how big (metres), turned how far (radians), its colour and its atlas frame.</summary>
/// <param name="Color">Premultiplied by its alpha; an additive particle's alpha is 0.</param>
/// <param name="Frame">The texture's part it shows: the corner (X, Y) and size (Z, W), 0 to 1.</param>
public readonly record struct ParticleSprite(Vector3 At, Vector2 Size, float Turn, Vector4 Color, Vector4 Frame);

/// <summary>Particles drawn with one texture: its premultiplied BGRA pixels (null for a soft round spot) under a key it's kept by.</summary>
public sealed record ParticleBatch(string Key, Func<(byte[] Pixels, int Width, int Height)?> Texture, IReadOnlyList<ParticleSprite> Sprites);

/// <summary>A particle drawn as a mesh: where it is, turned and sized (the game's space), its colour, and its texture's frame.</summary>
/// <param name="Color">Premultiplied by its alpha; an additive particle's alpha is 0.</param>
/// <param name="Frame">The texture's part it shows: the size (X, Y) and corner (Z, W), the corner scrolled on as its material scrolls.</param>
public readonly record struct MeshParticle(Matrix4x4 Place, Vector4 Color, Vector4 Frame);

/// <summary>Particles drawn as one mesh (its chunks) with one texture, under a key it's kept by.</summary>
public sealed record MeshParticleBatch(string Key, IReadOnlyList<W3ModWorkbench.Core.Meshes.MeshChunk> Chunks,
    Func<(byte[] Pixels, int Width, int Height)?> Texture, IReadOnlyList<MeshParticle> Particles);

public sealed partial class MeshView
{
    // the effects' particles, drawn after the meshes (Helix's particle stage) with the view's particle pass
    private readonly GroupModel3D _particles = new();
    private readonly Dictionary<string, InstancingBillboardModel3D> _particleModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<InstancingMeshGeometryModel3D>> _meshParticleModels = new(StringComparer.OrdinalIgnoreCase);

    // the game's axes to the view's and back: (x, y, z) to (x, z, -y)
    private static readonly Matrix4x4 GameToView = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 ViewToGame = Matrix4x4.Transpose(GameToView);

    /// <summary>Whether particles can be drawn: the view's particle shader compiled.</summary>
    public static bool CanDrawParticles => W3Shading.ParticleProblem is null;

    /// <summary>
    /// Shows these particles in place of the last ones. A texture is made once for its key and kept; a key not in the
    /// batches this time is emptied. Null or no batches clears them all.
    /// </summary>
    public void ShowParticles(IReadOnlyList<ParticleBatch>? batches, IReadOnlyList<MeshParticleBatch>? meshes = null)
    {
        ShowMeshParticles(meshes);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches ?? [])
        {
            shown.Add(batch.Key);
            if (!_particleModels.TryGetValue(batch.Key, out var model))
            {
                var picture = batch.Texture() ?? Spot();
                var texture = new TextureModel(picture.Pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, picture.Width, picture.Height);
                model = new InstancingBillboardModel3D
                {
                    Geometry = new BillboardSingleImage3D(texture, 1, 1),
                    FixedSize = false,
                    IsHitTestVisible = false,
                    EnableViewFrustumCheck = false,
                    IsThrowingShadow = false,
                };
                _particles.Children.Add(model);
                // drawn after the meshes, so the meshes in front hide them and those behind show through
                model.SceneNode.RenderType = RenderType.Particle;
                _particleModels[batch.Key] = model;
            }
            var instances = new List<Matrix4x4>(batch.Sprites.Count);
            var parameters = new List<BillboardInstanceParameter>(batch.Sprites.Count);
            foreach (var p in batch.Sprites)
            {
                var at = ToView(p.At);
                // the particle pass reads the size and turn from the first row and where it is from the last
                instances.Add(new Matrix4x4(p.Size.X, p.Size.Y, p.Turn, 0, 0, 0, 0, 0, 0, 0, 0, 0, at.X, at.Y, at.Z, 1));
                parameters.Add(new BillboardInstanceParameter
                {
                    DiffuseColor = new HelixToolkit.Maths.Color4(p.Color.X, p.Color.Y, p.Color.Z, p.Color.W),
                    TexCoordOffset = new Vector2(p.Frame.X, p.Frame.Y),
                    TexCoordScale = new Vector2(p.Frame.Z, p.Frame.W),
                });
            }
            model.Instances = instances;
            model.InstanceParamArray = parameters;
            model.Visibility = instances.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        foreach (var (key, model) in _particleModels)
            if (!shown.Contains(key) && model.Visibility == System.Windows.Visibility.Visible)
            {
                model.Instances = [];
                model.InstanceParamArray = [];
                model.Visibility = System.Windows.Visibility.Collapsed;
            }
        _viewport.InvalidateRender();
    }

    /// <summary>
    /// Shows mesh particles in place of the last ones: a model for each chunk of each batch's mesh, made once for its
    /// key, its particles its instances. The mesh's chunks are already in the view's axes, so each particle's place is
    /// turned into them too.
    /// </summary>
    private void ShowMeshParticles(IReadOnlyList<MeshParticleBatch>? batches)
    {
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches ?? [])
        {
            shown.Add(batch.Key);
            if (!_meshParticleModels.TryGetValue(batch.Key, out var models))
            {
                var picture = batch.Texture() ?? Spot();
                var material = new PhongMaterial(new ParticleMeshMaterialCore())
                {
                    DiffuseColor = new Color4(1, 1, 1, 1),
                    DiffuseMap = new TextureModel(picture.Pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, picture.Width, picture.Height),
                };
                models = [.. batch.Chunks.Select(chunk => new InstancingMeshGeometryModel3D
                {
                    Geometry = Geometry(chunk),
                    Material = material,
                    IsHitTestVisible = false,
                    EnableViewFrustumCheck = false,
                    IsThrowingShadow = false,
                    CullMode = SharpDX.Direct3D11.CullMode.None,
                })];
                foreach (var model in models)
                {
                    _particles.Children.Add(model);
                    model.SceneNode.RenderType = RenderType.Particle;
                }
                _meshParticleModels[batch.Key] = models;
            }
            var instances = batch.Particles.Select(p => ViewToGame * p.Place * GameToView).ToList();
            var parameters = batch.Particles.Select(p => new InstanceParameter
            {
                DiffuseColor = new Color4(p.Color.X, p.Color.Y, p.Color.Z, p.Color.W),
                // the pass reads the frame from the emissive colour: its size, then its corner
                EmissiveColor = new Color4(p.Frame.X, p.Frame.Y, p.Frame.Z, p.Frame.W),
            }).ToList();
            foreach (var model in models)
            {
                model.Instances = instances;
                model.InstanceParamArray = parameters;
                model.Visibility = instances.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
        }
        foreach (var (key, models) in _meshParticleModels)
            if (!shown.Contains(key))
                foreach (var model in models.Where(m => m.Visibility == System.Windows.Visibility.Visible))
                {
                    model.Instances = [];
                    model.InstanceParamArray = [];
                    model.Visibility = System.Windows.Visibility.Collapsed;
                }
    }

    /// <summary>Lets go of every particle texture, for a view showing something else.</summary>
    public void ForgetParticles()
    {
        _particles.Children.Clear();
        _particleModels.Clear();
        _meshParticleModels.Clear();
        _viewport.InvalidateRender();
    }

    /// <summary>A soft round spot, premultiplied, for an emitter with no texture.</summary>
    private static (byte[] Pixels, int Width, int Height) Spot()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float fall = Math.Max(0, 1 - (dx * dx + dy * dy));
                byte a = (byte)(fall * fall * 255);
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = a;
            }
        return (pixels, size, size);
    }
}
