// loc-ignore-start
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>The parts many kinds of object share: properties, a source, a node's settings, positioning, states, curves.</summary>
internal static partial class BankObjectReader
{
    // ---------------------------------------------------------------- properties

    /// <summary>The properties an object sets: how many, their numbers, then their values (a number, or an object's ID).</summary>
    private static void Properties(BankNode o, BankFormat f, bool modulator = false)
    {
        var p = o.Node("properties");
        p.U8("count");
        var items = p.List("set", "property", p.Last);
        var names = new List<string?>();
        foreach (var item in items)
        {
            long id = item.U8("property").Long;
            string? name = modulator ? BankTerms.ModulatorSetting(f.Version, id) : BankTerms.Property(f.Version, id);
            item.Field.Children[^1].Meaning = name;
            names.Add(name);
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (BankTerms.PropertyIsId(names[i])) items[i].Tid("value");
            else Number(items[i], "value");
        }
    }

    /// <summary>The properties an object randomises: their numbers, then each one's lowest and highest change.</summary>
    private static void Randomised(BankNode o, BankFormat f, bool modulator = false)
    {
        var p = o.Node("randomised");
        p.U8("count");
        var items = p.List("set", "property", p.Last);
        foreach (var item in items)
        {
            long id = item.U8("property").Long;
            item.Field.Children[^1].Meaning = modulator ? BankTerms.ModulatorSetting(f.Version, id) : BankTerms.Property(f.Version, id);
        }
        foreach (var item in items)
        {
            Number(item, "min");
            Number(item, "max");
        }
    }

    /// <summary>
    /// A property's value: four bytes that are a float for most properties and a whole number for some (counts,
    /// curves, priorities). Read as a float when its exponent is one a real setting has, else as a whole number.
    /// </summary>
    private static BankField Number(BankNode o, string name)
    {
        var raw = o.U32(name);
        uint bits = unchecked((uint)raw.Long);
        int exponent = (int)((bits >> 23) & 0xFF);
        if (bits != 0 && exponent is >= 90 and <= 170) raw.Value = BitConverter.UInt32BitsToSingle(bits);
        else raw.Value = (long)(int)bits;
        return raw;
    }

    // ---------------------------------------------------------------- a source

    /// <summary>
    /// Where a sound's audio is and how it's coded: the codec plugin, whether it's in a bank or streamed, the audio's ID,
    /// where it's kept and how big it is, some flags, and a source plugin's settings.
    /// </summary>
    private static void Source(BankNode o, BankFormat f)
    {
        long plugin = Plugin(o, "plugin");
        long storage = f.WideStorage ? o.U32("storage").Long : o.U8("storage").Long;
        o.Field.Children[^1].Meaning = BankTerms.Storage(f, storage);
        o.Tid("sourceId");
        bool inBank = storage != f.StreamedCode;
        if (f.SourceNamesItsFile)
        {
            o.Tid("fileId").Fnv(BankHash.Bank);
            if (f.SizeOnlyWhenKept)
            {
                if (inBank)
                {
                    o.U32("fileOffset");
                    o.U32("size");
                }
            }
            else
            {
                if (inBank) o.U32("fileOffset");
                o.U32("size");
            }
        }
        else
        {
            if (f.SourceCacheId) o.U32("cacheId");
            o.U32("size");
        }
        var flags = o.U8("flags");
        if (f.OldSourceFlags) flags.Bit("languageSpecific", 0).Bit("hasSource", 1).Bit("external", 2);
        else flags.Bit("languageSpecific", 0).Bit("prefetch", 1).Bit("notCached", 3).Bit("hasSource", 7);
        if (plugin > 0 && f.SourceHasSettings(plugin & 0xF)) PluginSettings(o, plugin);
    }

    /// <summary>A plugin's number, named, with its kind as a part of it.</summary>
    private static long Plugin(BankNode o, string name)
    {
        var field = o.U32(name);
        long id = field.Long;
        field.Meaning = BankTerms.Plugin(id);
        if (id > 0) field.Bit("kind", 0, 0xF).Children[^1].Meaning = BankTerms.PluginKind(id);
        return id;
    }

    /// <summary>A plugin's settings: their size, then the settings, as named values for Wwise's own plugins, else bytes.</summary>
    private static void PluginSettings(BankNode o, long plugin)
    {
        long size = o.U32("settingsSize").Long;
        if (size <= 0) return;
        if (BankEffects.Known(plugin, size))
        {
            var s = o.Node("settings");
            s.Within(size, () => BankEffects.Read(s, plugin, size));
        }
        else o.Gap("settings", size);
    }

    // ---------------------------------------------------------------- a node's settings

    /// <summary>Which settings a kind of node keeps beyond the ones all nodes share.</summary>
    private enum NodeKind { Sound, Container, Music }

    /// <summary>
    /// The settings every sound, container, actor-mixer and music object keeps: its effects, where it's routed, its
    /// properties, positioning, sends, limits, states and game parameter curves.
    /// </summary>
    private static void NodeSettings(BankNode o, BankFormat f)
    {
        var s = o.Node("settings");
        NodeEffects(s, f);
        if (f.MetadataSlots) Slots(s.Node("metadata"), f, withOverride: true);
        if (f.AttachmentOverride) s.U8("overrideAttachments");
        s.Tid("outputBus").Fnv(BankHash.Bus);
        s.Tid("parent");
        if (f.PackedFlags)
            s.U8("priorityFlags").Bit("overrideParent", 0).Bit("useDistance", 1).Bit("overrideMidiEvents", 2).Bit("overrideMidiNotes", 3)
                .Bit("trackMidiNotes", 4).Bit("breakLoopOnNoteOff", 5);
        else
        {
            s.U8("priorityOverridesParent");
            s.U8("priorityUsesDistance");
        }
        Properties(s, f);
        Randomised(s, f);
        Positioning(s, f);
        Sends(s, f);
        Limits(s, f);
        States(s, f);
        Curves(s, f);
        if (f.Feedback) s.Tid("feedbackBus").Fnv(BankHash.Bus);
    }

    /// <summary>A node's effects: whether it overrides its parent's, then each effect in its slot.</summary>
    private static void NodeEffects(BankNode o, BankFormat f)
    {
        var e = o.Node("effects");
        e.U8("overrideParent");
        long count = e.U8("count").Long;
        if (count == 0) return;
        e.U8(f.EffectFlagsPacked ? "bypassAll" : "bypassed");
        foreach (var slot in e.List("slots", "effect", count))
        {
            slot.U8("slot");
            slot.Tid("effect").Fnv(BankHash.Effect);
            if (f.EffectFlagsPacked) slot.U8("flags").Bit("bypassed", 0).Bit("shared", 1).Bit("rendered", 2);
            else
            {
                slot.U8("shared");
                slot.U8("rendered");
            }
        }
    }

    /// <summary>Effect or metadata slots of the later layout: a count, then each slot's effect.</summary>
    private static void Slots(BankNode o, BankFormat f, bool withOverride)
    {
        if (withOverride) o.U8("overrideParent");
        long count = o.U8("count").Long;
        foreach (var slot in o.List("slots", "slot", count))
        {
            slot.U8("slot");
            slot.Tid("effect").Fnv(BankHash.Effect);
            slot.U8("shared");
        }
    }

    // ---------------------------------------------------------------- positioning

    private static void Positioning(BankNode o, BankFormat f)
    {
        var p = o.Node("positioning");
        var flags = p.U8("flags");
        long bits = flags.Long;
        flags.Bit("overrideParent", 0);
        bool overrides = (bits & 1) != 0;
        bool is3d = false;
        switch (f.Positioning)
        {
            case BankFormat.PositionLayout.Separate2d3d:
                flags.Bit("listenerRelative", 1);
                if (overrides)
                {
                    if (f.Version <= 72)
                    {
                        is3d = p.U8("is3d").Long != 0;
                        if (!is3d) p.U8("panner");
                    }
                    else
                    {
                        bool is2d = p.U8("is2d").Long != 0;
                        is3d = p.U8("is3d").Long != 0;
                        if (is2d) p.U8("panner");
                    }
                }
                break;
            case BankFormat.PositionLayout.Bits112:
                flags.Bit("listenerRelative", 1).Bit("is3d", 3);
                is3d = ((bits >> 3) & 1) != 0;
                break;
            case BankFormat.PositionLayout.Bits122:
                flags.Bit("is3d", 3);
                is3d = ((bits >> 3) & 1) != 0;
                break;
            case BankFormat.PositionLayout.Bits129:
                flags.Bit("is3d", 4);
                is3d = ((bits >> 4) & 1) != 0;
                break;
            default:
                flags.Bit("is3d", 1).Bit("panner", 2, 3).Bit("position3d", 5, 3);
                flags.Children[^2].Meaning = BankTerms.Panner((bits >> 2) & 3);
                flags.Children[^1].Meaning = BankTerms.Position3d((bits >> 5) & 3);
                is3d = ((bits >> 1) & 1) != 0;
                break;
        }
        if (!overrides || !is3d) return;

        bool automated, dynamic = false;
        if (f.Positioning == BankFormat.PositionLayout.Separate2d3d)
        {
            var kind = p.U32("kind");
            kind.Meaning = BankTerms.Positioning89(kind.Long & 3);
            p.Tid("attenuation");
            p.U8("spatialised");
            long k = f.Version <= 72 ? kind.Long : kind.Long & 3;
            automated = f.Version <= 72 ? k == 2 : k != 1;
            dynamic = f.Version <= 72 ? k == 3 : !automated;
        }
        else
        {
            var b = p.U8("flags3d");
            long b3 = b.Long;
            b.Bit("spatialisation", 0, f.Version <= 126 ? 1 : 3);
            b.Children[^1].Meaning = BankTerms.Spatialisation(b3 & (f.Version <= 126 ? 1 : 3));
            if (f.Version <= 132) b.Bit("holdEmitter", 3).Bit("holdListener", 4).Bit("looping", 5);
            else b.Bit("attenuationOn", 3).Bit("holdEmitter", 4).Bit("holdListener", 5).Bit("looping", 6);
            if (f.Version > 134) b.Bit("diffraction", 7);
            if (f.PositionAttenuationId) p.Tid("attenuation");
            automated = f.Positioning switch
            {
                BankFormat.PositionLayout.Bits112 or BankFormat.PositionLayout.Bits122 => (b3 & 3) != 1,
                BankFormat.PositionLayout.Bits129 when f.Version <= 126 => ((b3 >> 4) & 1) != 1,
                BankFormat.PositionLayout.Bits129 => ((b3 >> 6) & 1) != 1,
                _ => ((bits >> 5) & 3) != 0,
            };
        }
        if (dynamic) p.U8("dynamic");
        if (!automated) return;

        var path = p.Node("path");
        if (f.Positioning == BankFormat.PositionLayout.Separate2d3d)
        {
            path.U32("mode").Meaning = BankTerms.PathMode(path.Last);
            path.U8("looping");
            path.S32("transitionTime");
            path.U8("followOrientation");
        }
        else
        {
            path.U8("mode").Meaning = BankTerms.PathMode(path.Last);
            path.S32("transitionTime");
        }
        long points = path.U32("pointCount").Long;
        foreach (var point in path.List("points", "point", points))
        {
            point.F32("x");
            point.F32("y");
            point.F32("z");
            point.S32("duration");
        }
        long paths = path.U32("pathCount").Long;
        foreach (var item in path.List("paths", "path", paths))
        {
            item.U32("firstPoint");
            item.U32("points");
        }
        foreach (var item in path.List("ranges", "range", paths))
        {
            item.F32("x");
            item.F32("y");
            if (f.PathZRange) item.F32("z");
        }
    }

    // ---------------------------------------------------------------- sends

    private static void Sends(BankNode o, BankFormat f)
    {
        var s = o.Node("sends");
        bool hasUserSends;
        if (f.UserSendFlagsPacked)
        {
            var flags = s.U8("flags");
            flags.Bit("overrideUserSends", 2).Bit("hasUserSends", 3).Bit("overrideReflections", 4);
            hasUserSends = ((flags.Long >> 3) & 1) != 0;
        }
        else
        {
            s.U8("overrideGameSends");
            s.U8("useGameSends");
            s.U8("overrideUserSends");
            hasUserSends = s.U8("hasUserSends").Long != 0;
        }
        if (hasUserSends)
            for (int i = 1; i <= 4; i++) s.Tid($"userSend{i}").Fnv(BankHash.Bus);
        if (f.ReflectionsBus) s.Tid("reflectionsBus").Fnv(BankHash.Bus);
    }

    // ---------------------------------------------------------------- limits

    private static void Limits(BankNode o, BankFormat f)
    {
        var l = o.Node("limits");
        if (f.LimitFlagsPacked)
        {
            l.U8("flags").Bit("killNewest", 0).Bit("useVirtualVoice", 1).Bit("ignoreParentLimit", 3).Bit("overrideVirtualVoice", 4);
            l.U8("virtualResume").Meaning = BankTerms.VirtualResume(l.Last);
            l.U16("maxInstances");
            l.U8("belowThreshold").Meaning = BankTerms.BelowThreshold(l.Last);
            l.U8("hdrFlags").Bit("overrideEnvelope", 0).Bit("overrideAnalysis", 1).Bit("normaliseLoudness", 2).Bit("enableEnvelope", 3);
            return;
        }
        l.U8("virtualResume").Meaning = BankTerms.VirtualResume(l.Last);
        l.U8("killNewest");
        l.U8("useVirtualVoice");
        l.U16("maxInstances");
        l.U8("globalLimit");
        l.U8("belowThreshold").Meaning = BankTerms.BelowThreshold(l.Last);
        l.U8("overrideLimit");
        l.U8("overrideVirtualVoice");
        if (f.HdrEnvelopeFlags)
        {
            l.U8("overrideEnvelope");
            l.U8("overrideAnalysis");
            l.U8("normaliseLoudness");
            l.U8("enableEnvelope");
        }
    }

    // ---------------------------------------------------------------- states

    /// <summary>
    /// The state groups that change this object, each state with its values: up to 122 as plain counts with each state
    /// pointing at a State object; later the properties the states change come first, with variable-length counts.
    /// </summary>
    private static void States(BankNode o, BankFormat f)
    {
        var s = o.Node("states");
        if (!f.CompactStates)
        {
            long groups = s.U32("groupCount").Long;
            foreach (var g in s.List("groups", "group", groups))
            {
                g.Tid("group").Fnv(BankHash.Variable);
                g.U8("sync").Meaning = BankTerms.Sync(g.Last);
                long states = g.U16("stateCount").Long;
                foreach (var st in g.List("states", "state", states))
                {
                    st.Tid("state").Fnv(BankHash.Value);
                    st.Tid("stateObject");
                }
            }
            return;
        }
        long props = s.Var("propertyCount").Long;
        foreach (var p in s.List("changes", "property", props))
        {
            p.Var("property").Meaning = BankTerms.Target(f.Version, p.Last);
            p.U8("blend").Meaning = BankTerms.Blend(f.Version, p.Last);
            if (f.StatePropertyInDb) p.U8("inDb");
        }
        long groupCount = s.Var("groupCount").Long;
        foreach (var g in s.List("groups", "group", groupCount))
        {
            g.Tid("group").Fnv(BankHash.Variable);
            if (f.EventLimits) g.Tid("usage").Fnv(BankHash.Variable);
            g.U8("sync").Meaning = BankTerms.Sync(g.Last);
            long states = g.Var("stateCount").Long;
            foreach (var st in g.List("states", "state", states))
            {
                st.Tid("state").Fnv(BankHash.Value);
                if (f.StateValuesInline) StateValues(st, f);
                else st.Tid("stateObject");
            }
        }
    }

    /// <summary>The values a state sets: how many, the properties, then the values.</summary>
    private static void StateValues(BankNode o, BankFormat f)
    {
        var v = o.Node("values");
        long count = f.WideStateValues ? v.U16("count").Long : v.U8("count").Long;
        var items = v.List("set", "value", count);
        foreach (var item in items)
            (f.WideStateValues ? item.U16("property") : item.U8("property")).Meaning = BankTerms.Target(f.Version, item.Last);
        foreach (var item in items) item.F32("value");
    }

    // ---------------------------------------------------------------- game parameter curves

    /// <summary>What a curve's target names: a node's properties, a modulator's own settings, or an effect's (plugin-numbered).</summary>
    private enum CurveTargets { Node, Modulator, Effect }

    /// <summary>The curves by which game parameters (and MIDI, switches, states, modulators) change this object's settings.</summary>
    private static void Curves(BankNode o, BankFormat f, CurveTargets targets = CurveTargets.Node)
    {
        var c = o.Node("curves");
        long count = c.U16("count").Long;
        foreach (var curve in c.List("set", "curve", count))
        {
            curve.Tid("parameter").Fnv(BankHash.GameParameter);
            if (f.CurveKindAndBlend)
            {
                curve.U8("kind").Meaning = BankTerms.CurveKind(f.Version, curve.Last);
                curve.U8("blend").Meaning = BankTerms.Blend(f.Version, curve.Last);
            }
            var target = f.CurveTargetWidth switch { 4 => curve.U32("changes"), 1 => curve.U8("changes"), _ => curve.Var("changes") };
            target.Meaning = targets switch
            {
                CurveTargets.Node => BankTerms.Target(f.Version, target.Long),
                CurveTargets.Modulator => BankTerms.ModulatorTargets.GetValueOrDefault(target.Long),
                _ => null,
            };
            curve.Tid("curveId");
            curve.U8("scaling").Meaning = BankTerms.CurveScaling(curve.Last);
            Points(curve, curve.U16("pointCount").Long);
        }
    }

    /// <summary>A curve's points: where on the game parameter, the value there, and the shape to the next point.</summary>
    private static void Points(BankNode o, long count, string name = "points")
    {
        foreach (var point in o.List(name, "point", count))
        {
            point.F32("x");
            point.F32("y");
            point.U32("shape").Meaning = BankTerms.Curve(point.Last);
        }
    }

    // ---------------------------------------------------------------- children

    private static void Children(BankNode o)
    {
        var c = o.Node("children");
        long count = c.U32("count").Long;
        for (int i = 0; i < count; i++) c.Tid("child");
    }
}
// loc-ignore-end
