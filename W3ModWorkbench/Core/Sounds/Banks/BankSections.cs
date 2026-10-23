// loc-ignore-start
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// A bank's sections other than its objects: its header (BKHD), the sounds it keeps (DIDX), the banks it names (STID),
/// and, in a project's Init.bnk, the project's global settings (STMG: volume threshold, voice limits, state groups with
/// their transition times, switch groups driven by game parameters, game parameters' starting values and ramping,
/// acoustic textures), its obstruction and occlusion curves (ENVS), the plugins it uses (INIT) and its platform
/// (PLAT). Each is read to its exact size, checked on the game's own Init.bnk.
/// </summary>
internal static class BankSections
{
    /// <summary>Reads one section's body under <paramref name="section"/>, which must take all of it.</summary>
    /// <summary>Writes a section's body from its tree in <paramref name="f"/>'s layout (the reading, run the other way).</summary>
    public static byte[] Write(BankField source, string tag, BankFormat f)
    {
        var output = new List<byte>();
        var r = new BankCursor([], f.Version, f.Feedback, output);
        var o = new BankNode(r, new BankField(tag, "node", null, 0, 0), source);
        Body(o, tag, f, 0);
        foreach (string rest in new[] { "padding", "unread" })
            if (source.Find(rest) is { Value: byte[] bytes }) o.Gap(rest, bytes.Length);
        return [.. output];
    }

    private static void Body(BankNode o, string tag, BankFormat f, int size)
    {
        switch (tag)
        {
            case "BKHD": Header(o, f, size); break;
            case "DIDX": MediaIndex(o, o.Writing ? o.Source!.Find("sounds")!.Children.Count * 12 : size); break;
            case "STID": BankNames(o); break;
            case "STMG": GlobalSettings(o, f); break;
            case "ENVS": ObstructionCurves(o, f); break;
            case "INIT": Plugins(o, f); break;
            case "PLAT": Platform(o, f); break;
        }
    }

    public static void Read(BankField section, string tag, byte[] data, int at, int size, BankFormat f)
    {
        var r = new BankCursor(data, f.Version, f.Feedback) { Position = at, Limit = at + size };
        var o = new BankNode(r, section);
        try
        {
            if (tag == "DATA") o.Gap("sounds", size);
            else Body(o, tag, f, size);
        }
        catch (BankReadException ex)
        {
            section.Error = ex.Message;
        }
        if (r.Position < at + size)
        {
            r.Limit = at + size;
            o.Gap(tag == "BKHD" ? "padding" : "unread", at + size - r.Position);
        }
    }

    private static void Header(BankNode o, BankFormat f, int size)
    {
        o.U32("version");
        o.Tid("id").Fnv(BankHash.Bank);
        if (f.Version <= 122) o.U32("language");
        else o.Tid("language").Fnv(BankHash.Language);
        if (f.Version <= 126) o.U32("feedback").Bit("hasFeedback", 0);
        else o.U32("alignment").Bit("alignment", 0, 0xFFFF).Bit("deviceAllocated", 16, 0xFFFF);
        if (f.Version > 76) o.U32("projectId");
        if (f.Version > 141)
        {
            o.U32("bankKind").Meaning = BankTerms.BankKind(o.Last);
            o.Gap("hash", 16);
        }
    }

    private static void MediaIndex(BankNode o, int size)
    {
        foreach (var m in o.List("sounds", "sound", size / 12))
        {
            m.Tid("id");
            m.U32("offset");
            m.U32("size");
        }
    }

    private static void BankNames(BankNode o)
    {
        o.U32("kind");
        long count = o.U32("count").Long;
        foreach (var b in o.List("banks", "bank", count))
        {
            b.Tid("id").Fnv(BankHash.Bank);
            long length = b.U8("length").Long;
            b.Str("name", length);
        }
    }

    private static void GlobalSettings(BankNode o, BankFormat f)
    {
        if (f.Version > 140) o.U16("filterBehaviour").Meaning = o.Last == 1 ? "Maximum" : "Additive";
        o.F32("volumeThreshold").Unit = "dB";
        o.U16("maxVoices");
        if (f.Version > 126) o.U16("maxVirtualVoices");

        long groups = o.U32("stateGroupCount").Long;
        foreach (var g in o.List("stateGroups", "group", groups))
        {
            g.Tid("group").Fnv(BankHash.Variable);
            g.U32("defaultTransition").Unit = "ms";
            long transitions = g.U32("transitionCount").Long;
            foreach (var t in g.List("transitions", "transition", transitions))
            {
                t.Tid("from").Fnv(BankHash.Value);
                t.Tid("to").Fnv(BankHash.Value);
                t.U32("time").Unit = "ms";
            }
        }

        long switches = o.U32("switchGroupCount").Long;
        foreach (var s in o.List("switchGroups", "group", switches))
        {
            s.Tid("group").Fnv(BankHash.Variable);
            s.Tid("parameter").Fnv(BankHash.GameParameter);
            if (f.Version > 89) s.U8("parameterKind").Meaning = BankTerms.CurveKind(f.Version, s.Last);
            long points = s.U32("pointCount").Long;
            foreach (var p in s.List("points", "point", points))
            {
                p.F32("x");
                p.F32("switch");
                p.U32("shape").Meaning = BankTerms.Curve(p.Last);
            }
        }

        long parameters = o.U32("parameterCount").Long;
        foreach (var p in o.List("parameters", "parameter", parameters))
        {
            p.Tid("parameter").Fnv(BankHash.GameParameter);
            p.F32("default");
            if (f.Version > 89)
            {
                p.U32("ramping").Meaning = p.Last switch { 1 => "Slew Rate", 2 => "Filtering over Time", _ => "None" };
                p.F32("rampUp");
                p.F32("rampDown");
                p.U8("builtIn").Meaning = BuiltIn(f, p.Last);
            }
        }

        if (f.Version > 122)
        {
            long textures = o.U32("textureCount").Long;
            foreach (var t in o.List("acousticTextures", "texture", textures))
            {
                t.Tid("texture");
                t.F32("absorptionOffset");
                t.F32("absorptionLow");
                t.F32("absorptionMidLow");
                t.F32("absorptionMidHigh");
                t.F32("absorptionHigh");
                t.F32("scattering");
            }
        }
    }

    private static string BuiltIn(BankFormat f, long id) => id switch
    {
        0 => "None", 1 => "Distance", 2 => "Azimuth", 3 => "Elevation", 4 => f.Version <= 126 ? "Object Angle" : "Emitter Cone",
        5 => "Obstruction", 6 => "Occlusion", 7 => "Listener Cone", 8 => "Diffraction", 9 => "Transmission Loss", _ => $"{id}",
    };

    /// <summary>The curves by which obstruction and occlusion (and later diffraction and transmission) lower volume and filter.</summary>
    private static void ObstructionCurves(BankNode o, BankFormat f)
    {
        string[] from = f.Version <= 150 ? ["Obstruction", "Occlusion"] : ["Obstruction", "Occlusion", "Diffraction", "Transmission"];
        string[] to = f.Version <= 89 ? ["Volume", "Low-pass"] : ["Volume", "Low-pass", "High-pass"];
        foreach (string x in from)
            foreach (string y in to)
            {
                var c = o.Node($"{x} to {y}");
                c.U8("on");
                c.U8("scaling").Meaning = BankTerms.CurveScaling(c.Last);
                long points = c.U16("pointCount").Long;
                foreach (var p in c.List("points", "point", points))
                {
                    p.F32("x");
                    p.F32("y");
                    p.U32("shape").Meaning = BankTerms.Curve(p.Last);
                }
            }
    }

    private static void Plugins(BankNode o, BankFormat f)
    {
        long count = o.U32("count").Long;
        foreach (var p in o.List("plugins", "plugin", count))
        {
            var id = p.U32("plugin");
            id.Meaning = BankTerms.Plugin(id.Long);
            if (f.Version <= 136)
            {
                long length = p.U32("libraryLength").Long;
                if (length > 0) p.Str("library", length);
            }
            else p.Stz("library");
        }
    }

    private static void Platform(BankNode o, BankFormat f)
    {
        if (f.Version <= 136)
        {
            long length = o.U32("length").Long;
            o.Str("platform", length);
        }
        else o.Stz("platform");
    }
}
// loc-ignore-end
