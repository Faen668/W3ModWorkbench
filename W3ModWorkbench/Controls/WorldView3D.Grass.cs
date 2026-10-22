using System.Numerics;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench;

/// <summary>
/// A world's grass carpet as the engine grows it: each terrain texture's vegetation brush (the clip map's grassBrushes,
/// a .vbrush per texture slot) lists SpeedTree plants with a size, a size variation and a radius scale; the plants grow
/// where the ground's texture names that brush and the grass mask (the foliage scene's .grassmask, a bit a cell) lets
/// grass grow. <see cref="Entries"/> name their plant kinds by path, among the kinds handed to the view.
/// </summary>
public sealed record GrassSetup(IReadOnlyList<GrassEntry> Entries, bool[] Mask, int MaskResolution);

/// <summary>One plant of a terrain texture's brush: the texture slot, the plant kind (its place in the kinds), and its size settings.</summary>
public readonly record struct GrassEntry(int Slot, string Path, float Size, float SizeVariation, float RadiusScale, float Footprint);

// The terrain's grass, grown round the camera in the plant cells as it moves. The engine's own placement is in its C++
// (not in REDkit's source): here each brush plant is laid on a jittered grid whose spacing is the plant's footprint
// set by its radius scale, kept where the ground's background texture at that point is
// the brush's (by the four control samples' weights) and the grass mask allows it.
public sealed partial class WorldView3D
{
    private GrassSetup? _grass;
    private readonly Dictionary<string, int> _grassKinds = new(StringComparer.OrdinalIgnoreCase);
    private ushort[] _control = [];
    private readonly HashSet<(int, int)> _grassGrown = [];

    /// <summary>How far round the camera the terrain's grass is grown, in metres.</summary>
    public float GrassReach { get; set; } = 160;

    /// <summary>Metres between plants for each unit of a brush entry's radius scale (set against REDkit's grass).</summary>
    public float GrassSpacing { get; set; } = 0.12f;

    /// <summary>The grass mask's rows counted from the north (a test switch).</summary>
    public bool GrassMaskFromNorth { get; set; }

    /// <summary>The terrain's grass: its setup, its plants found by path among the kinds already set.</summary>
    public void SetGrass(GrassSetup? setup)
    {
        _grass = setup;
        _grassKinds.Clear();
        for (int k = 0; k < _plants.Count; k++) _grassKinds[_plants[k].Kind.Path] = k;
        _grassGrown.Clear();
        Invalidate();
    }

    /// <summary>The cells round the camera that don't have their grass yet get it, a few a frame.</summary>
    private void GrowGrass()
    {
        if (_grass is not { } grass || _terrain is not { } terrain || _control.Length == 0) return;
        int cells = (int)MathF.Ceiling(GrassReach / PlantCell);
        int cx = (int)MathF.Floor(_eye.X / PlantCell), cy = (int)MathF.Floor(_eye.Y / PlantCell);
        int grown = 0;
        // nearest first, so the grass round the camera comes before the rest
        var wanted = new List<(int X, int Y, float D)>();
        for (int y = cy - cells; y <= cy + cells; y++)
            for (int x = cx - cells; x <= cx + cells; x++)
                if (!_grassGrown.Contains((x, y))) wanted.Add((x, y, MathF.Abs(x - cx) + MathF.Abs(y - cy)));
        foreach (var (x, y, _) in wanted.OrderBy(w => w.D))
        {
            if (grown++ >= 4) break;
            _grassGrown.Add((x, y));
            GrowCell(grass, terrain, x, y);
        }
    }

    private void GrowCell(GrassSetup grass, WorldTerrain terrain, int cellX, int cellY)
    {
        float spacing = terrain.Size / _side, range = terrain.Highest - terrain.Lowest;
        var cell = new List<(int, FoliageInstance)>();
        foreach (var entry in grass.Entries)
        {
            if (!_grassKinds.TryGetValue(entry.Path, out int kind)) continue;
            // the spacing follows the brush's radius scale alone (measured against REDkit: its smallest flowers are as rare
            // as their radius scale says, not as dense as their size would make them)
            float step = MathF.Max(0.2f, entry.RadiusScale * GrassSpacing);
            int n = Math.Max(1, (int)(PlantCell / step));
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    uint h = Hash((uint)(cellX * 73856093) ^ (uint)(cellY * 19349663) ^ (uint)(kind * 83492791) ^ (uint)(i * 2654435761) ^ (uint)(j * 40503));
                    float jx = (h & 0xFFFF) / 65535f, jy = (h >> 16) / 65535f;
                    var at = new Vector2(cellX * PlantCell + (i + jx) * PlantCell / n, cellY * PlantCell + (j + jy) * PlantCell / n);
                    // the grass mask, rows from the south as the terrain material reads it
                    int mx = (int)((at.X - terrain.Corner.X) / terrain.Size * grass.MaskResolution), my = (int)((at.Y - terrain.Corner.Y) / terrain.Size * grass.MaskResolution);
                    if (GrassMaskFromNorth) my = grass.MaskResolution - 1 - my;
                    if (mx < 0 || my < 0 || mx >= grass.MaskResolution || my >= grass.MaskResolution || !grass.Mask[my * grass.MaskResolution + mx]) continue;
                    // the share of the four control samples round the point whose background texture is the brush's
                    float sx = (at.X - terrain.Corner.X) / spacing - 0.5f, sy = (terrain.Corner.Y + terrain.Size - at.Y) / spacing - 0.5f;
                    int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
                    float fx = sx - x0, fy = sy - y0, share = 0, threshold = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int xx = Math.Clamp(x0 + (k & 1), 0, _side - 1), yy = Math.Clamp(y0 + (k >> 1), 0, _side - 1);
                        float w = ((k & 1) != 0 ? fx : 1 - fx) * ((k >> 1) != 0 ? fy : 1 - fy);
                        int control = _control[yy * _side + xx], back = (control & 31) - 1;
                        if (back == entry.Slot) share += w;
                        threshold += ((control >> 10) & 7) / 8f * w;
                    }
                    // grass grows on the horizontal texture only: where the slope (the terrain material's own test, from
                    // the ground's normal) shows the vertical one, none
                    if (share > 0)
                    {
                        float H(int xx, int yy) => _heights[Math.Clamp(yy, 0, _side - 1) * _side + Math.Clamp(xx, 0, _side - 1)] / 65535f * range;
                        float gx = (H(x0 + 1, y0) - H(x0 - 1, y0)) / (2 * spacing), gy = (H(x0, y0 - 1) - H(x0, y0 + 1)) / (2 * spacing);
                        float nz = 1 / MathF.Sqrt(1 + gx * gx + gy * gy);
                        float angle = MathF.Acos(Math.Clamp(nz, 0, 1)), a3 = angle * angle * angle;
                        float tangent = Math.Clamp(angle + a3 / 3 + 2 * a3 * angle * angle / 15, 0, 1);
                        // the shader judges the slope with the normal maps' bumps at full strength, so the vertical texture
                        // shows as soon as the smooth ground passes the threshold: grass only below it (fading over 0.05)
                        share *= 1 - Math.Clamp((tangent - threshold) / 0.05f, 0, 1);
                    }
                    uint h2 = Hash(h ^ 0x9E3779B9);
                    if ((h2 & 0xFFFF) / 65535f >= share) continue;
                    // the height where it stands, from the four samples
                    float Height(int xx, int yy) => terrain.Lowest + _heights[Math.Clamp(yy, 0, _side - 1) * _side + Math.Clamp(xx, 0, _side - 1)] / 65535f * range;
                    float z = (Height(x0, y0) * (1 - fx) + Height(x0 + 1, y0) * fx) * (1 - fy) + (Height(x0, y0 + 1) * (1 - fx) + Height(x0 + 1, y0 + 1) * fx) * fy;
                    float scale = entry.Size * (1 + entry.SizeVariation * (((h2 >> 16) & 0xFF) / 127.5f - 1));
                    cell.Add((kind, new FoliageInstance(new Vector3(at, z), MathF.Max(0.05f, scale), (h2 >> 24) / 255f * MathF.Tau)));
                }
        }
        if (cell.Count == 0) return;
        if (!_plantCells.TryGetValue((cellX, cellY), out var list)) _plantCells[(cellX, cellY)] = list = [];
        list.AddRange(cell);
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }
}
