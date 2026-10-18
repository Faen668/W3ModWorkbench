using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

/// <summary>A row of the hash window: what the value is, the value, and a note on it.</summary>
public sealed record HashRow(string Label, string Value, string? Note = null, string? Copy = null)
{
    /// <summary>What the copy button copies: the value, or one part of it.</summary>
    public string CopyText => Copy ?? Value;
}

/// <summary>
/// Small conversions modders need now and then: a name's hash in each of the game's ways of hashing one, the name behind a
/// hash (from the names in the game's scripts and the game's string keys), a number in its other forms, and a mod's block of string IDs.
/// </summary>
public partial class HashToolsWindow : Window, IMessageHost
{
    private static HashToolsWindow? _instance;

    /// <summary>Every known name by hash, for each kind of hash. Built once, in the background, the first time it's asked for.</summary>
    private static Task<Dictionary<ulong, List<(string Name, HashKind Kind)>>>? _names;
    private static int _nameCount;

    private HashToolsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TextBox.Focus();
        Closed += (_, _) => _instance = null;
    }

    public static void Open(Window owner)
    {
        if (_instance is null)
        {
            _instance = new HashToolsWindow().Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    // ---------------------------------------------------------------- hashing a name

    private void Text_Changed(object sender, TextChangedEventArgs e)
    {
        string text = TextBox.Text;
        HashList.ItemsSource = text.Length == 0 ? null : Hashes.Kinds.Select(k =>
        {
            ulong value = k.Compute(text);
            string hex = k.Bits == 64 ? $"0x{value:X16}" : $"0x{value:X8}";
            return new HashRow(k.Name, $"{hex}   ({value})", k.UsedFor, hex);
        }).ToList();
    }

    // ---------------------------------------------------------------- the name behind a hash

    private async void Lookup_Changed(object sender, TextChangedEventArgs e)
    {
        string typed = LookupBox.Text;
        if (typed.Trim().Length == 0)
        {
            LookupStatus.Text = "";
            LookupList.ItemsSource = null;
            return;
        }
        if (Hashes.ParseNumber(typed) is not { } hash)
        {
            LookupStatus.Text = "That isn't a number. Type it in decimal, or in hex with 0x in front.";
            LookupList.ItemsSource = null;
            return;
        }
        if (_names is not { IsCompleted: true })
        {
            LookupStatus.Text = "Reading the names in the game's scripts and strings…";
            LookupList.ItemsSource = null;
        }
        var names = await KnownNames();
        if (LookupBox.Text != typed) return;
        // a 32-bit hash may be typed as a negative number, or arrive with its top half set
        var found = names.GetValueOrDefault(hash) ?? (hash > uint.MaxValue ? names.GetValueOrDefault(hash & 0xFFFFFFFF) : null) ?? [];
        LookupList.ItemsSource = found.Select(f => new HashRow(f.Kind.Name, f.Name, f.Kind.UsedFor)).ToList();
        LookupStatus.Text = found.Count == 0
            ? $"None of the {_nameCount:N0} names known hashes to {hash} (0x{hash:X}). It may be a name no script or string key uses, such as a resource's."
            : found.Count == 1 ? "One name hashes to it:" : $"{found.Count} names hash to it:";
    }

    private static Task<Dictionary<ulong, List<(string Name, HashKind Kind)>>> KnownNames() =>
        _names ??= Task.Run(() =>
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            string? game = GameLocator.Resolve(null);
            if (ScriptCorpus.GameScriptsFolder(game) is { } scripts)
            {
                foreach (string path in SafeFiles(scripts))
                {
                    string text;
                    try { text = TextDecoding.Decode(File.ReadAllBytes(path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    foreach (Match m in Word().Matches(text)) words.Add(m.Value);
                    foreach (Match m in Quoted().Matches(text)) words.Add(m.Groups[1].Value);
                }
            }
            try
            {
                var language = W3Strings.Language("en") ?? W3Strings.Languages[0];
                var strings = GameStringIndex.GetAsync(game, language, dlcAndMods: game is not null).GetAwaiter().GetResult();
                foreach (var s in strings.All)
                    if (s.KeyName is { Length: > 0 } key) words.Add(key);
            }
            catch (Exception)
            {
                // without the game's strings there are still the scripts' names
            }

            _nameCount = words.Count;
            var byHash = new Dictionary<ulong, List<(string Name, HashKind Kind)>>();
            foreach (string word in words)
                foreach (var kind in Hashes.Kinds)
                {
                    ulong h = kind.Compute(word);
                    if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = [];
                    // the string key hash ignores case: one spelling is enough
                    if (!list.Any(l => l.Item2 == kind && l.Item1.Equals(word, StringComparison.OrdinalIgnoreCase))) list.Add((word, kind));
                }
            return byHash;
        });

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---------------------------------------------------------------- numbers

    private void Number_Changed(object sender, TextChangedEventArgs e)
    {
        if (Hashes.ParseNumber(NumberBox.Text) is not { } n)
        {
            NumberList.ItemsSource = NumberBox.Text.Trim().Length == 0 ? null
                : new List<HashRow> { new("", "That isn't a number. Type it in decimal, or in hex with 0x in front.") };
            return;
        }
        var rows = new List<HashRow> { new("Decimal", n.ToString()) };
        if (n <= uint.MaxValue)
        {
            uint u = (uint)n;
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, u);
            rows.Add(new("Hex", $"0x{u:X8}"));
            rows.Add(new("Signed 32-bit", ((int)u).ToString()));
            rows.Add(new("As a float", BitConverter.Int32BitsToSingle((int)u).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "The same 32 bits read as a float, as a value in a file would be"));
            rows.Add(new("Bytes in a file", Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b),
                "Little-endian, as the game stores numbers"));
        }
        else
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, n);
            rows.Add(new("Hex", $"0x{n:X16}"));
            rows.Add(new("Signed 64-bit", ((long)n).ToString()));
            rows.Add(new("Bytes in a file", Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b),
                "Little-endian, as the game stores numbers"));
        }
        if (Hashes.SplitModId(n) is { } mod)
        {
            rows.Add(new("Mod's Nexus ID", mod.NexusId.ToString(), "The four digits after 211, by the usual convention for mods' string IDs"));
            rows.Add(new("String number", mod.Index.ToString(), "The last three digits: which of the mod's 1000 strings it is"));
        }
        if (n <= uint.MaxValue)
        {
            rows.Add(new("In a script", $"GetLocStringById({n})", "Reads the string with this ID in the player's language"));
        }
        NumberList.ItemsSource = rows;
    }

    private void Nexus_Changed(object sender, TextChangedEventArgs e)
    {
        string text = NexusBox.Text.Trim();
        if (text.Length == 0)
        {
            NexusList.ItemsSource = null;
            return;
        }
        if (!int.TryParse(text, out int id) || Hashes.ModIdBlock(id) is not { } block)
        {
            NexusList.ItemsSource = new List<HashRow>
            {
                new("", int.TryParse(text, out _)
                    ? "The convention only has room for IDs up to 9999. Mods with longer IDs pick a free block of their own: Tools > Search game strings can check one."
                    : "Type the mod's ID on Nexus, a number."),
            };
            return;
        }
        NexusList.ItemsSource = new List<HashRow>
        {
            new("First ID", block.First.ToString()),
            new("Last ID", block.Last.ToString()),
        };
    }

    // ---------------------------------------------------------------- copying

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string copied } || copied.Length == 0) return;
        try
        {
            Clipboard.SetText(copied);
            _ = Message.Show(null, $"Copied {copied}", MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !Message.IsOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")]
    private static partial Regex Word();

    [GeneratedRegex(@"[""']([^""'\r\n]{1,120})[""']")]
    private static partial Regex Quoted();
}
