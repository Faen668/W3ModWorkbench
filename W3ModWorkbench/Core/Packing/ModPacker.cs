using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>What packing a workspace made, and what the modder should know about it.</summary>
public sealed record PackResult(IReadOnlyList<string> Made, IReadOnlyList<string> Notes);

/// <summary>
/// Packs a mod's workspace (its files under their game paths) into the files a mod's <c>content</c> folder has, as
/// REDkit's cook, pack and cache tools would, without them:
/// <list type="bullet">
/// <item>the files go in <c>blob0.bundle</c> (and <c>buffers0.bundle</c> for mesh and animation buffers), with the
/// <c>metadata.store</c> the game reads them by (<see cref="BundleWriter"/>, <see cref="MetadataStore"/>);</item>
/// <item>textures are cooked: a REDkit source <c>.xbm</c> exactly as REDkit cooks it, and a picture (<c>.dds</c>,
/// <c>.tga</c>, <c>.bmp</c>, or a <c>.png</c>/<c>.jpg</c> standing in for a texture the workspace or the game has) made
/// into the <c>.xbm</c> of its name; their mip levels go in <c>texture.cache</c> (<see cref="TextureCooker"/>);</item>
/// <item>sounds (<c>.wem</c>, <c>.bnk</c>) go in <c>soundspc.cache</c>, where the game looks for them;</item>
/// <item>caches made some other way (a <c>collision.cache</c>, say) at the top of the workspace go in as they are.</item>
/// </list>
/// A cooked file (one copied from the game) goes in as it is.
/// </summary>
public static class ModPacker
{
    /// <summary>Pictures that are always made into textures; a .png or .jpg is only when there's a texture of its name.</summary>
    private static readonly HashSet<string> TextureOnly = new(StringComparer.OrdinalIgnoreCase) { ".dds", ".tga", ".bmp" };

    /// <summary>
    /// Packs <paramref name="workspace"/> into <paramref name="content"/>. <paramref name="gameFile"/> reads a game file
    /// by its path (for the texture a picture replaces), or is null. Null when the workspace has nothing to pack.
    /// </summary>
    public static PackResult? Pack(string workspace, string content, Func<string, byte[]?>? gameFile, IProgress<string>? progress, CancellationToken cancel)
    {
        string root = Path.GetFullPath(workspace);
        var all = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => BundleWriter.Packs(f.Name, f.Length))
            .Select(f => new BundleWriter.Source(f.FullName, BundleWriter.GamePath(Path.GetRelativePath(root, f.FullName)), f.Length))
            .ToList();
        var caches = new DirectoryInfo(root).GetFiles("*.cache");
        if (all.Count == 0 && caches.Length == 0) return null;

        var made = new List<string>();
        var notes = new List<string>();
        var byPath = all.ToDictionary(s => s.GamePath, StringComparer.OrdinalIgnoreCase);

        // sounds, and pictures that are textures
        var sounds = new List<BundleWriter.Source>();
        var pictures = new Dictionary<string, BundleWriter.Source>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<BundleWriter.Source>();
        foreach (var file in BundleWriter.InFolderOrder(all))
        {
            string ext = Path.GetExtension(file.GamePath);
            if (SoundCacheWriter.IsSound(file.GamePath)) { sounds.Add(file); continue; }
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) || Pictures.Extensions.Contains(ext))
            {
                string texture = Path.ChangeExtension(file.GamePath, ".xbm");
                bool isTexture = TextureOnly.Contains(ext) || byPath.ContainsKey(texture) || gameFile?.Invoke(texture) is not null;
                if (isTexture)
                {
                    if (!pictures.TryAdd(texture, file)) notes.Add(L.T("ModPacker.LeftOutMakesAlready", file.GamePath, pictures[texture].GamePath, texture));
                    continue;
                }
            }
            rest.Add(file);
        }

        string staging = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "pack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var bundled = new List<BundleWriter.Source>();
            var cache = new List<CacheTexture>();
            int cooked = 0, imported = 0;
            BundleWriter.Source Stage(string gamePath, byte[] data)
            {
                string to = Path.Combine(staging, gamePath);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.WriteAllBytes(to, data);
                return new BundleWriter.Source(to, gamePath, data.Length);
            }

            // pictures made into textures, the workspace's or game's texture of that name as the pattern
            if (pictures.Count > 0) progress?.Report(L.T("ModPacker.MakingTextures", L.T("Count.Texture", pictures.Count)));
            foreach (var (texture, picture) in pictures)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var templateBytes = byPath.TryGetValue(texture, out var own) ? File.ReadAllBytes(own.FilePath) : gameFile?.Invoke(texture);
                    var template = templateBytes is null ? null : TextureCooker.ReadTexture(templateBytes);
                    var result = TextureCooker.Import(texture, picture.FilePath, File.ReadAllBytes(picture.FilePath), template);
                    string gamePath = texture.ToLowerInvariant();
                    bundled.Add(Stage(gamePath, result.Resource));
                    if (result.Cache is not null) cache.Add(result.Cache);
                    if (result.Note is { } note) notes.Add($"{picture.GamePath}: {note}");
                    imported++;
                }
                catch (InvalidDataException ex)
                {
                    notes.Add(L.T("ModPacker.WasntMadeIntoTexture", picture.GamePath, ex.Message));
                }
            }

            // everything else: textures REDkit hasn't cooked yet are cooked, the rest go in as they are
            foreach (var file in rest)
            {
                cancel.ThrowIfCancellationRequested();
                if (pictures.ContainsKey(file.GamePath)) continue; // replaced by a picture
                if (file.GamePath.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (TextureCooker.CookBitmap(file.GamePath, File.ReadAllBytes(file.FilePath)) is { } result)
                        {
                            bundled.Add(Stage(file.GamePath.ToLowerInvariant(), result.Resource));
                            if (result.Cache is not null) cache.Add(result.Cache);
                            cooked++;
                            continue;
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        notes.Add(L.T("ModPacker.CouldntCookedGoes", file.GamePath, ex.Message));
                    }
                }
                else if ((file.GamePath.EndsWith(".w2cube", StringComparison.OrdinalIgnoreCase) || file.GamePath.EndsWith(".texarray", StringComparison.OrdinalIgnoreCase))
                    && IsSourceTexture(file.FilePath))
                    notes.Add(L.T("ModPacker.REDkitSourceFileCube", file.GamePath));
                bundled.Add(file);
            }

            Directory.CreateDirectory(content);
            if (bundled.Count > 0)
            {
                var bundles = BundleWriter.Pack(BundleWriter.InFolderOrder(bundled), content, progress, cancel);
                progress?.Report(L.T("ModPacker.WritingBundlesMetadata"));
                MetadataStore.Write(content);
                made.Add(L.T("ModPacker.FilesInBundles", L.T("Count.File", bundled.Count), string.Join(", ", bundles.Select(b => Path.GetFileName(b.FilePath))))
                    + (cooked + imported > 0 ? $" ({string.Join(", ", new[] { cooked > 0 ? L.T("ModPacker.TexturesCooked", L.T("Count.Texture", cooked)) : null, imported > 0 ? L.T("ModPacker.TexturesFromPictures", L.T("Count.Texture", imported)) : null }.OfType<string>())})" : ""));
            }
            if (cache.Count > 0)
            {
                progress?.Report(L.T("ModPacker.WritingTextureCache", L.T("Count.Texture", cache.Count)));
                cache.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
                TextureCacheWriter.Write(Path.Combine(content, TextureCacheWriter.FileName), cache, cancel);
                made.Add(L.T("ModPacker.MipLevelsIn", L.T("Count.Texture", cache.Count), TextureCacheWriter.FileName));
            }
            if (sounds.Count > 0)
            {
                progress?.Report(L.T("ModPacker.WritingSoundCache", L.T("Count.Sound", sounds.Count)));
                var named = new List<(string, string)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sound in sounds)
                {
                    string name = Path.GetFileName(sound.GamePath);
                    if (seen.Add(name)) named.Add((name, sound.FilePath));
                    else notes.Add(L.T("ModPacker.LeftOutSoundCache", sound.GamePath, name));
                }
                SoundCacheWriter.Write(Path.Combine(content, SoundCacheWriter.FileName), named, cancel);
                made.Add(L.T("ModPacker.SoundsIn", L.T("Count.Sound", named.Count), SoundCacheWriter.FileName));
            }

            // caches made elsewhere (a collision cache, say) at the workspace's top go in as they are, beside the bundles
            foreach (var file in caches)
            {
                string to = Path.Combine(content, file.Name);
                if (File.Exists(to))
                {
                    notes.Add(L.T("ModPacker.WorkspaceLeftOutBuild", file.Name));
                    continue;
                }
                file.CopyTo(to);
                made.Add(L.T("ModPacker.ContentWorkspaceHas", file.Name));
            }
            return new PackResult(made, notes);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether a file is a CR2W resource REDkit hasn't cooked: none of its objects carry the cook's flag.</summary>
    private static bool IsSourceTexture(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return Cr2w.Cr2wFile.IsCr2w(data) && !TextureCooker.IsCooked(Cr2w.Cr2wFile.Read(data));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

}
