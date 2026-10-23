// loc-ignore-start
using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>The parts many objects share, upgraded: a source, a node's settings, properties, positioning, states, curves.</summary>
internal static partial class BankUpgrade
{
    private sealed partial class Upgrader
    {
        // ------------------------------------------------------------ a source

        /// <summary>
        /// A source in 150's layout. 88 numbered streamed 1 and prefetched 2, which 150 swaps; it named the file
        /// keeping the sound and where in it, which 150 doesn't (the bank's index says); and its flags were language,
        /// has source and external, where 150's are language, prefetch, not cached and has source. A streamed sound's
        /// size, which 88 hasn't, is as much of its .wem as the game would prefetch. A streamed sound whose .wem is the
        /// game's, which the Remastered re-encoded in Opus, is said to be Opus (as the pairs' 6,291 such sounds are).
        /// </summary>
        private BankField Source(BankField s)
        {
            if (!V88) return Copy(s);
            long plugin = s.Find("plugin")!.Long, storage = s.Find("storage")!.Long, id = s.Find("sourceId")!.Long;
            long flags = s.Find("flags")!.Long;
            long size = s.Find("size")?.Long ?? 0;
            long newStorage = storage switch { 1 => 2, 2 => 1, _ => 0 };
            if (newStorage != 0)
            {
                byte[]? start = null;
                if (wemOf((uint)id) is { } wem)
                {
                    try
                    {
                        if (WemInfo.Read(wem).Codec == 0x3041) plugin = OpusPlugin;
                        start = WemUpgrade.Prefetch(WemUpgrade.Upgrade(wem));
                    }
                    catch (InvalidDataException) { }
                }
                if (start is not null) size = start.Length;
                else if (newStorage == 2) NoteId("BankUpgrade.StreamedNotFound", id);
            }
            var n = Node(s.Name, Value("plugin", plugin), Value("storage", newStorage), Copy(s.Find("sourceId")!), Value("size", size),
                // language specific stays bit 0; has source moves from bit 1 to 7
                Value("flags", (flags & 1) | ((flags >> 1) & 1) << 7));
            if (s.Find("settingsSize") is { } ss) n.Children.Add(Copy(ss));
            if (s.Find("settings") is { } settings) n.Children.Add(Copy(settings));
            return n;
        }

        // ------------------------------------------------------------ a node's settings

        /// <summary>
        /// The settings every sound, container and music object keeps, in 150's layout. Where 88 kept a yes/no a byte
        /// each, 150 packs them as bits; the pairs show which bit each became.
        /// </summary>
        private BankField NodeSettings(BankField s)
        {
            var positioning = Positioning(s.Find("positioning")!, out long? attenuation);
            return Node("settings",
                Effects(s.Find("effects")!),
                s.Find("metadata") is { } m ? Copy(m) : Node("metadata", Value("overrideParent", 0L), Value("count", 0L), Node("slots")),
                Copy(s.Find("outputBus")!),
                Copy(s.Find("parent")!),
                s.Find("priorityFlags") is { } pf ? Copy(pf) : Value("priorityFlags", Bit(s, "priorityOverridesParent", 0) | Bit(s, "priorityUsesDistance", 1)),
                Properties(s.Find("properties")!, attenuation),
                Properties(s.Find("randomised")!, null),
                positioning,
                Sends(s.Find("sends")!),
                Limits(s.Find("limits")!),
                States(s.Find("states")!, nodeProperties: true),
                Curves(s.Find("curves")!));
        }

        /// <summary>
        /// A node's effects. Up to 145 a byte says which slots are bypassed (bits 0 to 3, and 4 for all of them) and
        /// each slot has a byte each for shared and rendered; 150 keeps "bypass all" on its own and each slot's three as
        /// bits of one byte.
        /// </summary>
        private static BankField Effects(BankField e)
        {
            var n = Node("effects", Copy(e.Find("overrideParent")!), Copy(e.Find("count")!));
            if (e.Find("count")!.Long == 0) return n;
            long bypassed = e.Find("bypassed")?.Long ?? 0;
            n.Children.Add(Value("bypassAll", (bypassed >> 4) & 1));
            var slots = new List<BankField>();
            foreach (var slot in e.Find("slots")!.Children)
            {
                long index = slot.Find("slot")!.Long;
                long flags = ((bypassed >> (int)index) & 1) | Bit(slot, "shared", 1) | Bit(slot, "rendered", 2);
                slots.Add(Node("effect", Copy(slot.Find("slot")!), Copy(slot.Find("effect")!), Value("flags", flags)));
            }
            n.Children.Add(Node("slots", slots.ToArray()));
            return n;
        }

        /// <summary>
        /// Properties (or randomised ones) renumbered by name for 150, in order of their numbers as 150's are. One
        /// 150 has no such property is left out and noted. An attenuation the old positioning named becomes the
        /// Attenuation property, as the pairs' 464 positioned objects show.
        /// </summary>
        private BankField Properties(BankField p, long? attenuation)
        {
            var items = new List<(long Id, BankField Item)>();
            foreach (var item in p.Find("set")!.Children)
            {
                var number = item.Find("property")!;
                string? name = BankTerms.Property(_f.Version, number.Long);
                if (BankTerms.Property150(name) is not { } id)
                {
                    Note(L.T("BankUpgrade.PropertyDropped", name ?? $"{number.Long}"));
                    continue;
                }
                var c = Copy(item);
                c.Find("property")!.Value = id;
                items.Add((id, c));
            }
            if (attenuation is { } att && att != 0 && BankTerms.Property150("Attenuation") is { } attId)
                items.Add((attId, Node("property", Value("property", attId), Value("value", att))));
            var sorted = items.OrderBy(i => i.Id).Select(i => i.Item).ToArray();
            return Node(p.Name, Value("count", (long)sorted.Length), Node("set", sorted));
        }

        // ------------------------------------------------------------ positioning

        /// <summary>
        /// Positioning in 150's layout. 88 kept 2D and 3D apart; 150 has every overriding object 3D (bit 1). From the
        /// pairs' 464 overriding objects: a 2D one is 3D without spatialisation; one the game places is spatialised by
        /// position and orientation; one on a path is placed relative to the listener with automation (position 2),
        /// its path looping as before and the listener's orientation held unless it followed it. Attenuation is on in
        /// all of them; the attenuation itself becomes a property.
        /// </summary>
        private BankField Positioning(BankField p, out long? attenuation)
        {
            attenuation = null;
            if (!V88) return Copy(p);
            long flags = p.Find("flags")!.Long;
            if ((flags & 1) == 0) return Node("positioning", Value("flags", 0L));
            bool is3d = (p.Find("is3d")?.Long ?? 0) != 0;
            if (!is3d)
            {
                long panner = p.Find("panner")?.Long ?? 0;
                return Node("positioning", Value("flags", 1L | 2 | (panner & 3) << 2), Value("flags3d", 8L));
            }
            attenuation = p.Find("attenuation")?.Long;
            long spatialisation = (p.Find("spatialised")?.Long ?? 0) != 0 ? 2 : 0;
            long kind = p.Find("kind")!.Long & 3;
            if (kind == 1) return Node("positioning", Value("flags", 3L), Value("flags3d", spatialisation | 8));
            var path = p.Find("path")!;
            bool looping = (path.Find("looping")?.Long ?? 0) != 0, follows = (path.Find("followOrientation")?.Long ?? 0) != 0;
            long flags3d = spatialisation | 8 | (follows ? 0 : 1L << 5) | (looping ? 1L << 6 : 0);
            var ranges = Copy(path.Find("ranges")!);
            foreach (var range in ranges.Children) range.Children.Add(Value("z", 0f));
            var newPath = Node("path", Copy(path.Find("mode")!), Copy(path.Find("transitionTime")!), Copy(path.Find("pointCount")!),
                Copy(path.Find("points")!), Copy(path.Find("pathCount")!), Copy(path.Find("paths")!), ranges);
            return Node("positioning", Value("flags", 3L | 2L << 5), Value("flags3d", flags3d), newPath);
        }

        // ------------------------------------------------------------ sends and limits

        /// <summary>Sends: 88's four yes/no bytes as bits 0 to 3 of one, and the reflections bus (none) 150 adds.</summary>
        private BankField Sends(BankField s)
        {
            if (!V88) return Copy(s);
            long flags = Bit(s, "overrideGameSends", 0) | Bit(s, "useGameSends", 1) | Bit(s, "overrideUserSends", 2) | Bit(s, "hasUserSends", 3);
            var n = Node("sends", Value("flags", flags));
            for (int i = 1; i <= 4; i++)
                if (s.Find($"userSend{i}") is { } send) n.Children.Add(Copy(send));
            n.Children.Add(Value("reflectionsBus", 0L));
            return n;
        }

        /// <summary>Limits: 88's yes/no bytes as bits of two (the voice limit's, and the loudness envelope's).</summary>
        private BankField Limits(BankField l)
        {
            if (!V88) return Copy(l);
            long flags = Bit(l, "killNewest", 0) | Bit(l, "useVirtualVoice", 1) | Bit(l, "globalLimit", 2) | Bit(l, "overrideLimit", 3) | Bit(l, "overrideVirtualVoice", 4);
            long hdr = Bit(l, "overrideEnvelope", 0) | Bit(l, "overrideAnalysis", 1) | Bit(l, "normaliseLoudness", 2) | Bit(l, "enableEnvelope", 3);
            return Node("limits", Value("flags", flags), Copy(l.Find("virtualResume")!), Copy(l.Find("maxInstances")!), Copy(l.Find("belowThreshold")!), Value("hdrFlags", hdr));
        }

        // ------------------------------------------------------------ states

        /// <summary>
        /// The properties 150 lists as changed by states, for a node with any state group: the same five on every one of
        /// the game's (volume and make-up gain added in dB, pitch added, the filters as filters).
        /// </summary>
        private static readonly (string Name, long Blend, long InDb)[] StateChanges =
            [("Volume", 2, 1), ("Pitch", 2, 0), ("Low-pass Filter", 6, 0), ("High-pass Filter", 6, 0), ("Make-Up Gain", 2, 1)];

        /// <summary>
        /// State groups in 150's layout: each state holding its values rather than pointing at a State object (whose
        /// values are taken from this bank), states in order of their IDs, and the properties states change listed.
        /// A node's values are renumbered by name; an effect's are its plugin's own and stay.
        /// </summary>
        private BankField States(BankField s, bool nodeProperties)
        {
            var groups = new List<BankField>();
            foreach (var g in s.Find("groups")!.Children)
            {
                var states = new List<(long Id, BankField State)>();
                foreach (var st in g.Find("states")!.Children)
                {
                    long id = st.Find("state")!.Long;
                    BankField? values = st.Find("values");
                    if (values is null && st.Find("stateObject") is { } so)
                    {
                        if (old[(uint)so.Long] is { ClassName: "CAkState" } state) values = state.Fields.Find("values");
                        else NoteId("BankUpgrade.StateNotInBank", so.Long);
                    }
                    states.Add((id, Node("state", Copy(st.Find("state")!), StateValues(values, nodeProperties))));
                }
                var sorted = states.OrderBy(x => (uint)x.Id).Select(x => x.State).ToArray();
                groups.Add(Node("group", Copy(g.Find("group")!), Copy(g.Find("sync")!), Value("stateCount", (long)sorted.Length), Node("states", sorted)));
            }
            BankField changes;
            if (!nodeProperties) changes = s.Find("changes") is { } c ? Copy(c) : Node("changes");
            else if (groups.Count == 0) changes = Node("changes");
            else changes = Node("changes", StateChanges.Select(p => Node("property", Value("property", BankTerms.Property150(p.Name)!.Value), Value("blend", p.Blend), Value("inDb", p.InDb))).ToArray());
            return Node("states", Value("propertyCount", (long)changes.Children.Count), changes, Value("groupCount", (long)groups.Count), Node("groups", groups.ToArray()));
        }

        /// <summary>A state's values, two bytes a count and a number in 150, renumbered by name when they're a node's.</summary>
        private BankField StateValues(BankField? values, bool nodeProperties)
        {
            var items = new List<BankField>();
            foreach (var v in values?.Find("set")?.Children ?? [])
            {
                var c = Copy(v);
                if (nodeProperties)
                {
                    string? name = BankTerms.Target(_f.Version, v.Find("property")!.Long);
                    if (BankTerms.Target150(name) is not { } id)
                    {
                        Note(L.T("BankUpgrade.StateValueDropped", name ?? $"{v.Find("property")!.Long}"));
                        continue;
                    }
                    c.Find("property")!.Value = id;
                }
                items.Add(c);
            }
            return Node("values", Value("count", (long)items.Count), Node("set", items.ToArray()));
        }

        // ------------------------------------------------------------ curves

        /// <summary>
        /// Game parameter curves in 150's layout: what each changes renumbered by name (150 numbers them as properties),
        /// and for 88's, which had neither, a game parameter's curve added to the rest.
        /// </summary>
        private BankField Curves(BankField c)
        {
            var curves = new List<BankField>();
            foreach (var curve in c.Find("set")!.Children)
            {
                var x = Copy(curve);
                var changes = x.Find("changes")!;
                string? name = BankTerms.Target(_f.Version, changes.Long);
                if (BankTerms.Target150(name) is not { } id)
                {
                    Note(L.T("BankUpgrade.CurveDropped", name ?? $"{changes.Long}"));
                    continue;
                }
                changes.Value = id;
                if (x.Find("kind") is null) x.Children.Add(Value("kind", 0L));
                if (x.Find("blend") is null) x.Children.Add(Value("blend", CurveBlend(name!)));
                curves.Add(x);
            }
            return Node("curves", Value("count", (long)curves.Count), Node("set", curves.ToArray()));
        }

        /// <summary>
        /// How a curve of 150 blends with the rest, by what it changes. Each target of the game's 2,811 node curves has
        /// one blend: the filters filter, Bypass Effect is a yes/no, Max Instances and 3D Blend take one value, the rest
        /// add. A target the game has no curve for goes with its kind.
        /// </summary>
        private static long CurveBlend(string target) => target switch
        {
            _ when target.Contains("Low-pass") || target.Contains("High-pass") => 6,
            _ when target.StartsWith("Bypass", StringComparison.Ordinal) => 4,
            "Max Instances" or "3D Blend" => 1,
            _ => 2,
        };
    }
}
// loc-ignore-end
