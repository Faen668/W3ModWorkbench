using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace W3ModWorkbench.Core;

/// <summary>A release of the app on GitHub: its version, notes, and the zip for 64-bit Windows.</summary>
public sealed record AppRelease(
    Version Version, string Tag, string Name, string Notes, string Page, DateTimeOffset Published,
    string? ZipUrl, string? ZipName, long ZipSize, string? Sha256);

/// <summary>Why the running copy can't replace itself, so an update is only downloaded for the user to unpack.</summary>
public enum InstallBlock { None, NotSingleFile, FolderReadOnly }

/// <summary>
/// The app's own updates, from its GitHub releases: the latest release (GitHub's API, one request), its zip
/// downloaded to %LocalAppData%\W3ModWorkbench\Updates and checked against the SHA-256 GitHub gives, then put in place
/// of the running copy. Windows lets a running exe be renamed but not overwritten, so each file of the app's is renamed
/// to name.update-old, the new one is copied in, and the next start deletes the old ones. Nothing is sent but the
/// requests themselves.
/// </summary>
public static class AppUpdates
{
    public const string Repository = "Faen668/W3ModWorkbench"; // loc-ignore
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases"; // loc-ignore
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest"; // loc-ignore
    private const string OldSuffix = ".update-old"; // loc-ignore
    private const string ExeName = "W3ModWorkbench.exe"; // loc-ignore

    /// <summary>Where the latest release is asked for; a test points it elsewhere.</summary>
    public static string LatestUrl { get; set; } = LatestApi;

    /// <summary>The running copy's version, as its releases are numbered (three parts).</summary>
    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Where downloaded updates are kept until they're installed.</summary>
    public static string DownloadFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "W3ModWorkbench", "Updates"); // loc-ignore

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub's API refuses requests without one; it names the program, nothing about the user
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"W3ModWorkbench/{Current}"); // loc-ignore
        return http;
    }

    private static Version Trim(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>"v1.8.0", "1.8", "1.8.0-beta": the version it names, or null.</summary>
    public static Version? ParseTag(string tag)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) text = text[..cut];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? Trim(v) : null;
    }

    /// <summary>The latest release on GitHub (drafts and pre-releases aren't counted).</summary>
    public static async Task<AppRelease> LatestAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json"); // loc-ignore
        using var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    /// <summary>A release from GitHub's JSON for one.</summary>
    public static AppRelease Parse(JsonElement root)
    {
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        string tag = Str(root, "tag_name");
        var version = ParseTag(tag) ?? throw new InvalidDataException(tag);
        DateTimeOffset.TryParse(Str(root, "published_at"), out var published);
        string? url = null, name = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                string n = Str(asset, "name");
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !n.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) continue; // loc-ignore
                name = n;
                url = Str(asset, "browser_download_url");
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long l) ? l : 0;
                string digest = Str(asset, "digest");
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].ToLowerInvariant(); // loc-ignore
                break;
            }
        return new AppRelease(version, tag, Str(root, "name"), Str(root, "body").Replace("\r", ""), Str(root, "html_url"), published,
            url, name, size, sha);
    }

    public static bool IsNewer(AppRelease release) => release.Version > Current;

    /// <summary>Whether this copy can put an update in place of itself, and why not when it can't.</summary>
    public static InstallBlock CanInstall()
    {
        // a published single exe has no assembly file of its own; a build from source runs from W3ModWorkbench.dll
        if (!string.IsNullOrEmpty(typeof(AppUpdates).Assembly.Location) || Environment.ProcessPath is null) return InstallBlock.NotSingleFile;
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Environment.ProcessId}"); // loc-ignore
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return InstallBlock.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return InstallBlock.FolderReadOnly;
        }
    }

    /// <summary>
    /// Downloads a release's zip (once: a copy already downloaded and whole is used again), checks it against the
    /// SHA-256 GitHub gives for it, and returns where it is. Progress is the bytes downloaded and the size.
    /// </summary>
    public static async Task<string> DownloadAsync(AppRelease release, IProgress<(long Done, long Total)>? progress, CancellationToken cancel)
    {
        if (release.ZipUrl is not { Length: > 0 } url || release.ZipName is null) throw new InvalidOperationException();
        Directory.CreateDirectory(DownloadFolder);
        string path = Path.Combine(DownloadFolder, Path.GetFileName(release.ZipName));
        if (File.Exists(path) && await MatchesAsync(path, release, cancel).ConfigureAwait(false)) return path;

        string part = path + ".part"; // loc-ignore
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.ZipSize;
            await using var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            var shown = Stopwatch.StartNew();
            int read;
            while ((read = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (shown.ElapsedMilliseconds > 100) { progress?.Report((done, total)); shown.Restart(); }
            }
            progress?.Report((done, total));
        }
        if (!await MatchesAsync(part, release, cancel).ConfigureAwait(false))
        {
            File.Delete(part);
            throw new InvalidDataException(release.ZipName);
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    private static async Task<bool> MatchesAsync(string path, AppRelease release, CancellationToken cancel)
    {
        if (release.ZipSize > 0 && new FileInfo(path).Length != release.ZipSize) return false;
        if (release.Sha256 is null) return true;
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == release.Sha256;
    }

    /// <summary>
    /// Puts the files of a downloaded release's zip in place of the app's in <paramref name="appFolder"/>: each one
    /// there already is renamed to name.update-old first (a running exe can be renamed, not overwritten), and if
    /// anything fails, everything done is put back. The exe goes in under the running copy's own name, so a renamed
    /// copy keeps its name. Returns the exe to start.
    /// </summary>
    public static string Install(string zipPath, string appFolder, string runningExe)
    {
        string staging = Path.Combine(DownloadFolder, "unpacked"); // loc-ignore
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        // the zip holds the app's files at its top; a zip of the folder holding them has them one level down
        string root = staging;
        if (!File.Exists(Path.Combine(root, ExeName)) && Directory.GetDirectories(root) is [var only] && File.Exists(Path.Combine(only, ExeName)))
            root = only;
        if (!File.Exists(Path.Combine(root, ExeName))) throw new InvalidDataException(Path.GetFileName(zipPath));

        string exeTarget = Path.GetFullPath(runningExe);
        var moves = new List<(string Target, string? Old)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                string target = string.Equals(relative, ExeName, StringComparison.OrdinalIgnoreCase)
                    ? exeTarget : Path.Combine(appFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + OldSuffix;
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                moves.Add((target, old));
                File.Copy(file, target);
            }
        }
        catch
        {
            // back as it was: the new files out, the old ones back under their names
            for (int i = moves.Count - 1; i >= 0; i--)
                try
                {
                    var (target, old) = moves[i];
                    if (File.Exists(target)) File.Delete(target);
                    if (old is not null) File.Move(old, target);
                }
                catch { /* the rest still go back */ }
            throw;
        }
        try { Directory.Delete(staging, recursive: true); } catch { /* deleted at the next start */ }
        return exeTarget;
    }

    /// <summary>
    /// After an update: deletes the old files it set aside (once nothing runs them) and the downloads it installed.
    /// Done at every start; whatever is still in use is left for the next.
    /// </summary>
    public static void CleanUp(string appFolder)
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(appFolder, "*" + OldSuffix, SearchOption.TopDirectoryOnly))
                try { File.Delete(old); } catch { /* another copy still runs it */ }
        }
        catch { /* a folder that can't be read: nothing to do */ }
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            foreach (string zip in Directory.EnumerateFiles(DownloadFolder, "*.zip"))
                if (ParseTag(VersionInName(zip)) is { } v && v <= Current) File.Delete(zip);
            foreach (string part in Directory.EnumerateFiles(DownloadFolder, "*.part")) File.Delete(part);
            string staging = Path.Combine(DownloadFolder, "unpacked"); // loc-ignore
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch { /* left for the next start */ }
    }

    /// <summary>"W3ModWorkbench-v1.9.0-win-x64.zip" → "v1.9.0".</summary>
    private static string VersionInName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int at = name.IndexOf("-v", StringComparison.OrdinalIgnoreCase); // loc-ignore
        return at < 0 ? "" : name[(at + 1)..];
    }

    /// <summary>Starts the updated exe, once this copy has closed.</summary>
    public static void Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
}
