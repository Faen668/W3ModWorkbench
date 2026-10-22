using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Curves;

/// <summary>How a curve goes from one key to the next, as the engine's ECurveSegmentType names them.</summary>
public enum CurveSegment
{
    Constant = 0,
    Interpolate = 1,
    Bezier = 2,
    BezierSmooth = 3,
    BezierSmoothLyzwiaz = 4,
    BezierSmoothSymmetric = 5,
    Bezier2D = 6,
}

/// <summary>What a curve's values are: one number, a vector, or a colour (0 to 255 a channel), perhaps with a scale.</summary>
public enum CurveKind
{
    Float,
    Vector,
    Color,
    ColorScaled,
}

/// <summary>
/// A key of a curve: its time, its value (a number in X, or a vector or colour), the handles either side of it (each a
/// step in time and value, for a Bezier segment) and how the segments into and out of it are drawn.
/// </summary>
public readonly record struct CurveKey(float Time, Vector4 Value, Vector2 In, Vector2 Out, CurveSegment Left, CurveSegment Right);

/// <summary>
/// A curve of the engine's: keys over a span of time (a day, from 0 to 1, for an environment; a particle's life for an
/// effect). Read from an environment's SSimpleCurve (both its layouts) or a particle module's CCurve; evaluated as the
/// game does, as closely as its data says: held, straight or Bezier between keys, and round the end back to the start
/// for a looping one.
/// </summary>
public sealed class RedCurve
{
    public required CurveKind Kind { get; init; }
    public required IReadOnlyList<CurveKey> Keys { get; init; }
    /// <summary>Whether the curve runs round from its last key to its first (a day does).</summary>
    public bool Loops { get; init; }
    /// <summary>How the whole curve is drawn when its keys don't say: CT_Linear, CT_Smooth or CT_Segmented.</summary>
    public string? BaseType { get; init; }
    /// <summary>Where REDkit's curve editor centres a number curve, used for a curve with no keys.</summary>
    public float EditOrigin { get; init; }

    public bool IsColor => Kind is CurveKind.Color or CurveKind.ColorScaled;

    /// <summary>
    /// An environment's curve (SSimpleCurve), or null when the struct isn't one. Newer files keep a list of keys
    /// (dataCurveValues); older ones keep parallel lists of times, control points, values and segment types.
    /// </summary>
    public static RedCurve? FromSimpleCurve(RedObject curve)
    {
        if (curve.ClassName != "SSimpleCurve") return null;
        var kind = curve["CurveType"]?.ToString() switch
        {
            "SCT_Vector" => CurveKind.Vector,
            "SCT_Color" => CurveKind.Color,
            "SCT_ColorScaled" => CurveKind.ColorScaled,
            _ => CurveKind.Float,
        };
        var keys = new List<CurveKey>();
        if (curve.Array("dataCurveValues") is { Count: > 0 } entries)
            foreach (var entry in entries.OfType<RedObject>())
                // the engine keeps these names clipped of their first letters (me for time, ntrolPoint for controlPoint)
                keys.Add(Key(kind, entry.Float("me", entry.Float("time")), entry.Struct("ntrolPoint") ?? entry.Struct("controlPoint"),
                    entry.Float("lue", entry.Float("value")), entry["rveTypeL"] ?? entry["curveTypeL"], entry["rveTypeR"] ?? entry["curveTypeR"]));
        else
        {
            var times = curve.Array("dataTimes");
            var points = curve.Array("dataControlPoints");
            var values = curve.Array("dataValues");
            var left = curve.Array("dataCurveType0");
            var right = curve.Array("dataCurveType1");
            for (int i = 0; i < times.Count; i++)
                keys.Add(Key(kind, Convert.ToSingle(times[i] ?? 0f), i < points.Count ? points[i] as RedObject : null,
                    i < values.Count ? Convert.ToSingle(values[i] ?? 0f) : 0, i < left.Count ? left[i] : null, i < right.Count ? right[i] : null));
        }
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new RedCurve
        {
            Kind = kind,
            Keys = keys,
            Loops = true,
            BaseType = curve["dataBaseType"]?.ToString(),
            EditOrigin = curve.Float("ScalarEditOrigin"),
        };
    }

    private static CurveKey Key(CurveKind kind, float time, RedObject? point, float value, object? left, object? right)
    {
        var p = point is null ? Vector4.Zero : new Vector4(point.Float("X"), point.Float("Y"), point.Float("Z"), point.Float("W"));
        // a number curve keeps its handles in the control point: in (X, Y), out (Z, W); a colour or vector keeps its value there
        return kind == CurveKind.Float
            ? new CurveKey(time, new Vector4(value, 0, 0, 0), new Vector2(p.X, p.Y), new Vector2(p.Z, p.W), Segment(left), Segment(right))
            : new CurveKey(time, p, Vector2.Zero, Vector2.Zero, Segment(left), Segment(right));
    }

    private static CurveSegment Segment(object? value) => value is null ? CurveSegment.Constant : (CurveSegment)Math.Clamp(Convert.ToInt32(value), 0, 6);

    /// <summary>
    /// A particle module's curve (CCurve): its keys after its properties, 48 bytes each: time, value, the in and out
    /// handles (each a Vector, of which X is the step in time and Y in value) and the segment types either side.
    /// </summary>
    public static RedCurve? FromCurveTail(byte[] tail)
    {
        if (tail.Length < 4) return null;
        int count = BitConverter.ToInt32(tail, 0);
        if (count < 0 || tail.Length < 4 + 48L * count) return null;
        var keys = new List<CurveKey>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 4 + 48 * i;
            float F(int offset) => BitConverter.ToSingle(tail, at + offset);
            keys.Add(new CurveKey(F(0), new Vector4(F(4), 0, 0, 0), new Vector2(F(8), F(12)), new Vector2(F(24), F(28)),
                Segment(BitConverter.ToInt32(tail, at + 40)), Segment(BitConverter.ToInt32(tail, at + 44))));
        }
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new RedCurve { Kind = CurveKind.Float, Keys = keys };
    }

    /// <summary>The curve's value at a time: for a number curve in X; for a colour its channels 0 to 255 and, scaled, the scale in W.</summary>
    public Vector4 Evaluate(float time)
    {
        if (Keys.Count == 0) return new Vector4(Kind == CurveKind.Float ? EditOrigin : 0, 0, 0, 0);
        if (Keys.Count == 1) return Keys[0].Value;
        // before the first key and after the last: round from the last to the first for a loop, else the end's value
        if (time < Keys[0].Time || time > Keys[^1].Time)
        {
            if (!Loops) return time < Keys[0].Time ? Keys[0].Value : Keys[^1].Value;
            var (last, first) = (Keys[^1], Keys[0]);
            float span = first.Time + 1 - last.Time;
            float into = time > last.Time ? time - last.Time : time + 1 - last.Time;
            return Between(last, first with { Time = last.Time + span }, last.Time + into);
        }
        int i = 0;
        while (i < Keys.Count - 2 && time > Keys[i + 1].Time) i++;
        return Between(Keys[i], Keys[i + 1], time);
    }

    /// <summary>A number curve's value at a time.</summary>
    public float At(float time) => Evaluate(time).X;

    private Vector4 Between(CurveKey a, CurveKey b, float time)
    {
        float span = b.Time - a.Time;
        float t = span <= 1e-6f ? 0 : Math.Clamp((time - a.Time) / span, 0, 1);
        var segment = Segmenting(a, b);
        if (segment == CurveSegment.Constant) return t >= 1 ? b.Value : a.Value;
        if (IsColor || Kind == CurveKind.Vector || segment == CurveSegment.Interpolate) return Vector4.Lerp(a.Value, b.Value, t);
        if (segment == CurveSegment.Bezier || segment == CurveSegment.Bezier2D)
        {
            // the handles as the keys keep them: steps in value from each key
            float p0 = a.Value.X, p1 = a.Value.X + a.Out.Y, p2 = b.Value.X + b.In.Y, p3 = b.Value.X;
            float u = 1 - t;
            return new Vector4(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3, 0, 0, 0);
        }
        // smooth: tangents through the keys either side, as a Catmull-Rom spline
        int at = IndexOf(a);
        float before = at > 0 ? Keys[at - 1].Value.X : a.Value.X, after = at + 2 < Keys.Count ? Keys[at + 2].Value.X : b.Value.X;
        float m0 = (b.Value.X - before) / 2, m1 = (after - a.Value.X) / 2;
        float t2 = t * t, t3 = t2 * t;
        return new Vector4((2 * t3 - 3 * t2 + 1) * a.Value.X + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.Value.X + (t3 - t2) * m1, 0, 0, 0);
    }

    private int IndexOf(CurveKey key)
    {
        for (int i = 0; i < Keys.Count; i++) if (Keys[i].Time == key.Time) return i;
        return 0;
    }

    /// <summary>How the segment between two keys is drawn: the first's right side, else the curve's own way.</summary>
    private CurveSegment Segmenting(CurveKey a, CurveKey b)
    {
        if (Kind == CurveKind.Float && (a.Right != CurveSegment.Constant || b.Left != CurveSegment.Constant))
            return a.Right != CurveSegment.Constant ? a.Right : b.Left;
        return BaseType switch
        {
            "CT_Segmented" => CurveSegment.Constant,
            "CT_Linear" => CurveSegment.Interpolate,
            _ => IsColor ? CurveSegment.Interpolate : CurveSegment.BezierSmooth,
        };
    }

    /// <summary>The lowest and highest a number curve reaches, sampled over its span.</summary>
    public (float Min, float Max) Range(float from = 0, float to = 1, int samples = 200)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i <= samples; i++)
        {
            float v = At(from + (to - from) * i / samples);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        foreach (var key in Keys)
        {
            min = Math.Min(min, key.Value.X);
            max = Math.Max(max, key.Value.X);
        }
        return (min, max);
    }
}
