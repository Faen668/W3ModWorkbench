using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>A plant placed in a foliage cell: where its foot is, how big it is, and which way it's turned (radians about Z).</summary>
public readonly record struct FoliageInstance(Vector3 Position, float Scale, float Yaw);

/// <summary>Every plant of one kind in a cell: the SpeedTree file (.srt) that draws it, and where each one stands.</summary>
public sealed record FoliageGroup(string Tree, bool Grass, IReadOnlyList<FoliageInstance> Instances);

/// <summary>
/// A foliage cell (.flyr, a CFoliageResource): the trees and grass of a square of a world, 64 m a side, named by its
/// corner (foliage_-128.00_0.00.flyr). After its properties it keeps two lists, trees then grass: a compressed count of
/// kinds, each the import of its .srt and a compressed count of instances of 24 bytes each (position, scale, then the
/// cosine and sine of its turn).
/// </summary>
public sealed class FoliageFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required IReadOnlyList<FoliageGroup> Groups { get; init; }

    public static bool IsFoliage(string path) => path.EndsWith(".flyr", StringComparison.OrdinalIgnoreCase);

    public static FoliageFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var cell = file.First("CFoliageResource") ?? throw new InvalidDataException(L.T("FoliageFile.NotFoliage"));
        var tail = cell.Tail;
        var groups = new List<FoliageGroup>();
        int at = 0;
        for (int list = 0; list < 2; list++)
        {
            if (!Packed.Count(tail, ref at, out int kinds)) break;
            for (int k = 0; k < kinds && at + 4 <= tail.Length; k++)
            {
                int handle = BitConverter.ToInt32(tail, at);
                at += 4;
                if (!Packed.Count(tail, ref at, out int count) || at + 24L * count > tail.Length) break;
                var instances = new FoliageInstance[count];
                for (int i = 0; i < count; i++, at += 24)
                {
                    float F(int o) => BitConverter.ToSingle(tail, at + o);
                    instances[i] = new FoliageInstance(new Vector3(F(0), F(4), F(8)), F(12), MathF.Atan2(F(20), F(16)));
                }
                groups.Add(new FoliageGroup(file.PathOf(new RedHandle(handle, false)) ?? "", list == 1, instances));
            }
        }
        return new FoliageFile { File = file, Resource = cell, Groups = groups };
    }
}
