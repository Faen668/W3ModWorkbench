using System.Text;

namespace W3ModWorkbench.Core.Quests;

/// <summary>A quest block to watch, as the stub writes it: its GUID, class, name and the outputs it's known to fire.</summary>
public sealed record NqhWatchedBlock(Guid Guid, string ClassName, string Name, IReadOnlyList<string> Outputs, string File);

/// <summary>Which of Native Quest Hook's events the stub overrides.</summary>
[Flags]
public enum NqhEvents
{
    None = 0,
    Activated = 1,
    Finished = 2,
    Active = 4,
    Stopped = 8,
}

/// <summary>
/// A ready-to-use Native Quest Hook listener, as WitcherScript: a CNQH_Listener subclass that watches the blocks given,
/// overrides the events asked for (each a switch on the GUID, with what the block fires noted), and registers itself
/// the way Native Quest Hook asks, by wrapping NQH_RegisterListeners. GUIDs are written as the hook matches them, lower
/// case in the standard form. This is code, not the app's text: none of it is translated.
/// </summary>
public static class NqhListenerStub
{
    // loc-ignore-start

    /// <summary>A mod name made fit for a script identifier: letters, digits and underscores, starting with a letter.</summary>
    public static string Identifier(string modName)
    {
        var sb = new StringBuilder();
        foreach (char c in modName)
            if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
        string id = sb.ToString();
        if (id.Length == 0) id = "MyMod";
        if (!char.IsAsciiLetter(id[0])) id = "M" + id;
        return id;
    }

    /// <summary>The listener's class name for a mod: C, the mod's name, then _QuestListener.</summary>
    public static string ClassName(string modName) => $"C{Identifier(modName)}_QuestListener";

    /// <summary>The file it's saved as: the mod's name, then QuestListener.ws.</summary>
    public static string FileName(string modName) => $"{Identifier(modName)}QuestListener.ws";

    public static string Write(string modName, IReadOnlyList<NqhWatchedBlock> blocks, NqhEvents events)
    {
        string id = Identifier(modName);
        string cls = ClassName(modName);
        var blocksByGuid = blocks.GroupBy(b => b.Guid).Select(g => g.First()).ToList();
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append("\r\n");

        Line("//---------------------------------------------------");
        Line($"//-- {id}: quest block events");
        Line("//---------------------------------------------------");
        Line("// Made with W3 Mod Workbench's quest graph viewer. Needs Native Quest Hook (modNativeQuestHook) installed;");
        Line("// without its .asi this compiles and runs, and simply hears nothing.");
        foreach (string file in blocksByGuid.Select(b => b.File).Distinct(StringComparer.OrdinalIgnoreCase))
            Line($"// Blocks from {file}");
        Line();
        Line($"class {cls} extends CNQH_Listener");
        Line("{");
        Line($"\tpublic function Init() : {cls}");
        Line("\t{");
        foreach (var b in blocksByGuid)
            Line($"\t\twatched.PushBack(\"{Guid(b)}\");   // {b.ClassName} \"{Comment(b.Name)}\"");
        Line("\t\treturn this;");
        Line("\t}");

        void Event(NqhEvents which, string signature, string note, bool outputs)
        {
            if (!events.HasFlag(which)) return;
            Line();
            Line($"\t// {note}");
            Line($"\tpublic function {signature}");
            Line("\t{");
            Line("\t\tswitch (guid)");
            Line("\t\t{");
            foreach (var b in blocksByGuid)
            {
                string what = $"{b.ClassName} \"{Comment(b.Name)}\"";
                if (outputs && b.Outputs.Count > 0)
                    what += ": fires " + string.Join(" or ", b.Outputs.Select(o => string.IsNullOrWhiteSpace(o) || o == "None" ? "None" : $"'{o}'"));
                Line($"\t\tcase \"{Guid(b)}\":   // {what}");
                Line("\t\t\tbreak;");
            }
            Line("\t\t}");
            Line("\t}");
        }
        Event(NqhEvents.Activated, "OnBlockActivated(guid : string)", "A watched block started.", false);
        Event(NqhEvents.Finished, "OnBlockFinished(guid : string, output : name)",
            "A watched block finished and fired output: this is what moves the quest on. An unnamed output is None.", true);
        Event(NqhEvents.Active, "OnBlockActive(guid : string)", "A watched block was already running: after a load or a world change, or when this registered.", false);
        Event(NqhEvents.Stopped, "OnBlockStopped(guid : string)", "A watched block stopped without finishing: a load, leaving its world, or its quest stopping.", false);
        Line("}");
        Line();
        Line("@wrapMethod");
        Line("function NQH_RegisterListeners()");
        Line("{");
        Line("\twrappedMethod();");
        Line($"\tNQH_Register('{id}', (new {cls} in theGame).Init());");
        Line("}");
        return sb.ToString();
    }

    private static string Guid(NqhWatchedBlock b) => b.Guid.ToString("D");

    // a comment can't hold a line break, and a name can have one
    private static string Comment(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

    // loc-ignore-end
}
