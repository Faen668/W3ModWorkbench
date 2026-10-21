using System.IO;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

/// <summary>
/// Links to sounds, as the viewers show them: a sound event by its name (<c>sound-event:g_footstep_walk</c>), a sound
/// bank by its name (<c>sound-bank:combat</c>), and the sounds and voice lines the game files list (<c>sounds\banks\…</c>,
/// <c>sounds\streamed\…</c>, <c>speech\en\…</c>), which are read from the game's sound caches and speech files rather
/// than its bundles.
/// </summary>
public static class SoundLinks
{
    public const string Event = "sound-event:", Bank = "sound-bank:";

    /// <summary>Whether a path is one of the sound caches' or speech files' sounds.</summary>
    public static bool IsSound(string path) =>
        path.StartsWith(SoundCacheArchive.Banks, StringComparison.OrdinalIgnoreCase) || path.StartsWith(SoundCacheArchive.Streamed, StringComparison.OrdinalIgnoreCase)
        || SpeechArchive.LineOf(path) is not null;

    /// <summary>A sound or voice line by its path, and where it's from; null when the game hasn't got it.</summary>
    public static async Task<(byte[] Data, string Source)?> ReadAsync(string path)
    {
        if (GameLocator.Resolve(null) is not { } game) return null;
        if (SpeechArchive.LineOf(path) is var (language, id))
        {
            var speech = await W3Speech.ForLanguage(game, language);
            return await Task.Run(() => speech.FirstOrDefault(s => s.Has(id)) is { } file && file.Sound(id) is { } data ? (data, Path.GetFileName(file.Path)) : ((byte[], string)?)null);
        }
        string name = SoundCacheArchive.CacheName(path);
        var caches = await SoundCache.ForGame(game);
        return await Task.Run(() => caches.FirstOrDefault(c => c.Has(name)) is { } cache && cache.Read(name) is { } data
            ? (data, Path.GetFileName(Path.GetDirectoryName(cache.Path)) + "\\" + Path.GetFileName(cache.Path)) : ((byte[], string)?)null);
    }
}
