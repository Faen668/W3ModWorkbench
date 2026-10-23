// loc-ignore-start
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// Reads each kind of a Wwise bank's objects (its HIRC section) into a tree of named values, from bank version 72
/// (2013) to 154. The layouts were worked out and checked against the game's own banks and older mods' banks: every
/// object has to end exactly where its size says. What changed between versions is kept in <see cref="BankFormat"/>.
/// </summary>
internal static partial class BankObjectReader
{
    /// <summary>The object kinds, numbered as banks from 127 on number them.</summary>
    private enum Kind
    {
        State = 1, Sound, Action, Event, RandomSequence, Switch, ActorMixer, Bus, Blend, MusicSegment, MusicTrack, MusicSwitch,
        MusicPlaylist, Attenuation, DialogueEvent, EffectShareSet, Effect, AuxBus, Lfo, Envelope, AudioDevice, TimeModulator,
    }

    /// <summary>Wwise's class for a kind, the name an object's tree has.</summary>
    public static string ClassOf(byte type, BankFormat f)
    {
        if (f.OldTypeNumbers && type == 0x10) return "CAkFeedbackBus";
        if (f.OldTypeNumbers && type == 0x11) return "CAkFeedbackNode";
        return (Kind)BankTerms.ModernType(type, f) switch
        {
            Kind.State => "CAkState", Kind.Sound => "CAkSound", Kind.Action => "CAkAction", Kind.Event => "CAkEvent",
            Kind.RandomSequence => "CAkRanSeqCntr", Kind.Switch => "CAkSwitchCntr", Kind.ActorMixer => "CAkActorMixer", Kind.Bus => "CAkBus",
            Kind.Blend => "CAkLayerCntr", Kind.MusicSegment => "CAkMusicSegment", Kind.MusicTrack => "CAkMusicTrack",
            Kind.MusicSwitch => "CAkMusicSwitchCntr", Kind.MusicPlaylist => "CAkMusicRanSeqCntr", Kind.Attenuation => "CAkAttenuation",
            Kind.DialogueEvent => "CAkDialogueEvent", Kind.EffectShareSet => "CAkFxShareSet", Kind.Effect => "CAkFxCustom",
            Kind.AuxBus => "CAkAuxBus", Kind.Lfo => "CAkLFOModulator", Kind.Envelope => "CAkEnvelopeModulator",
            Kind.AudioDevice => "CAkAudioDevice", Kind.TimeModulator => "CAkTimeModulator",
            _ => $"Type{type}",
        };
    }

    /// <summary>
    /// Reads one object (its type and size already read) under <paramref name="item"/>, never past its end. What's left
    /// unread is kept as "unread"; a reading that runs into trouble is noted on the item.
    /// </summary>
    public static void Read(BankField item, byte[] data, int at, int size, byte type, BankFormat f)
    {
        var r = new BankCursor(data, f.Version, f.Feedback) { Position = at, Limit = at + size };
        var o = new BankNode(r, item);
        try
        {
            Object(o, type, f, r);
        }
        catch (BankReadException ex)
        {
            item.Error = ex.Message;
        }
        if (r.Position < at + size)
        {
            r.Limit = at + size;
            o.Gap("unread", at + size - r.Position);
        }
    }

    private static void Object(BankNode o, byte type, BankFormat f, BankCursor r)
    {
        if (f.OldTypeNumbers && type == 0x10)
        {
            Bus(o, f);
            return;
        }
        if (f.OldTypeNumbers && type == 0x11)
        {
            FeedbackSound(o, f);
            return;
        }
        switch ((Kind)BankTerms.ModernType(type, f))
        {
            case Kind.State: State(o, f); break;
            case Kind.Sound: Sound(o, f); break;
            case Kind.Action: Action(o, f); break;
            case Kind.Event: Event(o, f); break;
            case Kind.RandomSequence: RandomSequence(o, f); break;
            case Kind.Switch: Switch(o, f); break;
            case Kind.ActorMixer: ActorMixer(o, f); break;
            case Kind.Bus or Kind.AuxBus: Bus(o, f); break;
            case Kind.Blend: Blend(o, f); break;
            case Kind.MusicSegment: MusicSegment(o, f); break;
            case Kind.MusicTrack: MusicTrack(o, f); break;
            case Kind.MusicSwitch: MusicSwitch(o, f, r); break;
            case Kind.MusicPlaylist: MusicPlaylist(o, f); break;
            case Kind.Attenuation: Attenuation(o, f); break;
            case Kind.DialogueEvent: DialogueEvent(o, f, r); break;
            case Kind.EffectShareSet: o.Tid("id").Fnv(BankHash.Effect); EffectSettings(o, f); break;
            case Kind.Effect: o.Tid("id"); EffectSettings(o, f); break;
            case Kind.Lfo or Kind.Envelope or Kind.TimeModulator: Modulator(o, f); break;
            case Kind.AudioDevice: AudioDevice(o, f); break;
        }
    }

    // ---------------------------------------------------------------- states, sounds, events, actions

    private static void State(BankNode o, BankFormat f)
    {
        o.Tid("id");
        StateValues(o, f);
    }

    private static void Sound(BankNode o, BankFormat f)
    {
        o.Tid("id");
        Source(o.Node("source"), f);
        NodeSettings(o, f);
    }

    private static void FeedbackSound(BankNode o, BankFormat f)
    {
        o.Tid("id");
        long count = o.U32("sourceCount").Long;
        foreach (var s in o.List("sources", "source", count))
        {
            s.U16("company");
            s.U16("device");
            s.F32("volumeOffset");
            Source(s, f);
        }
        NodeSettings(o, f);
    }

    private static void Event(BankNode o, BankFormat f)
    {
        o.Tid("id").Fnv(BankHash.Event);
        if (f.EventLimits)
        {
            o.U8("flags");
            o.U16("maxInstances");
            o.F32("cooldown");
        }
        long count = f.ActionListCompact ? o.Var("actionCount").Long : o.U32("actionCount").Long;
        var a = o.Node("actions");
        for (int i = 0; i < count; i++) a.Tid("action");
    }

    /// <summary>
    /// An action: its type (top byte the kind, low byte what it applies to), its target, its properties (delay,
    /// transition), then what that kind of action keeps.
    /// </summary>
    private static void Action(BankNode o, BankFormat f)
    {
        o.Tid("id");
        long type = o.U16("type").Long;
        o.Field.Children[^1].Meaning = BankTerms.Action(type, f.Version);
        o.Tid("target");
        o.U8("targetFlags").Bit("isBus", 0);
        Properties(o, f);
        Randomised(o, f);
        long kind = (type >> 8) & 0xFF;
        if (f.ActionCodes150 && kind is 0x1A or 0x1B) return; // break, trigger
        switch (kind)
        {
            case 0x01: Fade(o); o.U8Flags(f.Version > 122, "stopFlags", ("applyToTransitions", 1), ("applyToDynamicSequence", 2)); Exceptions(o, f); break;
            case 0x02: Fade(o); o.U8Flags(true, "pauseFlags", ("includePendingResume", 0), ("applyToTransitions", 1), ("applyToDynamicSequence", 2)); Exceptions(o, f); break;
            case 0x03: Fade(o); o.U8Flags(true, "resumeFlags", ("masterResume", 0), ("applyToTransitions", 1), ("applyToDynamicSequence", 2)); Exceptions(o, f); break;
            case 0x22: Fade(o); Exceptions(o, f); break; // reset playlist
            case 0x04 or 0x05 or 0x23: // play, play and continue, play (custom)
                Fade(o);
                o.Tid("bank").Fnv(BankHash.Bank);
                if (f.PlayNamesBankType) o.U32("bankKind").Meaning = BankTerms.BankKind(o.Last);
                break;
            case 0x06 or 0x07: Fade(o); Exceptions(o, f); break; // mute, unmute
            case >= 0x08 and <= 0x0F or 0x20 or 0x30: // set or reset a property
                Fade(o);
                SetValue(o, f, gameParameter: false);
                Exceptions(o, f);
                break;
            case 0x12:
                o.Tid("group").Fnv(BankHash.Variable);
                o.Tid("state").Fnv(BankHash.Value);
                break;
            case 0x13 or 0x14: // set or reset a game parameter
                Fade(o);
                SetValue(o, f, gameParameter: true);
                Exceptions(o, f);
                break;
            case 0x19:
                o.Tid("group").Fnv(BankHash.Variable);
                o.Tid("switch").Fnv(BankHash.Value);
                break;
            case 0x1A or 0x1B or >= 0x33 and <= 0x37: // effect bypasses
                o.U8("bypass");
                o.U8(f.Version <= 145 ? "slots" : "slot");
                Exceptions(o, f);
                break;
            case 0x1E: // seek
                o.U8("relativeToLength");
                o.F32("position");
                o.F32("positionMin");
                o.F32("positionMax");
                o.U8("snapToMarker");
                Exceptions(o, f);
                break;
            case 0x31 or 0x32: // set or reset an effect
                o.U8("onDevice");
                o.U8("slot");
                o.Tid("effect").Fnv(BankHash.Effect);
                o.U8("shared");
                Exceptions(o, f);
                break;
                // enable/disable state, events, duck, break, trigger, release, play event: nothing more
        }
    }

    private static void Fade(BankNode o) => o.U8("fade").Bit("curve", 0, 0x1F).Children[^1].Meaning = BankTerms.Curve(o.Last & 0x1F);

    /// <summary>The value a set-property or set-game-parameter action applies: how it's meant, then a base and its range.</summary>
    private static void SetValue(BankNode o, BankFormat f, bool gameParameter)
    {
        var v = o.Node("value");
        if (gameParameter && f.Version > 89) v.U8("bypassTransition");
        v.U8("meaning").Meaning = BankTerms.ValueMeaning(v.Last);
        v.F32("value");
        v.F32("min");
        v.F32("max");
    }

    /// <summary>The objects an action leaves alone.</summary>
    private static void Exceptions(BankNode o, BankFormat f)
    {
        var e = o.Node("exceptions");
        long count = f.ExceptionCountCompact ? e.Var("count").Long : e.U32("count").Long;
        foreach (var x in e.List("objects", "object", count))
        {
            x.Tid("id");
            x.U8("isBus");
        }
    }

    // ---------------------------------------------------------------- containers

    private static void RandomSequence(BankNode o, BankFormat f)
    {
        o.Tid("id");
        NodeSettings(o, f);
        var c = o.Node("container");
        c.U16("loopCount");
        if (f.LoopRange)
        {
            c.U16("loopMin");
            c.U16("loopMax");
        }
        c.F32("transitionTime");
        c.F32("transitionMin");
        c.F32("transitionMax");
        c.U16("avoidRepeats");
        c.U8("transition").Meaning = BankTerms.Transition(c.Last);
        c.U8("randomMode").Meaning = BankTerms.RandomMode(c.Last);
        c.U8("mode").Meaning = BankTerms.ContainerMode(c.Last);
        if (f.PackedFlags)
            c.U8("flags").Bit("weighted", 0).Bit("resetEachPlay", 1).Bit("restartBackward", 2).Bit("continuous", 3).Bit("global", 4);
        else
        {
            c.U8("weighted");
            c.U8("resetEachPlay");
            c.U8("restartBackward");
            c.U8("continuous");
            c.U8("global");
        }
        Children(o);
        var p = o.Node("playlist");
        long count = p.U16("count").Long;
        foreach (var item in p.List("items", "item", count))
        {
            item.Tid("plays");
            item.S32("weight");
        }
    }

    private static void Switch(BankNode o, BankFormat f)
    {
        o.Tid("id");
        NodeSettings(o, f);
        var s = o.Node("switch");
        (f.SwitchModeWide ? s.U32("groupKind") : s.U8("groupKind")).Meaning = BankTerms.GroupKind(s.Last);
        s.Tid("group").Fnv(BankHash.Variable);
        s.Tid("default").Fnv(BankHash.Value);
        s.U8("continuousValidation");
        Children(o);
        long switches = o.U32("switchCount").Long;
        foreach (var sw in o.List("switches", "switch", switches))
        {
            sw.Tid("value").Fnv(BankHash.Value);
            long count = sw.U32("count").Long;
            for (int i = 0; i < count; i++) sw.Tid("plays");
        }
        long settings = o.U32("childSettingsCount").Long;
        foreach (var cs in o.List("childSettings", "child", settings))
        {
            cs.Tid("child");
            if (f.SwitchModeWide)
            {
                cs.U8("firstOnly");
                cs.U8("continuePlaying");
                cs.U32("onSwitch").Meaning = BankTerms.OnSwitch(cs.Last);
            }
            else
            {
                cs.U8("flags").Bit("firstOnly", 0).Bit("continuePlaying", 1);
                if (f.SwitchKeepsMode) cs.U8("onSwitch").Bit("mode", 0, 7).Meaning = BankTerms.OnSwitch(cs.Last & 7);
            }
            cs.S32("fadeOut");
            cs.S32("fadeIn");
        }
    }

    private static void ActorMixer(BankNode o, BankFormat f)
    {
        o.Tid("id");
        NodeSettings(o, f);
        Children(o);
    }

    private static void Blend(BankNode o, BankFormat f)
    {
        o.Tid("id");
        NodeSettings(o, f);
        Children(o);
        long layers = o.U32("layerCount").Long;
        foreach (var layer in o.List("layers", "layer", layers))
        {
            layer.Tid("layer");
            Curves(layer, f);
            layer.Tid("crossfadeParameter").Fnv(BankHash.GameParameter);
            if (f.CurveKindAndBlend) layer.U8("parameterKind").Meaning = BankTerms.CurveKind(f.Version, layer.Last);
            long children = layer.U32("childCount").Long;
            foreach (var child in layer.List("children", "child", children))
            {
                child.Tid("child");
                Points(child, child.U32("pointCount").Long);
            }
        }
        if (f.BlendContinuousCheck) o.U8("continuousValidation");
    }

    // ---------------------------------------------------------------- buses

    private static void Bus(BankNode o, BankFormat f)
    {
        o.Tid("id").Fnv(BankHash.Bus);
        long parent = o.Tid("parentBus").Fnv(BankHash.Bus).Long;
        if (f.Version > 126 && parent == 0) o.Tid("device").Fnv(BankHash.Effect);
        var b = o.Node("bus");
        Properties(b, f);
        if (f.Version > 122)
        {
            Positioning(b, f);
            Sends(b, f);
        }
        if (f.Version <= 77)
        {
            b.U8("killNewest");
            b.U8("useVirtualVoice");
            b.U16("maxInstances");
            b.U8("overrideLimit");
            b.U16("channels");
            b.U8("unused1");
            b.U8("unused2");
        }
        else if (f.Version <= 89)
        {
            b.U8("positioning");
            b.U8("panner");
            b.U8("killNewest");
            b.U8("useVirtualVoice");
            b.U16("maxInstances");
            b.U8("overrideLimit");
            b.U16("channels");
            b.U8("unused1");
            b.U8("unused2");
            b.U8("hdr");
            b.U8("hdrExponentialRelease");
        }
        else
        {
            if (f.Version <= 122)
            {
                b.U8("flags").Bit("mainOutput", 0).Bit("backgroundMusic", 1);
                b.U8("limitFlags").Bit("killNewest", 0).Bit("useVirtualVoice", 1);
            }
            else b.U8("flags").Bit("killNewest", 0).Bit("useVirtualVoice", 1).Bit("ignoreParentLimit", 2).Bit("backgroundMusic", 3);
            b.U16("maxInstances");
            var ch = b.U32("channels");
            ch.Bit("count", 0, 0xFF).Bit("config", 8, 0xF).Bit("mask", 12, 0xFFFFF);
            ch.Children[^2].Meaning = BankTerms.ChannelConfig((ch.Long >> 8) & 0xF);
            b.U8("hdrFlags").Bit("hdr", 0).Bit("exponentialRelease", 1);
        }
        o.S32("recoveryTime");
        o.F32("maxDuckVolume");
        long ducks = o.U32("duckCount").Long;
        foreach (var d in o.List("ducks", "duck", ducks))
        {
            d.Tid("bus").Fnv(BankHash.Bus);
            d.F32("volume");
            d.S32("fadeOut");
            d.S32("fadeIn");
            d.U8("curve").Meaning = BankTerms.Curve(d.Last);
            d.U8("property").Meaning = BankTerms.Property(f.Version, d.Last);
        }
        var e = o.Node("effects");
        if (f.BusEffectCountFirst)
        {
            long count = e.U8("count").Long;
            if (count > 0)
            {
                e.U8("bypassed");
                foreach (var slot in e.List("slots", "effect", count))
                {
                    slot.U8("slot");
                    slot.Tid("effect").Fnv(BankHash.Effect);
                    slot.U8("shared");
                    slot.U8("rendered");
                }
            }
        }
        else
        {
            long count = e.U8("count").Long;
            if (count > 0)
            {
                e.U8(f.EffectFlagsPacked ? "bypassAll" : "bypassed");
                foreach (var slot in e.List("slots", "effect", count))
                {
                    slot.U8("slot");
                    slot.Tid("effect").Fnv(BankHash.Effect);
                    if (f.EffectFlagsPacked) slot.U8("flags").Bit("bypassed", 0).Bit("shared", 1);
                    else
                    {
                        slot.U8("shared");
                        slot.U8("rendered");
                    }
                }
            }
        }
        if (f.BusSecondEffect)
        {
            e.Tid("mixer").Fnv(BankHash.Effect);
            e.U8("mixerShared");
        }
        if (f.AttachmentOverride) o.U8("overrideAttachments");
        if (f.MetadataSlots) Slots(o.Node("metadata"), f, withOverride: false);
        Curves(o, f);
        States(o, f);
        if (f.Feedback) o.Tid("feedbackBus").Fnv(BankHash.Bus);
    }

    // ---------------------------------------------------------------- music

    /// <summary>What every music object keeps: MIDI flags, a node's settings, children, the meter and stingers.</summary>
    private static void MusicNode(BankNode o, BankFormat f)
    {
        if (f.MidiFlags) o.U8("midiFlags").Bit("overrideTempo", 1).Bit("overrideTarget", 2).Bit("targetIsBus", 3);
        NodeSettings(o, f);
        Children(o);
        var m = o.Node("meter");
        m.D64("gridPeriod");
        m.D64("gridOffset");
        m.F32("tempo");
        m.U8("beatsPerBar");
        m.U8("beatValue");
        o.U8("overrideMeter");
        long stingers = o.U32("stingerCount").Long;
        foreach (var s in o.List("stingers", "stinger", stingers))
        {
            s.Tid("trigger").Fnv(BankHash.Trigger);
            s.Tid("segment");
            s.U32("playAt").Meaning = BankTerms.Sync(s.Last);
            s.U32("cueFilter");
            s.S32("dontRepeatFor");
            s.U32("lookAheadSegments");
        }
    }

    private static void MusicSegment(BankNode o, BankFormat f)
    {
        o.Tid("id");
        MusicNode(o, f);
        o.D64("duration");
        long markers = o.U32("markerCount").Long;
        foreach (var m in o.List("markers", "marker", markers))
        {
            m.Tid("id");
            m.D64("position");
            if (f.MarkerNamesSized)
            {
                long length = m.U32("nameLength").Long;
                if (length > 0) m.Str("name", length);
            }
            else m.Stz("name");
        }
    }

    private static void MusicTrack(BankNode o, BankFormat f)
    {
        o.Tid("id");
        if (f.TrackFlagsFirst) o.U8("midiFlags").Bit("overrideTempo", 1).Bit("overrideTarget", 2).Bit("targetIsBus", 3);
        long sources = o.U32("sourceCount").Long;
        foreach (var s in o.List("sources", "source", sources)) Source(s, f);
        if (f.TrackFlagsAfterSources) o.U8("midiFlags").Bit("overrideTempo", 1).Bit("overrideTarget", 2).Bit("targetIsBus", 3);
        long clips = o.U32("clipCount").Long;
        if (clips > 0)
        {
            foreach (var c in o.List("clips", "clip", clips))
            {
                c.U32("subTrack");
                c.Tid("sourceId");
                if (f.TrackCacheIds) c.U32("cacheId");
                if (f.TrackEventIds) c.Tid("event").Fnv(BankHash.Event);
                c.D64("playAt");
                c.D64("trimStart");
                c.D64("trimEnd");
                c.D64("sourceLength");
            }
            o.U32("subTrackCount");
        }
        long automations = o.U32("automationCount").Long;
        foreach (var a in o.List("automations", "automation", automations))
        {
            a.U32("clip");
            a.U32("kind").Meaning = BankTerms.ClipAutomation(f.Version, a.Last);
            Points(a, a.U32("pointCount").Long);
        }
        NodeSettings(o, f);
        if (!f.TrackTypeByte) o.U32("kind").Meaning = BankTerms.TrackKind(o.Last);
        else
        {
            long kind = o.U8("kind").Long;
            o.Field.Children[^1].Meaning = BankTerms.TrackKind(kind);
            if (kind == 3)
            {
                var s = o.Node("switch");
                s.U8("groupKind").Meaning = BankTerms.GroupKind(s.Last);
                s.Tid("group").Fnv(BankHash.Variable);
                s.Tid("default").Fnv(BankHash.Value);
                long count = s.U32("count").Long;
                for (int i = 0; i < count; i++) s.Tid("value").Fnv(BankHash.Value);
                var t = o.Node("switchTransition");
                Fade(t.Node("fadeOut"));
                t.U32("sync").Meaning = BankTerms.Sync(t.Last);
                t.U32("cueFilter");
                Fade(t.Node("fadeIn"));
            }
        }
        o.S32("lookAhead");

        static void Fade(BankNode n)
        {
            n.S32("time");
            n.U32("curve").Meaning = BankTerms.Curve(n.Last);
            n.S32("offset");
        }
    }

    /// <summary>A music object with transition rules: a music switch or playlist.</summary>
    private static void MusicTransitions(BankNode o, BankFormat f)
    {
        MusicNode(o, f);
        long rules = o.U32("ruleCount").Long;
        foreach (var rule in o.List("rules", "rule", rules))
        {
            long from = f.TransitionCounts ? rule.U32("fromCount").Long : 1;
            for (int i = 0; i < from; i++) rule.Tid("from");
            long to = f.TransitionCounts ? rule.U32("toCount").Long : 1;
            for (int i = 0; i < to; i++) rule.Tid("to");
            var exit = rule.Node("exit");
            Fade(exit);
            exit.U32("sync").Meaning = BankTerms.Sync(exit.Last);
            exit.U32(f.TransitionCounts ? "cueFilter" : "marker");
            exit.U8("playPostExit");
            var entry = rule.Node("entry");
            Fade(entry);
            entry.U32(f.TransitionCounts ? "cueFilter" : "marker");
            entry.Tid("jumpTo");
            if (f.JumpToType) entry.U16("jumpToKind").Meaning = BankTerms.JumpTo(entry.Last);
            entry.U16("entryAt").Meaning = BankTerms.EntryAt(entry.Last);
            entry.U8("playPreEntry");
            entry.U8("matchCueName");
            // up to 72 the transition segment is always there, with a flag saying whether it's used; later only when flagged
            if (f.TransitionCounts)
            {
                if (rule.U8("hasTransitionSegment").Long == 0) continue;
            }
            else rule.U8("transitionSegmentOn");
            var t = rule.Node("transitionSegment");
            t.Tid("segment");
            Fade(t.Node("fadeIn"));
            Fade(t.Node("fadeOut"));
            t.U8("playPreEntry");
            t.U8("playPostExit");
        }

        static void Fade(BankNode n)
        {
            n.S32("time");
            n.U32("curve").Meaning = BankTerms.Curve(n.Last);
            n.S32("offset");
        }
    }

    private static void MusicSwitch(BankNode o, BankFormat f, BankCursor r)
    {
        o.Tid("id");
        MusicTransitions(o, f);
        if (f.OldSwitchAssociations)
        {
            o.U32("groupKind").Meaning = BankTerms.GroupKind(o.Last);
            o.Tid("group").Fnv(BankHash.Variable);
            o.Tid("default").Fnv(BankHash.Value);
            o.U8("continuousValidation");
            long count = o.U32("associationCount").Long;
            foreach (var a in o.List("associations", "association", count))
            {
                a.Tid("value").Fnv(BankHash.Value);
                a.Tid("plays");
            }
            return;
        }
        o.U8("continuePlaying");
        long depth = o.U32("depth").Long;
        Arguments(o, depth, kinds: true);
        long size = o.U32("treeSize").Long;
        o.U8("mode").Meaning = BankTerms.TreeMode(o.Last);
        DecisionTree(o, depth, size, r);
    }

    private static void MusicPlaylist(BankNode o, BankFormat f)
    {
        o.Tid("id");
        MusicTransitions(o, f);
        o.U32("itemCount");
        PlaylistItems(o, f, 1);
    }

    /// <summary>A playlist's items, each with its own children, nested.</summary>
    private static void PlaylistItems(BankNode o, BankFormat f, long count)
    {
        foreach (var item in o.List("items", "item", count))
        {
            item.Tid("segment");
            item.Tid("itemId");
            long children = item.U32("childCount").Long;
            var kind = item.U32("kind");
            kind.Meaning = BankTerms.PlaylistKind(kind.Long);
            item.S16("loopCount");
            if (f.PlaylistLoopRange)
            {
                item.S16("loopMin");
                item.S16("loopMax");
            }
            item.U32("weight");
            item.U16("avoidRepeats");
            item.U8("weighted");
            item.U8("shuffle");
            PlaylistItems(item, f, children);
        }
    }

    // ---------------------------------------------------------------- decision trees

    /// <summary>The switch or state groups a decision tree goes by, then (after 72) whether each is a switch or a state.</summary>
    private static void Arguments(BankNode o, long depth, bool kinds)
    {
        var a = o.Node("arguments");
        var items = a.List("groups", "group", depth);
        foreach (var item in items) item.Tid("group").Fnv(BankHash.Variable);
        if (kinds)
            foreach (var item in items) item.U8("kind").Meaning = BankTerms.GroupKind(item.Last);
    }

    /// <summary>
    /// A decision tree: a flat list of twelve-byte nodes (a key, then either where its children start in the list and
    /// how many there are, or the object it plays; a weight and a probability). Which nodes have children is worked
    /// out by walking from the first node through each node's child range; the list is then read in order.
    /// </summary>
    private static void DecisionTree(BankNode o, long depth, long size, BankCursor r)
    {
        const int NodeSize = 12;
        if (size < 0 || r.Position + size > r.Limit) throw new BankReadException("decision tree runs past its object");
        int start = r.Position, count = (int)(size / NodeSize);
        var parentOf = new int[count];
        var depthOf = new int[count];
        Array.Fill(parentOf, -1);
        Array.Fill(depthOf, -1);
        var branch = new bool[count];
        if (count > 0) depthOf[0] = 0;
        var queue = new Queue<int>();
        if (count > 0) queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (depthOf[i] >= depth) continue;
            int at = start + i * NodeSize + 4;
            int first = BitConverter.ToUInt16(r.Data, at), many = BitConverter.ToUInt16(r.Data, at + 2);
            // a node has children when its range points further along the list at nodes no other node claimed
            bool fits = many > 0 && first > i && first + many <= count && Enumerable.Range(first, many).All(c => depthOf[c] < 0);
            if (!fits) continue;
            branch[i] = true;
            for (int c = first; c < first + many; c++)
            {
                depthOf[c] = depthOf[i] + 1;
                parentOf[c] = i;
                queue.Enqueue(c);
            }
        }
        var t = o.Node("tree");
        foreach (var (node, i) in t.List("nodes", "node", count).Select((n, i) => (n, i)))
        {
            node.Tid("key").Fnv(BankHash.Value);
            if (branch[i])
            {
                node.U16("childrenAt");
                node.U16("childCount");
            }
            else node.Tid("plays");
            node.U16("weight");
            node.U16("probability");
        }
        if (r.Position < start + size) t.Gap("rest", start + size - r.Position);
        // every node hangs from the first one; any that doesn't means the tree's shape wasn't worked out
        int unplaced = depthOf.Count(d => d < 0);
        if (unplaced > 0) t.Field.Error = $"{unplaced} of {count} nodes not reached from the first";
    }

    // ---------------------------------------------------------------- attenuation and dialogue

    private static void Attenuation(BankNode o, BankFormat f)
    {
        o.Tid("id");
        if (f.HeightSpread) o.U8("heightSpread");
        if ((o.U8("cone").Long & 1) != 0)
        {
            var c = o.Node("coneSettings");
            c.F32("insideAngle");
            c.F32("outsideAngle");
            c.F32("outsideVolume");
            c.F32("lowPass");
            if (f.ConeHighPass) c.F32("highPass");
        }
        var uses = o.Node("curveFor");
        for (int i = 0; i < f.AttenuationCurveSlots; i++) uses.S8($"slot{i}");
        long count = o.U8("curveCount").Long;
        foreach (var c in o.List("distanceCurves", "curve", count))
        {
            c.U8("scaling").Meaning = BankTerms.CurveScaling(c.Last);
            Points(c, c.U16("pointCount").Long);
        }
        Curves(o, f);
    }

    private static void DialogueEvent(BankNode o, BankFormat f, BankCursor r)
    {
        o.Tid("id").Fnv(BankHash.Event);
        if (f.Version > 72) o.U8("probability");
        long depth = o.U32("depth").Long;
        Arguments(o, depth, kinds: f.Version > 72);
        long size = o.U32("treeSize").Long;
        if (f.Version <= 72) o.U8("probability");
        o.U8("mode").Meaning = BankTerms.TreeMode(o.Last);
        DecisionTree(o, depth, size, r);
        if (f.Version > 118)
        {
            Properties(o, f);
            Randomised(o, f);
        }
    }

    // ---------------------------------------------------------------- effects, modulators, devices

    /// <summary>An effect's plugin and settings, the sounds it uses, its curves, and its starting values or states.</summary>
    private static void EffectSettings(BankNode o, BankFormat f)
    {
        var e = o.Node("effect");
        long plugin = Plugin(e, "plugin");
        if (plugin != 0 && plugin != -1) PluginSettings(e, plugin);
        long media = e.U8("mediaCount").Long;
        foreach (var m in e.List("media", "sound", media))
        {
            m.U8("index");
            m.Tid("sourceId");
        }
        Curves(e, f, CurveTargets.Effect);
        if (f.EffectInitValues)
        {
            if (f.Version > 122)
            {
                e.U8("unused1");
                e.U8("unused2");
            }
            long count = e.U16("startCount").Long;
            foreach (var v in e.List("startValues", "value", count))
            {
                if (f.Version <= 113) v.U8("parameter");
                else v.Var("parameter");
                v.F32("value");
            }
        }
        else if (f.EffectStatesAndValues)
        {
            States(e, f);
            long count = e.U16("valueCount").Long;
            foreach (var v in e.List("values", "value", count))
            {
                v.Var("parameter");
                v.U8("blend").Meaning = BankTerms.Blend(f.Version, v.Last);
                v.F32("value");
            }
        }
    }

    private static void Modulator(BankNode o, BankFormat f)
    {
        o.Tid("id");
        var m = o.Node("modulator");
        Properties(m, f, modulator: true);
        Randomised(m, f, modulator: true);
        Curves(m, f, CurveTargets.Modulator);
    }

    private static void AudioDevice(BankNode o, BankFormat f)
    {
        o.Tid("id").Fnv(BankHash.Effect);
        EffectSettings(o, f);
        if (f.DeviceSlots)
        {
            var e = o.Node("effects");
            long count = e.U8("count").Long;
            if (count == 0) return;
            e.U8(f.EffectFlagsPacked ? "bypassAll" : "bypassed");
            foreach (var slot in e.List("slots", "effect", count))
            {
                slot.U8("slot");
                slot.Tid("effect").Fnv(BankHash.Effect);
                if (f.EffectFlagsPacked) slot.U8("flags").Bit("bypassed", 0).Bit("shared", 1);
                else
                {
                    slot.U8("shared");
                    slot.U8("rendered");
                }
            }
        }
    }
}

internal static class BankNodeFlags
{
    /// <summary>A byte of flags when <paramref name="present"/>, its named bits as parts of it.</summary>
    public static void U8Flags(this BankNode o, bool present, string name, params (string Name, int Bit)[] bits)
    {
        if (!present) return;
        var f = o.U8(name);
        foreach (var (bit, at) in bits) f.Bit(bit, at);
    }
}
// loc-ignore-end
