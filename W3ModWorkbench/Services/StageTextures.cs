using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// The textures a stage's actors are drawn with, all read before the actors are shown, as REDkit has a scene's
/// entities loaded before it plays: several at once on the thread pool, each material's colour texture tinted as the
/// actor's appearance colours its component. Read as they're shown instead, a scene played straight away stays grey
/// until they catch up.
/// </summary>
public static class StageTextures
{
    /// <summary>The colour the parts of an undyed piece of gear that take a dye are shown in: no colour, a little darker.</summary>
    public static readonly ColorShift Undyed = new(0, -100, -35);

    /// <summary>
    /// How a mesh drawn for an actor is coloured: by the colour entries of the entity and appearance it comes from, for
    /// the component that draws it; a stand-in piece of the player's gear undyed, as plain dark leather.
    /// </summary>
    public static (ColorShift? First, ColorShift? Second)? Coloring(EntityFile actor, EntityFile colorEntity, string? colorAppearance, string component)
    {
        var coloring = LookTextures.Coloring(colorEntity, colorAppearance, component);
        return coloring is null && !ReferenceEquals(colorEntity, actor) ? (Undyed, Undyed) : coloring;
    }

    /// <summary>
    /// Every material's texture for each mesh, by its place in <paramref name="parts"/>: the picture to draw it with and its
    /// normal map, or that it's left out (a shadow drawn over the eyes). <paramref name="progress"/> hears how many are done of how many.
    /// </summary>
    public static async Task<List<(int Part, int Material, BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)>> LoadAsync(
        IReadOnlyList<(MeshFile Mesh, (ColorShift? First, ColorShift? Second)? Coloring)> parts, ResourceLocator locator,
        Func<string, Task<BitmapSource?>> texture, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        var jobs = new List<(int Part, int Material)>();
        for (int p = 0; p < parts.Count; p++)
            for (int m = 0; m < parts[p].Mesh.Materials.Count; m++) jobs.Add((p, m));
        var results = new (int, int, BitmapSource?, MaterialLook?, bool)[jobs.Count];
        int done = 0;
        progress?.Report((0, jobs.Count));
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount - 1, 2, 8));
        await Task.WhenAll(jobs.Select(async (job, i) =>
        {
            await gate.WaitAsync(token);
            try
            {
                var (mesh, coloring) = parts[job.Part];
                var (picture, look, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, job.Material, locator, texture, coloring), token)
                    .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : ((BitmapSource?)null, (MaterialLook?)null, false), TaskScheduler.Default);
                results[i] = (job.Part, job.Material, picture, look, leaveOut);
            }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), jobs.Count));
            }
        }));
        return [.. results];
    }

    /// <summary>"Loading textures, 12 of 80…" for a stage's message.</summary>
    public static string Describe((int Done, int Total) progress) =>
        progress.Total == 0 ? "Putting the actors together…" : $"Loading textures, {progress.Done} of {progress.Total}…";
}
