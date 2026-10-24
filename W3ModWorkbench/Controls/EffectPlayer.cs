using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Effects;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Particles;

namespace W3ModWorkbench;

/// <summary>
/// Plays an entity's effects in a 3D view: each effect switched on runs along its timeline (a looped one between its
/// loop points; one that isn't plays through and starts again after a pause), and each of its particle parts runs a
/// particle system where its spawner puts it on the entity while the part lasts. What the parts that aren't particles
/// do isn't drawn. Particle systems and their textures are read once, in the background; a system the game keeps
/// cooked plays from REDkit's copy when there is one, as the particle window does.
/// </summary>
public sealed class EffectPlayer
{
    /// <summary>How long a one-off effect waits before it plays again.</summary>
    private const float Pause = 1;

    /// <summary>At most so many particles are drawn, the rest left out, so a big effect doesn't stall the view.</summary>
    public const int MaxSprites = 20000;

    private sealed class PartRun(EffectPart part)
    {
        public EffectPart Part { get; } = part;
        // a simulation for each place the spawner puts the part (a simple spawner can name several bones)
        public List<ParticleSimulation> Simulations { get; } = [];
        public ParticleSystemFile? System { get; set; }
        public bool Active { get; set; }
    }

    private sealed class Run(EntityEffect effect)
    {
        public EntityEffect Effect { get; } = effect;
        public float Time { get; set; }
        public List<PartRun> Parts { get; } = [.. effect.ParticleParts.Select(p => new PartRun(p))];
    }

    private readonly ResourceLocator _locator;
    private readonly Func<EffectPart, IReadOnlyList<Matrix4x4>> _where;
    private readonly List<Run> _runs = [];
    private readonly Dictionary<string, Task<ParticleSystemFile?>> _systems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<(byte[] Pixels, int Width, int Height)?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<List<MeshChunk>?>> _meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="where">Where a part goes on the entity now, in the game's space: one place for each its spawner names.</param>
    public EffectPlayer(ResourceLocator locator, Func<EffectPart, IReadOnlyList<Matrix4x4>> where)
    {
        _locator = locator;
        _where = where;
    }

    /// <summary>The names of the effects playing.</summary>
    public IEnumerable<string> Playing => _runs.Select(r => r.Effect.Name);

    public bool Any => _runs.Count > 0;

    /// <summary>The particle systems that couldn't be played: not found, not read, or cooked with no REDkit copy.</summary>
    public IEnumerable<string> Missing => _runs.SelectMany(r => r.Parts).Where(p => p.System is null && _systems.TryGetValue(p.Part.ParticleSystem!, out var t) && t.IsCompleted)
        .Select(p => p.Part.ParticleSystem!).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The drawers of the playing effects' emitters that aren't drawn yet (meshes, trails…), by class.</summary>
    public IEnumerable<string> NotDrawn => _runs.SelectMany(r => r.Parts).Select(p => p.System).OfType<ParticleSystemFile>()
        .SelectMany(s => s.Emitters).Where(e => !e.DrawnAsSprites && !e.DrawnAsMeshes).Select(e => e.Drawer).Distinct();

    /// <summary>How many particles there are now.</summary>
    public int Count => _runs.Sum(r => r.Parts.Sum(p => p.Simulations.Sum(s => s.Count)));

    public bool IsPlaying(EntityEffect effect) => _runs.Any(r => r.Effect == effect);

    /// <summary>Starts an effect from its beginning, reading its particle systems if they haven't been yet.</summary>
    public void Start(EntityEffect effect)
    {
        if (IsPlaying(effect)) return;
        var run = new Run(effect);
        foreach (var part in run.Parts) _ = SystemAsync(part);
        _runs.Add(run);
    }

    public void Stop(EntityEffect effect) => _runs.RemoveAll(r => r.Effect == effect);

    public void StopAll() => _runs.Clear();

    /// <summary>Plays on by a time (seconds).</summary>
    public void Advance(float seconds)
    {
        foreach (var run in _runs)
        {
            var effect = run.Effect;
            float length = Math.Max(effect.Length, effect.Parts.Select(p => p.End).DefaultIfEmpty(0).Max());
            run.Time += seconds;
            if (effect.Looped)
            {
                float end = effect.LoopEnd > effect.LoopStart ? effect.LoopEnd : length;
                float start = effect.LoopEnd > effect.LoopStart ? effect.LoopStart : 0;
                if (end > start && run.Time > end) run.Time = start + (run.Time - end) % (end - start);
            }
            else if (run.Time > length + Pause)
            {
                // a one-off starts again once its particles have had their time
                run.Time = 0;
            }
            foreach (var part in run.Parts)
            {
                if (part.System is null) continue;
                bool active = run.Time >= part.Part.Start && run.Time <= part.Part.End + 1e-3f;
                var places = _where(part.Part);
                // a simulation for each place, made the first time the part plays
                while (part.Simulations.Count < places.Count)
                {
                    var simulation = new ParticleSimulation(part.System);
                    if (part.System.Prewarm > 0) simulation.Advance(Math.Min(part.System.Prewarm, 30));
                    part.Simulations.Add(simulation);
                }
                for (int i = 0; i < part.Simulations.Count; i++)
                {
                    var simulation = part.Simulations[i];
                    if (i < places.Count) simulation.Placement = places[i];
                    simulation.Emitting = active && i < places.Count;
                    simulation.Advance(seconds);
                }
                part.Active = active;
            }
        }
    }

    /// <summary>
    /// The particles there are now, in batches by texture, coloured and sized as the particle preview draws them: an
    /// emitter that keeps its simulation local is drawn where its part is now, the others where their particles are.
    /// </summary>
    public List<ParticleBatch> Batches()
    {
        var batches = new Dictionary<string, (ParticleBatch Batch, List<ParticleSprite> Sprites)>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var run in _runs)
            foreach (var part in run.Parts)
            {
                if (part.System is not { } system) continue;
                foreach (var simulation in part.Simulations)
                    for (int e = 0; e < system.Emitters.Count; e++)
                    {
                        var emitter = system.Emitters[e];
                        var particles = simulation.Particles(e);
                        if (particles.Count == 0 || !emitter.DrawnAsSprites) continue;
                        // a texture not read yet is drawn as the soft spot, under the spot's key, until it is
                        var picture = emitter.Texture is { } texture ? TextureNow(texture) : null;
                        string key = picture is null ? "" : emitter.Texture!;
                        if (!batches.TryGetValue(key, out var batch))
                        {
                            var sprites = new List<ParticleSprite>();
                            batch = (new ParticleBatch(key, () => picture, sprites), sprites);
                            batches[key] = batch;
                        }
                        var place = emitter.KeepLocal ? simulation.Placement : Matrix4x4.Identity;
                        var tint = emitter.Tint;
                        var (columns, rows) = emitter.Atlas;
                        columns = Math.Max(1, columns);
                        rows = Math.Max(1, rows);
                        int frames = columns * rows;
                        var scale = new Vector2(1f / columns, 1f / rows);
                        foreach (var p in particles)
                        {
                            if (total >= MaxSprites) break;
                            float alpha = Math.Clamp(p.Color.W, 0, 1) * tint.A / 255f;
                            if (alpha <= 0.002f) continue;
                            var rgb = new Vector3(p.Color.X * tint.R / 255f, p.Color.Y * tint.G / 255f, p.Color.Z * tint.B / 255f) * alpha;
                            int frame = ((int)MathF.Floor(p.Frame) % frames + frames) % frames;
                            batch.Sprites.Add(new ParticleSprite(
                                Vector3.Transform(p.Position, place),
                                new Vector2(MathF.Abs(p.Size.X), MathF.Abs(p.Size.Y)),
                                p.Rotation * MathF.Tau,
                                // premultiplied; an additive particle only adds, so it covers nothing
                                new Vector4(rgb, emitter.Additive ? 0 : alpha),
                                new Vector4(frame % columns * scale.X, frame / columns * scale.Y, scale.X, scale.Y)));
                            total++;
                        }
                    }
            }
        return [.. batches.Values.Select(b => b.Batch)];
    }

    /// <summary>
    /// The particles drawn as meshes there are now, in batches by mesh and texture: each particle takes one of its
    /// emitter's meshes, sized on each axis, turned by its 3D rotation (or the way it moves, or not at all, as the
    /// emitter says), coloured as a sprite is, its texture scrolled on as its material scrolls.
    /// </summary>
    public List<MeshParticleBatch> MeshBatches()
    {
        var batches = new Dictionary<string, (MeshParticleBatch Batch, List<MeshParticle> Particles)>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var run in _runs)
            foreach (var part in run.Parts)
            {
                if (part.System is not { } system) continue;
                foreach (var simulation in part.Simulations)
                    for (int e = 0; e < system.Emitters.Count; e++)
                    {
                        var emitter = system.Emitters[e];
                        var particles = simulation.Particles(e);
                        if (particles.Count == 0 || !emitter.DrawnAsMeshes) continue;
                        var picture = emitter.Texture is { } texture ? TextureNow(texture) : null;
                        var place = emitter.KeepLocal ? simulation.Placement : Matrix4x4.Identity;
                        var tint = emitter.Tint;
                        var (columns, rows) = emitter.Atlas;
                        columns = Math.Max(1, columns);
                        rows = Math.Max(1, rows);
                        int frames = columns * rows;
                        foreach (var p in particles)
                        {
                            if (total >= MaxSprites) break;
                            string meshPath = emitter.Meshes[Math.Clamp((int)(p.Random.X * emitter.Meshes.Count), 0, emitter.Meshes.Count - 1)];
                            if (MeshNow(meshPath) is not { Count: > 0 } chunks) continue;
                            float alpha = Math.Clamp(p.Color.W, 0, 1) * tint.A / 255f;
                            if (alpha <= 0.002f) continue;
                            string key = meshPath + "|" + (picture is null ? "" : emitter.Texture);
                            if (!batches.TryGetValue(key, out var batch))
                            {
                                var list = new List<MeshParticle>();
                                var made = picture;
                                batch = (new MeshParticleBatch(key, chunks, () => made, list), list);
                                batches[key] = batch;
                            }
                            var rgb = new Vector3(p.Color.X * tint.R / 255f, p.Color.Y * tint.G / 255f, p.Color.Z * tint.B / 255f) * alpha;
                            int frame = ((int)MathF.Floor(p.Frame) % frames + frames) % frames;
                            var scroll = emitter.ScrollSpeed * p.Age;
                            var size = new Vector3(MathF.Abs(p.Size3D.X), MathF.Abs(p.Size3D.Y), MathF.Abs(p.Size3D.Z));
                            batch.Particles.Add(new MeshParticle(
                                Matrix4x4.CreateScale(size) * MeshTurn(emitter.MeshOrientation, p) * Matrix4x4.CreateTranslation(p.Position) * place,
                                new Vector4(rgb, emitter.Additive ? 0 : alpha),
                                new Vector4(1f / columns, 1f / rows, frame % columns / (float)columns + scroll.X, frame / columns / (float)rows + scroll.Y)));
                            total++;
                        }
                    }
            }
        return [.. batches.Values.Select(b => b.Batch)];
    }

    /// <summary>
    /// How a mesh particle is turned: not at all, along the way it moves (the mesh's Y, its forward, towards its
    /// velocity), or by its 3D rotation, in turns about X, Y and Z.
    /// </summary>
    private static Matrix4x4 MeshTurn(string orientation, Particle p)
    {
        if (orientation == "MPOM_NoRotation") return Matrix4x4.Identity; // loc-ignore
        if (orientation == "MPOM_MovementDirection") // loc-ignore
        {
            float speed = p.Velocity.Length();
            if (speed < 1e-4f) return Matrix4x4.Identity;
            var to = p.Velocity / speed;
            float dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, to), -1, 1);
            var axis = Vector3.Cross(Vector3.UnitY, to);
            return axis.LengthSquared() < 1e-8f
                ? dot > 0 ? Matrix4x4.Identity : Matrix4x4.CreateRotationZ(MathF.PI)
                : Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
        }
        return Matrix4x4.CreateRotationX(p.Rotation3D.X * MathF.Tau) * Matrix4x4.CreateRotationY(p.Rotation3D.Y * MathF.Tau) * Matrix4x4.CreateRotationZ(p.Rotation3D.Z * MathF.Tau);
    }

    /// <summary>A mesh's top level of detail, read once in the background.</summary>
    private Task<List<MeshChunk>?> MeshAsync(string path)
    {
        lock (_meshes)
        {
            if (!_meshes.TryGetValue(path, out var task))
                _meshes[path] = task = Task.Run<List<MeshChunk>?>(() =>
                {
                    try
                    {
                        if (_locator.ReadFile(path) is not { } data) return null;
                        var archive = _locator.Find(path)?.Archive;
                        var mesh = MeshFile.Read(data, n => _locator.ReadBuffer(path, n, archive));
                        return [.. mesh.Chunks.Where(c => c.InLod(0))];
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
                    {
                        return null;
                    }
                });
            return task;
        }
    }

    private List<MeshChunk>? MeshNow(string path)
    {
        var task = MeshAsync(path);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>A particle system, read once: REDkit's copy when the game's emitters are cooked and there is one.</summary>
    private async Task SystemAsync(PartRun part)
    {
        string path = part.Part.ParticleSystem!;
        Task<ParticleSystemFile?> task;
        lock (_systems)
            if (!_systems.TryGetValue(path, out task!)) _systems[path] = task = Task.Run(() => ReadSystem(path));
        var system = await task;
        if (system is null) return;
        part.System = system;
        // the textures and meshes read ahead, so they're there by the time the particles are
        foreach (var texture in system.Emitters.Select(e => e.Texture).OfType<string>()) _ = TextureAsync(texture);
        foreach (var mesh in system.Emitters.Where(e => e.DrawnAsMeshes).SelectMany(e => e.Meshes)) _ = MeshAsync(mesh);
    }

    private ParticleSystemFile? ReadSystem(string path)
    {
        try
        {
            ParticleSystemFile? system = _locator.ReadFile(path) is { } data ? ParticleSystemFile.Read(data, ParticleWindow.Shaders(_locator)) : null;
            if ((system is null || system.Emitters.Any(e => e.Cooked)) && RedkitSource.Read(path) is { } redkit)
                system = ParticleSystemFile.Read(redkit.File.Data, ParticleWindow.Shaders(_locator));
            return system is null || system.Emitters.All(e => e.Cooked) ? null : system;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private Task<(byte[] Pixels, int Width, int Height)?> TextureAsync(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task))
                _textures[path] = task = Task.Run<(byte[] Pixels, int Width, int Height)?>(() =>
                {
                    if (MeshTextures.LoadWithAlpha(_locator, path, 512) is not { } picture) return null;
                    var (pixels, width, height) = ParticlePreview.Pixels(picture);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    return (bytes, width, height);
                });
            return task;
        }
    }

    /// <summary>A texture when it's been read, else null.</summary>
    private (byte[] Pixels, int Width, int Height)? TextureNow(string path)
    {
        var task = TextureAsync(path);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }
}
