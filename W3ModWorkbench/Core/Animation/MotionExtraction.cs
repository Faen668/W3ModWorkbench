using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// How an animation moves the character as a whole (its motion extraction): where it has got to over the ground, and
/// how far it has turned, at each time, in its own space (forward is +Y). An animation that walks keeps its body in
/// place, its root walking back as far as its body walks on, and the game moves the character along this instead.
/// </summary>
public sealed class MotionExtraction
{
    private readonly float[] _times;
    private readonly (Vector3 Position, float Yaw)[] _keys;

    private MotionExtraction(float[] times, (Vector3, float)[] keys)
    {
        _times = times;
        _keys = keys;
    }

    /// <summary>
    /// Reads an animation's motion extraction, or null when it has none it can read. CLineMotionExtraction2 keeps keys
    /// a number of frames apart at 30 a second (its deltaTimes, a byte each), each with the channels its flags name: 1 X,
    /// 2 Y, 4 Z, 8 the turn round the upright in radians. CUncompressedMotionExtraction keeps a vector a frame, evenly
    /// over its length, the turn in W.
    /// </summary>
    public static MotionExtraction? Read(Cr2wFile file, RedObject animation)
    {
        if (animation["motionExtraction"] is not RedHandle handle || file.Object(handle) is not { } motion) return null;
        switch (motion.ClassName)
        {
            case "CLineMotionExtraction2":
            {
                int flags = motion["flags"] is { } f ? Convert.ToInt32(f) : 0;
                int channels = (flags & 1) + (flags >> 1 & 1) + (flags >> 2 & 1) + (flags >> 3 & 1);
                var values = motion.Array("frames").Select(Convert.ToSingle).ToArray();
                if (channels == 0 || values.Length < channels) return null;
                int count = values.Length / channels;
                var steps = motion["deltaTimes"] as byte[] ?? [];
                var times = new float[count];
                for (int k = 1; k < count; k++) times[k] = times[k - 1] + (k - 1 < steps.Length ? steps[k - 1] : 1) / 30f;
                var keys = new (Vector3, float)[count];
                for (int k = 0; k < count; k++)
                {
                    int at = k * channels;
                    float Next() => values[at++];
                    float x = (flags & 1) != 0 ? Next() : 0, y = (flags & 2) != 0 ? Next() : 0, z = (flags & 4) != 0 ? Next() : 0, yaw = (flags & 8) != 0 ? Next() : 0;
                    keys[k] = (new Vector3(x, y, z), yaw);
                }
                return new MotionExtraction(times, keys);
            }
            case "CUncompressedMotionExtraction":
            {
                var frames = motion.Array("frames").OfType<RedObject>().ToArray();
                if (frames.Length == 0) return null;
                float length = motion.Float("duration");
                var times = frames.Select((_, k) => frames.Length > 1 ? length * k / (frames.Length - 1) : 0).ToArray();
                var keys = frames.Select(v => (new Vector3(v.Float("X"), v.Float("Y"), v.Float("Z")), v.Float("W"))).ToArray();
                return new MotionExtraction(times, keys);
            }
            default:
                return null;
        }
    }

    /// <summary>Where the character has got to and how far it has turned at a time into the animation, between the keys.</summary>
    public (Vector3 Position, float Yaw) At(float time)
    {
        if (_keys.Length == 1 || time <= _times[0]) return _keys[0];
        if (time >= _times[^1]) return _keys[^1];
        int k = Array.FindLastIndex(_times, t => t <= time);
        float span = _times[k + 1] - _times[k], t = span > 0 ? (time - _times[k]) / span : 0;
        return (Vector3.Lerp(_keys[k].Position, _keys[k + 1].Position, t), _keys[k].Yaw + (_keys[k + 1].Yaw - _keys[k].Yaw) * t);
    }

    /// <summary>
    /// How the character has moved between two times into the animation, in its own space as it stood at the first: a
    /// turn round the upright and a move over the ground, as a matrix to put before where it stands.
    /// </summary>
    public Matrix4x4 Between(float from, float to)
    {
        var (a, aYaw) = At(from);
        var (b, bYaw) = At(to);
        var start = Matrix4x4.CreateRotationZ(aYaw) * Matrix4x4.CreateTranslation(a);
        var end = Matrix4x4.CreateRotationZ(bYaw) * Matrix4x4.CreateTranslation(b);
        return Matrix4x4.Invert(start, out var back) ? end * back : Matrix4x4.Identity;
    }
}
