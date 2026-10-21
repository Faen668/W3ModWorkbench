using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Input;

/// <summary>One binding in input.settings: <c>IK_F5=(Action=QuickSave)</c> under a context such as [Exploration].</summary>
public sealed record InputBinding(string Context, string Key, string Action, string Line);

/// <summary>What a new key binding is made of, for <see cref="InputSettings.Write"/>.</summary>
/// <param name="Action">The action scripts listen for: theInput.RegisterListener(this, 'OnMyAction', 'MyAction').</param>
/// <param name="DisplayName">The string key the key bindings menu shows it under.</param>
/// <param name="Hold">Fires while the key is held, not just when it's pressed: State=Duration.</param>
public sealed record NewBinding(string Action, string Key, IReadOnlyList<string> Contexts, string DisplayName, bool Hold, string Listener);

/// <summary>
/// The game's key bindings in Documents\The Witcher 3\input.settings: one section per input context, each a list of
/// key=(Action=…) lines. A mod's own action needs a line in each context it should work in, and an entry in
/// bin\config\r4game\user_config_matrix\pc\input.xml to show in the key bindings menu.
/// </summary>
public sealed partial class InputSettings
{
    public string Path { get; }
    public IReadOnlyList<InputBinding> Bindings { get; }

    /// <summary>The contexts in the order the file has them.</summary>
    public IReadOnlyList<string> Contexts { get; }

    private InputSettings(string path, List<InputBinding> bindings, List<string> contexts)
    {
        Path = path;
        Bindings = bindings;
        Contexts = contexts;
    }

    /// <summary>The contexts most mods bind a key in: walking about, fighting, on a horse, swimming and in a boat.</summary>
    public static readonly string[] CommonContexts = ["Exploration", "Combat", "Horse", "Swimming", "Boat", "Diving"];

    /// <summary>Documents\The Witcher 3\input.settings, the file the game reads.</summary>
    public static string DefaultPath => GameDocuments.FileIn("input.settings");

    /// <summary>bin\config\r4game\user_config_matrix\pc\input.xml in the game's folder, which lists the actions the key bindings menu offers.</summary>
    public static string? MenuPath(string? gameFolder) =>
        gameFolder is null ? null : System.IO.Path.Combine(gameFolder, "bin", "config", "r4game", "user_config_matrix", "pc", "input.xml");

    public static InputSettings Read(string path)
    {
        var bindings = new List<InputBinding>();
        var contexts = new List<string>();
        string? context = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                context = line[1..^1];
                if (!contexts.Contains(context, StringComparer.OrdinalIgnoreCase)) contexts.Add(context);
            }
            else if (context is not null && Binding().Match(line) is { Success: true } m)
                bindings.Add(new InputBinding(context, m.Groups["key"].Value, m.Groups["action"].Value, line));
        }
        return new InputSettings(path, bindings, contexts);
    }

    /// <summary>The bindings already on a key in the given contexts, which a new one would share the key with.</summary>
    public List<InputBinding> OnKey(string key, IEnumerable<string> contexts)
    {
        var wanted = contexts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Bindings.Where(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && wanted.Contains(b.Context))];
    }

    /// <summary>The bindings an action already has.</summary>
    public List<InputBinding> ForAction(string action) =>
        [.. Bindings.Where(b => b.Action.Equals(action, StringComparison.OrdinalIgnoreCase))];

    // ---------------------------------------------------------------- writing

    /// <summary>The line for one context: IK_K=(Action=MyAction), with State=Duration for a key that's held.</summary>
    public static string Line(NewBinding b) => b.Hold
        ? $"{b.Key}=(Action={b.Action},State=Duration,IdleTime=0.5)"
        : $"{b.Key}=(Action={b.Action})";

    /// <summary>The input.settings lines, each under its context's heading, as a readme would list them.</summary>
    public static string Write(NewBinding b)
    {
        var sb = new StringBuilder();
        foreach (string context in b.Contexts)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"[{context}]");
            sb.AppendLine(Line(b));
        }
        return sb.ToString();
    }

    /// <summary>The input.xml entry that puts the action in the game's key bindings menu.</summary>
    public static string MenuEntry(NewBinding b) =>
        $"<Var builder=\"Input\" id=\"{b.Action}\" displayName=\"{b.DisplayName}\" displayType=\"INPUTPC\" actions=\"{b.Action}\"/>"; // loc-ignore

    /// <summary>The WitcherScript that listens for the action, to put in a class that lives as long as it's needed.</summary>
    public static string Script(NewBinding b)
    {
        string what = b.Hold ? "// held for half a second: State=Duration makes it fire once the key has been down that long" : "// what the key does"; // loc-ignore
        // loc-ignore-start: WitcherScript
        return $$"""
            // in the class's set-up, such as OnSpawned or an init function:
            theInput.RegisterListener(this, '{{b.Listener}}', '{{b.Action}}');

            // and when it's no longer wanted:
            theInput.UnregisterListener(this, '{{b.Action}}');

            event {{b.Listener}}(action : SInputAction)
            {
                if (IsPressed(action))
                {
                    {{what}}
                }
            }
            """;
        // loc-ignore-end
    }

    /// <summary>
    /// Adds the binding to input.settings, under each context's heading (a missing one is added at the end), after
    /// keeping a copy of the file as input.settings.bak. Lines it has already are left alone. Returns how many were added.
    /// </summary>
    public static int Add(string path, NewBinding b)
    {
        string text = File.ReadAllText(path);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();
        string add = Line(b);
        int added = 0;
        foreach (string context in b.Contexts)
        {
            int heading = lines.FindIndex(l => l.Trim().Equals($"[{context}]", StringComparison.OrdinalIgnoreCase));
            if (heading < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add($"[{context}]");
                lines.Add(add);
                lines.Add("");
                added++;
                continue;
            }
            int end = heading + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            if (lines.Skip(heading + 1).Take(end - heading - 1).Any(l => l.Trim().Equals(add, StringComparison.OrdinalIgnoreCase))) continue;
            // after the section's last line, before the blank line that ends it
            int at = end;
            while (at > heading + 1 && lines[at - 1].Trim().Length == 0) at--;
            lines.Insert(at, add);
            added++;
        }
        if (added == 0) return 0;
        File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, string.Join(newline, lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: text.Length > 0 && text[0] == '﻿'));
        return added;
    }

    /// <summary>
    /// Adds the menu entry to input.xml, before the end of its list of entries, after keeping a copy as input.xml.bak.
    /// False when the action has an entry already or the file has no list to add to.
    /// </summary>
    public static bool AddToMenu(string path, NewBinding b)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
        if (HasEntry(text, b.Action)) return false;
        int end = text.LastIndexOf("</VisibleVars>", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return false;
        int lineStart = text.LastIndexOf('\n', end) + 1;
        string indent = text[lineStart..end].Trim().Length == 0 ? text[lineStart..end] + "\t" : "\t\t\t";
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        text = text.Insert(lineStart, indent + MenuEntry(b) + newline);
        File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return true;
    }

    /// <summary>Whether input.xml lists the action already, so there's nothing to add.</summary>
    public static bool HasMenuEntry(string path, string action) => HasEntry(File.ReadAllText(path), action);

    private static bool HasEntry(string text, string action) =>
        Regex.IsMatch(text, $@"\bactions=""[^""]*\b{Regex.Escape(action)}\b", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"^(?<key>IK_\w+)\s*=\s*\(\s*Action\s*=\s*(?<action>\w+)")]
    private static partial Regex Binding();
}
