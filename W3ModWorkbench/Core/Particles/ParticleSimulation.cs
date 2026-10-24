using System.Numerics;

namespace W3ModWorkbench.Core.Particles;

/// <summary>A particle as it is now: where, how big, turned how far (in turns), its colour and alpha (0 to 1) and atlas frame.</summary>
public struct Particle
{
    public Vector3 Position, Velocity;
    public float Age, Life, Rotation, RotationRate, Frame, StartFrame, FrameSpeed;
    public Vector2 BaseSize, Size;
    public Vector4 BaseColor, Color, Random;

    public readonly float LifeFraction => Life > 0 ? Math.Clamp(Age / Life, 0, 1) : 1;
}

/// <summary>
/// An effect played the way the engine plays its emitters, as closely as the editor's data says: each emitter births
/// particles at its rate (by LOD, over its run) and in its bursts, its initializers set each one up (life, where in its
/// spawn shape, velocity, size, rotation, colour, alpha), and its modificators change it as it lives (size, alpha,
/// colour and velocity over life, acceleration, turbulence, rotation, atlas frames). What it can't know (collisions,
/// targets in the world, the scene's wind) is left out. Particles are in the effect's space, metres, Z up.
/// </summary>
public sealed class ParticleSimulation
{
    private sealed class EmitterState
    {
        public readonly List<Particle> Live = [];
        public float Owed, Time;
    }

    private readonly ParticleSystemFile _system;
    private readonly EmitterState[] _states;
    private readonly Random _random = new(1234);

    public ParticleSimulation(ParticleSystemFile system)
    {
        _system = system;
        _states = system.Emitters.Select(_ => new EmitterState()).ToArray();
    }

    /// <summary>The level of detail played: 0 is the closest.</summary>
    public int Lod { get; set; }

    /// <summary>Whether the emitters birth particles; off, those alive live on and die out (an effect's part that has ended).</summary>
    public bool Emitting { get; set; } = true;

    /// <summary>
    /// Where the effect is in the world (an entity's bone or slot, say). An emitter that doesn't keep its simulation local
    /// leaves its particles where they were born, so they're placed in the world as they're born, and trail behind a moving
    /// one; the rest stay in the effect's space, to be drawn where the effect is now.
    /// </summary>
    public Matrix4x4 Placement { get; set; } = Matrix4x4.Identity;

    /// <summary>How long the effect has played, in seconds.</summary>
    public float Time { get; private set; }

    /// <summary>The live particles of an emitter.</summary>
    public IReadOnlyList<Particle> Particles(int emitter) => _states[emitter].Live;

    public int Count => _states.Sum(s => s.Live.Count);

    public void Reset()
    {
        foreach (var s in _states)
        {
            s.Live.Clear();
            s.Owed = 0;
            s.Time = 0;
        }
        Time = 0;
    }

    /// <summary>Plays on by a time, in steps of at most a thirtieth of a second.</summary>
    public void Advance(float seconds)
    {
        while (seconds > 1e-5f)
        {
            float dt = Math.Min(seconds, 1 / 30f);
            Step(dt);
            seconds -= dt;
        }
    }

    private void Step(float dt)
    {
        Time += dt;
        for (int i = 0; i < _states.Length; i++)
        {
            var emitter = _system.Emitters[i];
            var state = _states[i];
            if (!emitter.Showing || emitter.Cooked)
            {
                state.Live.Clear();
                continue;
            }
            Live(emitter, state.Live, dt);
            float before = state.Time - emitter.Delay;
            state.Time += dt;
            float now = state.Time - emitter.Delay;
            if (now < 0 || !Emitting) continue;
            float duration = emitter.Duration > 0 ? emitter.Duration : 1;
            // the runs it makes: none after its last, when it has a number of them
            if (emitter.Loops > 0 && emitter.Duration > 0 && now >= emitter.Duration * emitter.Loops) continue;
            float run = now % duration / duration;
            var rate = Lod < emitter.BirthRates.Count ? emitter.BirthRates[Lod] : emitter.BirthRates.FirstOrDefault();
            if (Lod < emitter.LodsOn.Count && !emitter.LodsOn[Lod]) continue;
            if (rate is not null) state.Owed += Math.Max(0, rate.Float(run, (float)_random.NextDouble())) * dt;
            int born = (int)state.Owed;
            state.Owed -= born;
            // its bursts, when their time comes round in this step
            var bursts = Lod < emitter.Bursts.Count ? emitter.Bursts[Lod] : emitter.Bursts.FirstOrDefault() ?? [];
            foreach (var burst in bursts)
            {
                float a = Math.Max(before, 0), b = now;
                if (burst.Repeat > 0)
                {
                    int k = (int)Math.Floor((b - burst.Time) / burst.Repeat);
                    if (k >= 0 && burst.Time + k * burst.Repeat >= a && burst.Time + k * burst.Repeat < b) born += burst.Count;
                }
                else
                {
                    // once a run: when the run's clock passes its time
                    float at = burst.Time;
                    float runA = emitter.Duration > 0 ? a % emitter.Duration : a, runB = emitter.Duration > 0 ? b % emitter.Duration : b;
                    if (runB < runA ? at >= runA || at < runB : at >= runA && at < runB) born += burst.Count;
                }
            }
            for (int n = 0; n < born && state.Live.Count < Math.Max(1, emitter.MaxParticles); n++)
            {
                var particle = Spawn(emitter, run);
                if (!emitter.KeepLocal && !Placement.IsIdentity)
                {
                    particle.Position = Vector3.Transform(particle.Position, Placement);
                    particle.Velocity = Vector3.TransformNormal(particle.Velocity, Placement);
                }
                state.Live.Add(particle);
            }
        }
    }

    private Vector4 Random4() => new((float)_random.NextDouble(), (float)_random.NextDouble(), (float)_random.NextDouble(), (float)_random.NextDouble());

    /// <summary>A particle born, as the emitter's initializers set it up, at a point of its run (0 to 1).</summary>
    private Particle Spawn(ParticleEmitter e, float run)
    {
        var r = Random4();
        var p = new Particle { Random = r, Life = 1, BaseSize = new Vector2(0.1f), BaseColor = Vector4.One };
        foreach (var m in e.Modules.Where(m => m.Enabled && m.IsInitializer))
        {
            var mr = Random4();
            switch (m.ClassName)
            {
                case "CParticleInitializerLifeTime":
                    p.Life = Math.Max(0.01f, m["lifeTime"]?.Float(run, mr.X) ?? 1);
                    break;
                case "CParticleInitializerPosition":
                    var at = m["position"]?.Value(run, mr) ?? Vector4.Zero;
                    var offset = m["offset"]?.Value(run, Random4()) ?? Vector4.Zero;
                    p.Position += new Vector3(at.X, at.Y, at.Z) + new Vector3(offset.X, offset.Y, offset.Z);
                    break;
                case "CParticleInitializerSpawnBox":
                    var extents = m["extents"]?.Value(run, mr) ?? Vector4.Zero;
                    var u = Random4();
                    p.Position += new Vector3((u.X * 2 - 1) * extents.X, (u.Y * 2 - 1) * extents.Y, (u.Z * 2 - 1) * extents.Z);
                    break;
                case "CParticleInitializerSpawnSphere":
                {
                    float inner = m["innerRadius"]?.Float(run, mr.X) ?? 0, outer = m["outerRadius"]?.Float(run, mr.Y) ?? 1;
                    var direction = RandomDirection(m.Flag("spawnPositiveX", true), m.Flag("spawnNegativeX", true), m.Flag("spawnPositiveY", true),
                        m.Flag("spawnNegativeY", true), m.Flag("spawnPositiveZ", true), m.Flag("spawnNegativeZ", true));
                    float radius = m.Flag("surfaceOnly") ? outer : inner + (outer - inner) * MathF.Cbrt((float)_random.NextDouble());
                    p.Position += direction * radius;
                    if (m.Flag("velocity")) p.Velocity += direction * (m["forceScale"]?.Float(run, mr.Z) ?? 1);
                    break;
                }
                case "CParticleInitializerSpawnCircle":
                {
                    float inner = m["innerRadius"]?.Float(run, mr.X) ?? 0, outer = m["outerRadius"]?.Float(run, mr.Y) ?? 1;
                    float angle = (float)_random.NextDouble() * MathF.Tau;
                    float radius = m.Flag("surfaceOnly") ? outer : inner + (outer - inner) * MathF.Sqrt((float)_random.NextDouble());
                    p.Position += new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * radius;
                    break;
                }
                case "CParticleInitializerVelocity":
                    var v = m["velocity"]?.Value(run, mr) ?? Vector4.Zero;
                    p.Velocity += new Vector3(v.X, v.Y, v.Z);
                    break;
                case "CParticleInitializerVelocitySpread":
                    float spread = m["scale"]?.Float(run, mr.X) ?? 0;
                    p.Velocity += RandomDirection(true, true, true, true, true, true) * spread * Math.Max(p.Velocity.Length(), 1);
                    break;
                case "CParticleInitializerSize":
                    var size = m["size"]?.Value(run, mr) ?? new Vector4(0.1f);
                    p.BaseSize = new Vector2(size.X, size.Y);
                    break;
                case "CParticleInitializerRotation":
                    p.Rotation = m["rotation"]?.Float(run, mr.X) ?? 0;
                    break;
                case "CParticleInitializerRotationRate":
                    p.RotationRate = m["rotationRate"]?.Float(run, mr.X) ?? 0;
                    break;
                case "CParticleInitializerColor":
                    var c = m["color"]?.Value(run, mr) ?? Vector4.One;
                    p.BaseColor = new Vector4(c.X, c.Y, c.Z, p.BaseColor.W);
                    break;
                case "CParticleInitializerAlpha":
                    p.BaseColor.W = m["alpha"]?.Float(run, mr.X) ?? 1;
                    break;
            }
        }
        if (e.Module("CParticleModificatorTextureAnimation") is { } animation)
        {
            p.StartFrame = animation["initialFrame"]?.Float(run, (float)_random.NextDouble()) ?? 0;
            p.FrameSpeed = animation["animationSpeed"]?.Float(run, (float)_random.NextDouble()) ?? 0;
        }
        p.Size = p.BaseSize;
        p.Color = p.BaseColor;
        p.Frame = p.StartFrame;
        return p;
    }

    private Vector3 RandomDirection(bool px, bool nx, bool py, bool ny, bool pz, bool nz)
    {
        for (int tries = 0; tries < 32; tries++)
        {
            var d = new Vector3((float)_random.NextDouble() * 2 - 1, (float)_random.NextDouble() * 2 - 1, (float)_random.NextDouble() * 2 - 1);
            float length = d.Length();
            if (length < 1e-3f || length > 1) continue;
            d /= length;
            if (!px && d.X > 0 || !nx && d.X < 0) d.X = -d.X;
            if (!py && d.Y > 0 || !ny && d.Y < 0) d.Y = -d.Y;
            if (!pz && d.Z > 0 || !nz && d.Z < 0) d.Z = -d.Z;
            return d;
        }
        return Vector3.UnitZ;
    }

    /// <summary>Moves the emitter's particles on by a step, as its modificators change them, and lets the dead go.</summary>
    private void Live(ParticleEmitter e, List<Particle> live, float dt)
    {
        var modificators = e.Modules.Where(m => m.Enabled && !m.IsInitializer).ToList();
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var p = live[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                live.RemoveAt(i);
                continue;
            }
            float t = p.LifeFraction;
            var size = p.BaseSize;
            var color = p.BaseColor;
            var velocityScale = Vector3.One;
            foreach (var m in modificators)
                switch (m.ClassName)
                {
                    case "CParticleModificatorSizeOverLife":
                    case "CParticleModificatorSize3DOverLife":
                        var s = m["size"]?.Value(t, p.Random) ?? Vector4.One;
                        size *= new Vector2(s.X, s.Y);
                        break;
                    case "CParticleModificatorAlphaOverLife":
                        float alpha = m["alpha"]?.Float(t, p.Random.X) ?? 1;
                        color.W = m.Flag("modulate", true) ? color.W * alpha : alpha;
                        break;
                    case "CParticleModificatorColorOverLife":
                        var c = m["color"]?.Value(t, p.Random) ?? Vector4.One;
                        color = m.Flag("modulate", true) ? new Vector4(color.X * c.X, color.Y * c.Y, color.Z * c.Z, color.W) : new Vector4(c.X, c.Y, c.Z, color.W);
                        break;
                    case "CParticleModificatorAcceleration":
                        var direction = m["direction"]?.Value(t, p.Random) ?? Vector4.Zero;
                        float scale = m["scale"]?.Float(t, p.Random.Y) ?? 1;
                        p.Velocity += new Vector3(direction.X, direction.Y, direction.Z) * scale * dt;
                        break;
                    case "CParticleModificatorVelocityOverLife":
                        var v = m["velocity"]?.Value(t, p.Random) ?? Vector4.One;
                        velocityScale *= new Vector3(v.X, v.Y, v.Z);
                        break;
                    case "CParticleModificatorVelocityTurbulize":
                        var turbulence = m["scale"]?.Value(t, p.Random) ?? Vector4.Zero;
                        float limit = m["timelifeLimit"]?.Float(t, p.Random.Z) ?? 1;
                        if (t <= Math.Max(limit, 0.0001f))
                            p.Velocity += new Vector3(Noise(p.Random.X, p.Age), Noise(p.Random.Y, p.Age), Noise(p.Random.Z, p.Age)) * new Vector3(turbulence.X, turbulence.Y, turbulence.Z) * dt;
                        break;
                    case "CParticleModificatorRotationOverLife":
                        p.Rotation = m["rotation"]?.Float(t, p.Random.W) ?? p.Rotation;
                        break;
                    case "CParticleModificatorRotationRateOverLife":
                        p.RotationRate = m["rotationRate"]?.Float(t, p.Random.W) ?? p.RotationRate;
                        break;
                }
            p.Position += p.Velocity * velocityScale * dt;
            p.Rotation += p.RotationRate * dt;
            p.Size = size;
            p.Color = color;
            p.Frame = p.StartFrame + p.FrameSpeed * p.Age;
            live[i] = p;
        }
    }

    /// <summary>A smooth wobble between -1 and 1, its own for each particle.</summary>
    private static float Noise(float seed, float time) => MathF.Sin(time * 3.1f + seed * 40) * 0.6f + MathF.Sin(time * 7.3f + seed * 91) * 0.4f;
}
