using System.IO;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Core.Sounds.Banks;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>What happened to one file of a cache being upgraded.</summary>
public enum UpgradeOutcome { Upgraded, AlreadyCurrent, Kept, LeftOut }

/// <summary>One file of an upgraded cache: what happened to it, and the notes on the way.</summary>
public sealed record UpgradedFile(string Name, UpgradeOutcome Outcome, IReadOnlyList<string> Notes);

/// <summary>
/// Upgrades a mod's sound cache for the Remastered: each bank of 88 or 140 rewritten as 150 (see
/// <see cref="BankUpgrade"/>), each streamed sound of the original game's Wwise given the header the Remastered's
/// engine reads, and the cache written as the game's own are. A bank that can't be upgraded is kept as it was (the
/// game won't play it, but nothing is lost); a mod's own Init.bnk is left out, so the game's is used.
/// </summary>
public static class SoundCacheUpgrade
{
    /// <summary>
    /// Upgrades the cache at <paramref name="source"/> into a new one at <paramref name="target"/>. A streamed sound a
    /// bank names is looked for in the mod's cache, then in <paramref name="game"/> (the game's own caches), whose
    /// Init.bnk also says which buses there are.
    /// </summary>
    public static IReadOnlyList<UpgradedFile> Upgrade(string source, string target, IReadOnlyList<SoundCache> game,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var cache = SoundCache.Open(source);
        var files = cache.Files.Select(f => f.Name).ToList();
        IReadOnlySet<uint>? buses = null;
        if (game.FirstOrDefault(c => c.Has("init.bnk"))?.Read("init.bnk") is { } gameInit) buses = BankUpgrade.Buses(gameInit);

        // streamed sounds: the mod's own, upgraded, else the game's
        var wems = new Dictionary<uint, byte[]?>();
        byte[]? WemOf(uint id)
        {
            if (wems.TryGetValue(id, out var known)) return known;
            byte[]? wem = cache.Read($"{id}.wem");
            if (wem is not null)
                try { wem = WemUpgrade.Upgrade(wem); }
                catch (InvalidDataException) { }
            wem ??= game.FirstOrDefault(c => c.Has($"{id}.wem"))?.Read($"{id}.wem");
            // only what's needed to size a sound is kept, not every sound whole
            byte[]? start = wem is null ? null : WemUpgrade.Prefetch(wem) ?? wem;
            return wems[id] = start;
        }

        var results = new List<UpgradedFile>();
        var output = new List<(string Name, Func<byte[]> Data)>();
        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, files.Count));
            string name = files[i];
            if (name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bank = cache.Read(name)!;
                var result = BankUpgrade.Upgrade(bank, WemOf, buses);
                bool current = SoundCacheWriter.GameBankVersion == BankVersionOf(bank);
                bool init = !current && (name.Equals("init.bnk", StringComparison.OrdinalIgnoreCase) || name.EndsWith("\\init.bnk", StringComparison.OrdinalIgnoreCase));
                if (result.Bank is { } upgraded)
                {
                    output.Add((name, () => upgraded));
                    results.Add(new(name, UpgradeOutcome.Upgraded, result.Notes));
                }
                else if (init)
                    results.Add(new(name, UpgradeOutcome.LeftOut, result.Notes));
                else
                {
                    output.Add((name, () => cache.Read(name)!));
                    results.Add(new(name, current ? UpgradeOutcome.AlreadyCurrent : UpgradeOutcome.Kept, result.Notes));
                }
            }
            else if (name.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            {
                byte[] wem = cache.Read(name)!;
                byte[] upgraded;
                List<string> notes = [];
                try { upgraded = WemUpgrade.Upgrade(wem); }
                catch (InvalidDataException ex)
                {
                    upgraded = wem;
                    notes.Add(ex.Message);
                }
                output.Add((name, () => upgraded));
                results.Add(new(name, ReferenceEquals(upgraded, wem) ? notes.Count > 0 ? UpgradeOutcome.Kept : UpgradeOutcome.AlreadyCurrent : UpgradeOutcome.Upgraded, notes));
            }
            else
            {
                output.Add((name, () => cache.Read(name)!));
                results.Add(new(name, UpgradeOutcome.Kept, []));
            }
        }
        progress?.Report((files.Count, files.Count));
        SoundCacheWriter.Write(target, output, cancel);
        return results;
    }

    /// <summary>
    /// Upgrades one bank file at <paramref name="source"/> into <paramref name="target"/>; its streamed sounds are
    /// looked for beside it (as <c>ID.wem</c>), then in <paramref name="game"/>.
    /// </summary>
    public static UpgradedFile UpgradeBank(string source, string target, IReadOnlyList<SoundCache> game)
    {
        string folder = Path.GetDirectoryName(source) ?? "";
        byte[]? WemOf(uint id)
        {
            string beside = Path.Combine(folder, $"{id}.wem");
            byte[]? wem = File.Exists(beside) ? File.ReadAllBytes(beside) : game.FirstOrDefault(c => c.Has($"{id}.wem"))?.Read($"{id}.wem");
            if (wem is null) return null;
            try { wem = WemUpgrade.Upgrade(wem); }
            catch (InvalidDataException) { }
            return WemUpgrade.Prefetch(wem) ?? wem;
        }
        IReadOnlySet<uint>? buses = game.FirstOrDefault(c => c.Has("init.bnk"))?.Read("init.bnk") is { } init ? BankUpgrade.Buses(init) : null;
        byte[] bank = File.ReadAllBytes(source);
        var result = BankUpgrade.Upgrade(bank, WemOf, buses);
        string name = Path.GetFileName(source);
        if (result.Bank is null)
            return new(name, SoundCacheWriter.GameBankVersion == BankVersionOf(bank) ? UpgradeOutcome.AlreadyCurrent : UpgradeOutcome.Kept, result.Notes);
        File.WriteAllBytes(target, result.Bank);
        return new(name, UpgradeOutcome.Upgraded, result.Notes);
    }

    /// <summary>What a cache or bank file holds, for saying so before it's upgraded: banks by version, and streamed sounds.</summary>
    public static (SortedDictionary<uint, int> BanksByVersion, int Streamed) Survey(string path)
    {
        var versions = new SortedDictionary<uint, int>();
        if (path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
        {
            uint v = SoundCacheWriter.BankVersion(path) ?? 0;
            versions[v] = 1;
            return (versions, 0);
        }
        var cache = SoundCache.Open(path);
        int streamed = 0;
        foreach (var (name, _) in cache.Files)
        {
            if (name.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) streamed++;
            else if (name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) && cache.ReadHead(name, 12) is { } head)
            {
                uint v = BankVersionOf(head);
                versions[v] = versions.GetValueOrDefault(v) + 1;
            }
        }
        return (versions, streamed);
    }

    private static uint BankVersionOf(byte[] bank) => bank.Length >= 12 && bank[0] == 'B' && bank[1] == 'K' ? BitConverter.ToUInt32(bank, 8) : 0;
}
