using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace W3ModWorkbench;

/// <summary>A link to a game file, as another program or a web page opens it in the tool: the file's game path, and whether to open it or only show it.</summary>
public sealed record AppLink(string Path, bool Open);

/// <summary>
/// Links that open a game file in the tool's Asset Browser, such as EasyAssetBrowser's site has:
/// <c>w3modworkbench://asset?path=environment/decorations/kaer_morhen/table.w2mesh</c> shows the file there, highlighted,
/// and <c>&amp;action=open</c> opens it as well. Windows starts the tool with the link once the tool has registered the
/// <c>w3modworkbench</c> link type for the user (<see cref="Register"/>; nothing else is registered, and only for the
/// user signed in). A tool already running is handed the link instead of a second one starting.
/// </summary>
public static class AppLinks
{
    public const string Scheme = "w3modworkbench";

    private static readonly string ClassKey = $@"Software\Classes\{Scheme}";

    // one listener per user; only that user's programs can connect
    private static readonly string PipeName = $"W3ModWorkbench.Links.{Environment.UserName}";

    /// <summary>A link to a game file: <c>w3modworkbench://asset?path=…</c>, with the path as the game's (either slash).</summary>
    public static string For(string gamePath, bool open = false) =>
        $"{Scheme}://asset?path={Uri.EscapeDataString(gamePath.Replace('\\', '/'))}" + (open ? "&action=open" : "");

    /// <summary>
    /// Reads a link, or null when it isn't one of the tool's. The path is only ever looked up among the game's files,
    /// never read from disk, but one that climbs out of a folder or names a drive is refused all the same.
    /// </summary>
    public static AppLink? Parse(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("asset", StringComparison.OrdinalIgnoreCase))
            return null;
        string? path = null, action = null;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase)) path = value;
            else if (key.Equals("action", StringComparison.OrdinalIgnoreCase)) action = value;
        }
        path = path?.Replace('/', '\\').Trim().Trim('\\');
        if (string.IsNullOrEmpty(path) || path.Contains(':') || path.Split('\\').Any(p => p is "" or "." or ".."))
            return null;
        return new AppLink(path, action?.Equals("open", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ---------------------------------------------------------------- the link type

    private static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

    /// <summary>Whether Windows opens the tool's links with this copy of the tool.</summary>
    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassKey}\shell\open\command");
            return key?.GetValue(null) is string command && command.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether any copy of the tool is registered to open the links, maybe one since moved.</summary>
    public static bool IsRegisteredAnywhere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassKey);
            return key is not null;
        }
    }

    /// <summary>Has Windows open the tool's links with this copy of the tool, for the user signed in.</summary>
    public static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(ClassKey);
        key.SetValue(null, "URL:W3 Mod Workbench");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{Environment.ProcessPath}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, Command);
    }

    /// <summary>Takes the link type away again, so links to the tool open nothing.</summary>
    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);

    /// <summary>When the links were registered to a copy of the tool somewhere else (an older version's folder), points them at this one.</summary>
    public static void KeepRegistrationCurrent()
    {
        try
        {
            if (IsRegisteredAnywhere && !IsRegistered) Register();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    // ---------------------------------------------------------------- handing a link to the tool already running

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Hands a link to the copy of the tool already running; false when none is (or it doesn't answer).</summary>
    public static bool SendToRunning(string link)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            // Windows gave this copy the right to come to the front; let the running one have it, to bring the browser up
            AllowSetForegroundWindow(-1);
            byte[] bytes = Encoding.UTF8.GetBytes(link);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for links handed over by copies of the tool started with one, calling <paramref name="received"/> with
    /// each, until the tool closes. Only the first copy running listens.
    /// </summary>
    public static void Listen(Action<string> received)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // another copy listens already
                    return;
                }
                await using (pipe)
                    try
                    {
                        await pipe.WaitForConnectionAsync();
                        using var reader = new StreamReader(pipe, Encoding.UTF8);
                        var buffer = new char[4096];
                        int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                        if (read > 0) received(new string(buffer, 0, read));
                    }
                    catch (IOException)
                    {
                        // a copy that went before it was done; wait for the next
                    }
            }
        });
    }
}
