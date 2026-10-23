// loc-ignore-start
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// Rewrites a bank of the original game (version 88) or of the next-gen update (140) as a bank of 150, the only
/// version the Remastered's sound engine loads. Every rule here was learnt from data, not assumed: the original game's
/// banks that old mods carry against the same banks as the Remastered ships them (39 pairs, 20,601 objects sharing
/// IDs), the game's own 740 banks and 5,789 streamed sounds for what a bank of 150 holds, and the layouts the reader
/// already proves on every bank. Where the data shows a rule the rule is followed; where a bank holds something no
/// data showed (a bus, an effect bypass action, a version other than 88 or 140) it isn't upgraded, and says why.
/// </summary>
internal static partial class BankUpgrade
{
    public const int Version = 150;

    /// <summary>The project every one of the game's 740 banks names, which banks loaded with its Init.bnk carry.</summary>
    private const uint GameProject = 39;

    /// <summary>The language the game's sound effect banks name: the hash of "sfx" (old banks wrote 0 for it).</summary>
    private const uint SfxLanguage = 0x17705D3E;

    /// <summary>The Opus codec as the game's banks number it.</summary>
    private const long OpusPlugin = 0x00140001;

    private static readonly BankFormat Target = new(Version, false);

    /// <summary>The upgraded bank (null when it couldn't be), and what was changed or left out on the way.</summary>
    public sealed record Result(byte[]? Bank, IReadOnlyList<string> Notes);

    /// <summary>
    /// Upgrades <paramref name="bank"/>. <paramref name="wemOf"/> gives a streamed or prefetched sound's .wem by its ID
    /// (the mod's own, else the game's), which a bank of 150 needs to size it; null when there's none.
    /// </summary>
    public static Result Upgrade(byte[] bank, Func<uint, byte[]?> wemOf) => Upgrade(bank, wemOf, null);

    /// <summary>
    /// Upgrades <paramref name="bank"/>, noting any bus it sends to that isn't among <paramref name="buses"/> (the
    /// game's Init.bnk's), where the Remastered would play it nowhere.
    /// </summary>
    public static Result Upgrade(byte[] bank, Func<uint, byte[]?> wemOf, IReadOnlySet<uint>? buses)
    {
        var result = UpgradeBank(bank, wemOf);
        if (result.Bank is null || buses is null) return result;
        var missing = new SortedSet<uint>();
        foreach (var o in BankObjects.Read(result.Bank).Objects)
            if (o.Fields.Find("settings") is { } s)
            {
                if (s.Find("outputBus")?.Long is long bus and > 0 && !buses.Contains((uint)bus)) missing.Add((uint)bus);
                for (int i = 1; i <= 4; i++)
                    if (s.Find("sends")?.Find($"userSend{i}")?.Long is long send and > 0 && !buses.Contains((uint)send)) missing.Add((uint)send);
            }
        if (missing.Count == 0) return result;
        return result with { Notes = [.. result.Notes, L.T("BankUpgrade.BusesMissing", string.Join(", ", missing))] };
    }

    /// <summary>The game's buses, by ID, from its Init.bnk: what upgraded banks' sounds can play through.</summary>
    public static IReadOnlySet<uint> Buses(byte[] init) =>
        BankObjects.Read(init).Objects.Where(o => o.ClassName is "CAkBus" or "CAkAuxBus").Select(o => o.Id).ToHashSet();

    private static Result UpgradeBank(byte[] bank, Func<uint, byte[]?> wemOf)
    {
        var old = BankObjects.Read(bank);
        if (old.Version == Version) return new(null, [L.T("BankUpgrade.AlreadyCurrent")]);
        if (old.Version is not (88 or 140)) return new(null, [L.T("BankUpgrade.VersionNotLearnt", old.Version)]);
        if (old.Sections.Any(s => s.Tag is "STMG" or "INIT")) return new(null, [L.T("BankUpgrade.InitBank")]);
        if (old.Problem is not null || old.Objects.FirstOrDefault(o => !o.ReadWhole) is { } bad)
            return new(null, [L.T("BankUpgrade.NotReadWhole", old.Objects.FirstOrDefault(o => !o.ReadWhole)?.Id ?? 0)]);
        return new Upgrader(old, bank, wemOf).Run();
    }

    /// <summary>One bank's upgrade: the old bank, its media, and the notes gathered.</summary>
    private sealed partial class Upgrader(BankObjects old, byte[] raw, Func<uint, byte[]?> wemOf)
    {
        private readonly BankFormat _f = old.Format;
        private bool V88 => _f.Version <= 89;
        private readonly Dictionary<string, int> _notes = [];
        private string? _refusal;

        /// <summary>A note, counted: the same note for many objects is said once with how many.</summary>
        private void Note(string text) => _notes[text] = _notes.GetValueOrDefault(text) + 1;

        /// <summary>The IDs a note is about, said once as a list (each sound or state at most once).</summary>
        private readonly Dictionary<string, SortedSet<long>> _listed = [];

        private void NoteId(string key, long id)
        {
            if (!_listed.TryGetValue(key, out var ids)) _listed[key] = ids = [];
            ids.Add(id);
        }

        /// <summary>A listed note's text: how many, then their IDs (the first twenty).</summary>
        private static string Listed(string key, SortedSet<long> ids)
        {
            string list = string.Join(", ", ids.Take(20)) + (ids.Count > 20 ? ", …" : "");
            return key switch
            {
                "BankUpgrade.StreamedNotFound" => L.T(key, L.T("Count.StreamedSound", ids.Count), list),
                "BankUpgrade.MediaKept" => L.T(key, L.T("Count.Sound", ids.Count), list),
                _ => L.T(key, list),
            };
        }

        /// <summary>The bank can't be upgraded: something in it has no rule the data showed.</summary>
        private void Refuse(string why) => _refusal ??= why;

        public Result Run()
        {
            var objects = new List<(byte Type, BankField Fields)>();
            foreach (var o in old.Objects)
            {
                var converted = Object(o);
                if (converted is { } c) objects.Add(c);
                if (_refusal is not null) return new(null, [_refusal]);
            }
            var (didx, data) = Media(objects);
            if (_refusal is not null) return new(null, [_refusal]);

            var order = new List<string> { "BKHD" };
            if (didx is not null) order.AddRange(["DIDX", "DATA"]);
            order.Add("HIRC");
            if (old.Sections.Any(s => s.Tag == "STID")) order.Add("STID");
            var sections = new List<(string, BankField)> { ("BKHD", Header(didx)) };
            if (didx is not null) sections.Add(("DIDX", didx));
            if (old.Sections.FirstOrDefault(s => s.Tag == "STID").Fields is { } stid) sections.Add(("STID", Copy(stid)));
            byte[] bank;
            try { bank = BankWriter.Write(order, sections, objects, data, Target); }
            catch (BankReadException ex) { return new(null, [L.T("BankUpgrade.DoesntWrite", ex.Message)]); }

            // the header's hash: the game's is the authoring tool's and can't be worked out from a bank, so ours is the
            // bank's own content, the same bank always giving the same
            byte[] hash = MD5.HashData(bank.AsSpan(8 + 40));
            hash.CopyTo(bank, 8 + 24);

            // what was written must read back whole as a bank of 150
            var check = BankObjects.Read(bank);
            if (check.Version != Version || check.Objects.Count != objects.Count || check.Objects.Any(o => !o.ReadWhole))
                return new(null, [L.T("BankUpgrade.DoesntReadBack")]);
            var notes = _notes.Select(n => n.Value > 1 ? L.T("BankUpgrade.NoteTimes", n.Key, n.Value) : n.Key)
                .Concat(_listed.Select(l => Listed(l.Key, l.Value))).ToList();
            return new(bank, notes);
        }

        // ------------------------------------------------------------ the header and the media

        /// <summary>
        /// The header of 150: the game's project and the sound effects language, media aligned to 16 bytes, an
        /// ordinary (user) bank, and padding so the media start on a 16-byte boundary (as each of the game's banks does).
        /// </summary>
        private BankField Header(BankField? didx)
        {
            var h = old.Sections.First(s => s.Tag == "BKHD").Fields;
            long language = h.Find("language")!.Long;
            if (V88 && language != 0) Refuse(L.T("BankUpgrade.LanguageBank"));
            int padding = 0;
            if (didx is not null)
            {
                // BKHD (8 + 40 + padding), DIDX (8 + 12 per sound), then DATA's 8: its body on a 16-byte boundary
                int sounds = didx.Find("sounds")!.Children.Count;
                padding = (16 - (8 + 40 + 8 + 12 * sounds + 8) % 16) % 16;
            }
            return Node("BKHD",
                Value("version", (long)Version),
                Copy(h.Find("id")!),
                Value("language", V88 ? (long)SfxLanguage : language),
                Value("alignment", 16L),
                Value("projectId", (long)GameProject),
                Value("bankKind", 0L),
                Bytes("hash", new byte[16]),
                padding > 0 ? Bytes("padding", new byte[padding]) : null);
        }

        /// <summary>
        /// The media the bank keeps, each moved to a 16-byte boundary (the game's banks all have them so), with its
        /// header in the layout the Remastered's engine reads; a prefetched sound's start cut again as the game cuts it.
        /// </summary>
        private (BankField? Didx, byte[]? Data) Media(List<(byte Type, BankField Fields)> objects)
        {
            if (old.Sections.FirstOrDefault(s => s.Tag == "DIDX").Fields is not { } index) return (null, null);
            var (body, _) = Section(raw, "DATA");
            if (body < 0) return (null, null);
            var prefetched = new HashSet<long>(Sources(objects).Where(s => s.Find("storage")!.Long == 1).Select(s => s.Find("sourceId")!.Long));
            var data = new List<byte>();
            var sounds = new List<BankField>();
            var sizes = new Dictionary<long, long>();
            foreach (var s in index.Find("sounds")!.Children)
            {
                long id = s.Find("id")!.Long;
                byte[] media = raw.AsSpan(body + (int)s.Find("offset")!.Long, (int)s.Find("size")!.Long).ToArray();
                if (V88) media = Wem88(media, id);
                if (V88 && prefetched.Contains(id))
                {
                    if (wemOf((uint)id) is { } whole && WemUpgrade.Prefetch(WemUpgrade.Upgrade(whole)) is { } start) media = start;
                    else Note(L.T("BankUpgrade.PrefetchKept"));
                }
                while (data.Count % 16 != 0) data.Add(0);
                sounds.Add(Node("sound", Value("id", id), Value("offset", (long)data.Count), Value("size", (long)media.Length)));
                sizes[id] = media.Length;
                data.AddRange(media);
            }
            // the sources' sizes are the media's as kept here now
            foreach (var src in Sources(objects))
                if (src.Find("storage")!.Long != 2 && sizes.TryGetValue(src.Find("sourceId")!.Long, out long size)) src.Find("size")!.Value = size;
            return (Node("DIDX", Node("sounds", sounds.ToArray())), data.ToArray());
        }

        /// <summary>A sound of 88's header rewritten as 150 reads it, noted when it can't be.</summary>
        private byte[] Wem88(byte[] media, long id)
        {
            try { return WemUpgrade.Upgrade(media); }
            catch (InvalidDataException) { NoteId("BankUpgrade.MediaKept", id); return media; }
        }

        /// <summary>Every source in the upgraded objects: sounds' and music tracks'.</summary>
        private static IEnumerable<BankField> Sources(List<(byte Type, BankField Fields)> objects) =>
            objects.SelectMany(o => o.Fields.Find("source") is { } s ? [s] : o.Fields.Find("sources")?.Children ?? []);

        private static (int Body, int Size) Section(byte[] bank, string tag)
        {
            for (int at = 0; at + 8 <= bank.Length;)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 4));
                if (Encoding.ASCII.GetString(bank, at, 4) == tag) return (at + 8, size);
                at += 8 + size;
            }
            return (-1, 0);
        }

        // ------------------------------------------------------------ objects

        /// <summary>An object in the numbering and layout of 150, or null when 150 keeps it elsewhere (a state's values).</summary>
        private (byte, BankField)? Object(BankObject o)
        {
            var x = o.Fields;
            byte type = (byte)BankTerms.ModernType(o.Type, _f);
            switch (x.Name)
            {
                case "CAkState": return null; // its values go into the objects its states change
                case "CAkSound": return (type, Node(x.Name, Copy(x.Find("id")!), Source(x.Find("source")!), NodeSettings(x.Find("settings")!)));
                case "CAkActorMixer": return (type, Node(x.Name, Copy(x.Find("id")!), NodeSettings(x.Find("settings")!), Copy(x.Find("children")!)));
                case "CAkRanSeqCntr": return (type, RandomSequence(x));
                case "CAkSwitchCntr": return (type, Switch(x));
                case "CAkLayerCntr": return (type, Blend(x));
                case "CAkEvent": return (type, Copy(x));
                case "CAkAction": return Action(x) is { } a ? (type, a) : null;
                case "CAkMusicSegment": return (type, MusicSegment(x));
                case "CAkMusicTrack": return (type, MusicTrack(x));
                case "CAkMusicSwitchCntr": return (type, MusicSwitch(x));
                case "CAkMusicRanSeqCntr": return (type, MusicPlaylist(x));
                case "CAkAttenuation": return (type, Attenuation(x));
                case "CAkFxCustom" or "CAkFxShareSet": return (type, Effect(x));
                case "CAkDialogueEvent": return (type, DialogueEvent(x));
                default:
                    // buses live in the game's Init.bnk; modulators, devices and motion feedback weren't in any bank
                    // the rules were learnt from
                    Refuse(L.T("BankUpgrade.ObjectNotLearnt", x.Name, o.Id));
                    return null;
            }
        }

        private BankField RandomSequence(BankField x)
        {
            var c = Copy(x.Find("container")!);
            if (V88)
            {
                // five yes/no bytes became bits of one (the pairs' 1,084 containers say which)
                long flags = Bit(c, "weighted", 0) | Bit(c, "resetEachPlay", 1) | Bit(c, "restartBackward", 2) | Bit(c, "continuous", 3) | Bit(c, "global", 4);
                c.Children.Add(Value("flags", flags));
            }
            return Node(x.Name, Copy(x.Find("id")!), NodeSettings(x.Find("settings")!), c, Copy(x.Find("children")!), Copy(x.Find("playlist")!));
        }

        private BankField Switch(BankField x)
        {
            var settings = Copy(x.Find("childSettings")!);
            if (V88)
                foreach (var child in settings.Children)
                    child.Children.Add(Value("flags", Bit(child, "firstOnly", 0) | Bit(child, "continuePlaying", 1)));
            return Node(x.Name, Copy(x.Find("id")!), NodeSettings(x.Find("settings")!), Copy(x.Find("switch")!), Copy(x.Find("children")!),
                Copy(x.Find("switchCount")!), Copy(x.Find("switches")!), Copy(x.Find("childSettingsCount")!), settings);
        }

        private BankField Blend(BankField x)
        {
            var layers = Copy(x.Find("layers")!);
            foreach (var layer in layers.Children)
            {
                Replace(layer, Curves(layer.Find("curves")!));
                if (V88) layer.Children.Add(Value("parameterKind", 0L));
            }
            return Node(x.Name, Copy(x.Find("id")!), NodeSettings(x.Find("settings")!), Copy(x.Find("children")!), Copy(x.Find("layerCount")!), layers,
                x.Find("continuousValidation") is { } cv ? Copy(cv) : Value("continuousValidation", 0L));
        }

        private BankField DialogueEvent(BankField x)
        {
            var d = Copy(x);
            if (d.Find("properties") is { } p) Replace(d, Properties(p, null));
            if (d.Find("randomised") is { } r) Replace(d, Properties(r, null));
            return d;
        }

        /// <summary>
        /// An action. Its properties renumbered; a stop gets the flags 150 keeps (the pairs' stops all have
        /// "apply to transitions" and "apply to dynamic sequences" on), a pause or resume those two added; a play the
        /// kind of bank it names; setting a game parameter whether it skips the transition; setting a switch or a state
        /// targets the value it sets (as each of the pairs' 1,720 switch actions does).
        /// </summary>
        private BankField? Action(BankField x)
        {
            var a = Copy(x);
            long type = a.Find("type")!.Long, kind = (type >> 8) & 0xFF;
            if (kind is 0x1A or 0x1B or 0x1C or 0x1D)
            {
                // effect bypasses, break and trigger were renumbered in 150, and no bank showed how
                Refuse(L.T("BankUpgrade.ActionNotLearnt", BankTerms.Action(type, _f.Version), a.Find("id")!.Long));
                return null;
            }
            Replace(a, Properties(a.Find("properties")!, null));
            Replace(a, Properties(a.Find("randomised")!, null));
            switch (kind)
            {
                case 0x01 when a.Find("stopFlags") is null: a.Children.Add(Value("stopFlags", 6L)); break;
                case 0x02 when V88: a.Find("pauseFlags")!.Value = a.Find("pauseFlags")!.Long | 6; break;
                case 0x03 when V88: a.Find("resumeFlags")!.Value = a.Find("resumeFlags")!.Long | 6; break;
                case 0x04 or 0x05 or 0x23 when a.Find("bankKind") is null: a.Children.Add(Value("bankKind", 0L)); break;
                case 0x13 or 0x14 when V88: a.Find("value")!.Children.Add(Value("bypassTransition", 0L)); break;
                case 0x12 when a.Find("target")!.Long == 0: a.Find("target")!.Value = a.Find("state")!.Long; break;
                case 0x19 when a.Find("target")!.Long == 0: a.Find("target")!.Value = a.Find("switch")!.Long; break;
            }
            return a;
        }

        // ------------------------------------------------------------ music

        private BankField MusicSegment(BankField x)
        {
            var s = MusicNode(x);
            s.Children.Add(Copy(x.Find("duration")!));
            s.Children.Add(Copy(x.Find("markerCount")!));
            var markers = Copy(x.Find("markers")!);
            if (V88)
                // a marker's name kept with its length became one ending at a zero
                foreach (var m in markers.Children.Where(m => m.Find("name") is null)) m.Children.Add(Value("name", ""));
            s.Children.Add(markers);
            return s;
        }

        private BankField MusicSwitch(BankField x)
        {
            var s = MusicTransitions(x);
            foreach (string name in new[] { "continuePlaying", "depth", "arguments", "treeSize", "mode", "tree" })
                s.Children.Add(Copy(x.Find(name)!));
            return s;
        }

        private BankField MusicPlaylist(BankField x)
        {
            var s = MusicTransitions(x);
            s.Children.Add(Copy(x.Find("itemCount")!));
            var items = Copy(x.Find("items")!);
            if (V88) PlaylistLoops(items);
            s.Children.Add(items);
            return s;

            // a playlist item's loop range, which 88 hasn't (the pairs' 1,897 items all have 0 and 0)
            static void PlaylistLoops(BankField items)
            {
                foreach (var item in items.Children)
                {
                    item.Children.Add(Value("loopMin", 0L));
                    item.Children.Add(Value("loopMax", 0L));
                    if (item.Find("items") is { } children) PlaylistLoops(children);
                }
            }
        }

        /// <summary>What every music object keeps: its MIDI flags (none before 90), node settings, children, meter and stingers.</summary>
        private BankField MusicNode(BankField x)
        {
            var overrideMeter = Copy(x.Find("overrideMeter")!);
            if (V88) Yes(overrideMeter);
            // the top of a music tree overrides MIDI tempo and target (as all 9 of the game's do), the rest don't (5,855)
            long midi = x.Find("settings")!.Find("parent")!.Long == 0 ? 6 : 0;
            return Node(x.Name, Copy(x.Find("id")!), x.Find("midiFlags") is { } m ? Copy(m) : Value("midiFlags", midi),
                NodeSettings(x.Find("settings")!), Copy(x.Find("children")!), Copy(x.Find("meter")!), overrideMeter,
                Copy(x.Find("stingerCount")!), Copy(x.Find("stingers")!));
        }

        /// <summary>A music switch's or playlist's transition rules: where to jump to (from 133), and yes as 1 rather than 255.</summary>
        private BankField MusicTransitions(BankField x)
        {
            var s = MusicNode(x);
            s.Children.Add(Copy(x.Find("ruleCount")!));
            var rules = Copy(x.Find("rules")!);
            foreach (var rule in rules.Children)
            {
                var entry = rule.Find("entry")!;
                if (entry.Find("jumpToKind") is null) entry.Children.Add(Value("jumpToKind", 0L));
                if (!V88) continue;
                Yes(entry.Find("playPreEntry")!);
                Yes(rule.Find("exit")!.Find("playPostExit")!);
                if (rule.Find("transitionSegment") is { } t)
                {
                    Yes(t.Find("playPreEntry")!);
                    Yes(t.Find("playPostExit")!);
                }
            }
            s.Children.Add(rules);
            return s;
        }

        private BankField MusicTrack(BankField x)
        {
            var t = Node(x.Name, Copy(x.Find("id")!), x.Find("midiFlags") is { } m ? Copy(m) : Value("midiFlags", 0L));
            var sources = x.Find("sources")!;
            t.Children.Add(Copy(x.Find("sourceCount")!));
            t.Children.Add(Node("sources", sources.Children.Select(Source).ToArray()));
            t.Children.Add(Copy(x.Find("clipCount")!));
            if (x.Find("clips") is { } clips)
            {
                var c = Copy(clips);
                // a clip names the event it plays from 133 on; none in 88's
                foreach (var clip in c.Children.Where(clip => clip.Find("event") is null)) clip.Children.Add(Value("event", 0L));
                t.Children.Add(c);
                t.Children.Add(Copy(x.Find("subTrackCount")!));
            }
            t.Children.Add(Copy(x.Find("automationCount")!));
            var automations = Copy(x.Find("automations")!);
            foreach (var a in automations.Children)
            {
                // high-pass was added at 2, moving fade in and out up one: renumbered by name
                var kind = a.Find("kind")!;
                string name = BankTerms.ClipAutomation(_f.Version, kind.Long);
                kind.Value = (long)Enumerable.Range(0, 5).First(i => BankTerms.ClipAutomation(Version, i) == name);
            }
            t.Children.Add(automations);
            t.Children.Add(NodeSettings(x.Find("settings")!));
            var trackKind = Copy(x.Find("kind")!);
            t.Children.Add(trackKind);
            if (trackKind.Long == 3)
            {
                if (x.Find("switch") is null) Refuse(L.T("BankUpgrade.ObjectNotLearnt", "CAkMusicTrack (switch)", x.Find("id")!.Long));
                else
                {
                    t.Children.Add(Copy(x.Find("switch")!));
                    t.Children.Add(Copy(x.Find("switchTransition")!));
                }
            }
            t.Children.Add(Copy(x.Find("lookAhead")!));
            return t;
        }

        // ------------------------------------------------------------ attenuations and effects

        /// <summary>
        /// An attenuation. 150 has nineteen curve uses where 88 had five and 140 seven: the first four (dry and wet
        /// volumes, low-pass) stay, 88's spread moves to 5 with high-pass at 4 and focus at 6 unused (-1), and the twelve
        /// obstruction, occlusion, diffraction and transmission uses go to the project's curves (-2), as the pairs' 167
        /// attenuations all show. The height spread 88 hasn't is on.
        /// </summary>
        private BankField Attenuation(BankField x)
        {
            var a = Node(x.Name, Copy(x.Find("id")!), x.Find("heightSpread") is { } h ? Copy(h) : Value("heightSpread", 1L), Copy(x.Find("cone")!));
            if (x.Find("coneSettings") is { } cone)
            {
                var c = Copy(cone);
                if (c.Find("highPass") is null) c.Children.Add(Value("highPass", 0f));
                a.Children.Add(c);
            }
            var uses = x.Find("curveFor")!;
            long Use(int slot) => uses.Find($"slot{slot}")?.Long ?? -1;
            var slots = new long[19];
            for (int i = 0; i < 19; i++) slots[i] = i < 7 ? -1 : -2;
            if (V88)
            {
                for (int i = 0; i < 4; i++) slots[i] = Use(i);
                slots[5] = Use(4);
            }
            else
                for (int i = 0; i < 7; i++) slots[i] = Use(i);
            a.Children.Add(Node("curveFor", slots.Select((v, i) => Value($"slot{i}", v)).ToArray()));
            a.Children.Add(Copy(x.Find("curveCount")!));
            a.Children.Add(Copy(x.Find("distanceCurves")!));
            a.Children.Add(Curves(x.Find("curves")!));
            return a;
        }

        /// <summary>
        /// An effect: its plugin's settings (the Meter before 2016 hadn't Infinite Hold, which is off), its media and
        /// curves, then the states and starting values 150 keeps (none in 88).
        /// </summary>
        private BankField Effect(BankField x)
        {
            var e = Copy(x.Find("effect")!);
            long plugin = e.Find("plugin")!.Long;
            if (e.Find("settings") is { } settings)
            {
                if (settings.Type == "gap") Note(L.T("BankUpgrade.EffectSettingsKept", BankTerms.Plugin(plugin)));
                else if (plugin == 0x00810003 && settings.Find("Infinite Hold") is null)
                {
                    settings.Children.Insert(5, Value("Infinite Hold", 0L));
                    e.Find("settingsSize")!.Value = e.Find("settingsSize")!.Long + 1;
                }
            }
            // an effect's curves change its plugin's own settings, numbered by the plugin: only the kind and blend 88 hadn't
            foreach (var curve in e.Find("curves")!.Find("set")!.Children)
            {
                if (curve.Find("kind") is null) curve.Children.Add(Value("kind", 0L));
                if (curve.Find("blend") is null) curve.Children.Add(Value("blend", 2L));
            }
            if (e.Find("states") is { } states) Replace(e, States(states, nodeProperties: false));
            else e.Children.Add(Node("states", Value("propertyCount", 0L), Node("changes"), Value("groupCount", 0L), Node("groups")));
            if (e.Find("valueCount") is null)
            {
                e.Children.Add(Value("valueCount", 0L));
                e.Children.Add(Node("values"));
            }
            return Node(x.Name, Copy(x.Find("id")!), e);
        }
    }

    // ---------------------------------------------------------------- building trees

    private static BankField Node(string name, params BankField?[] children)
    {
        var node = new BankField(name, "node", null, 0, 0);
        foreach (var c in children)
            if (c is not null)
            {
                c.Parent = node;
                node.Children.Add(c);
            }
        return node;
    }

    private static BankField Value(string name, object value) => new(name, value is float ? "f32" : "u32", value, 0, 0);

    private static BankField Bytes(string name, byte[] value) => new(name, "gap", value, 0, value.Length);

    /// <summary>A copy of a tree, so the old bank's stays as it was read.</summary>
    private static BankField Copy(BankField f)
    {
        var c = new BankField(f.Name, f.Type, f.Value, f.Offset, f.Size) { Meaning = f.Meaning, Unit = f.Unit, Hash = f.Hash };
        foreach (var child in f.Children)
        {
            var cc = Copy(child);
            cc.Parent = c;
            c.Children.Add(cc);
        }
        return c;
    }

    /// <summary>Puts <paramref name="replacement"/> in the place of <paramref name="node"/>'s child of the same name.</summary>
    private static void Replace(BankField node, BankField replacement)
    {
        int at = node.Children.FindIndex(c => c.Name == replacement.Name);
        replacement.Parent = node;
        if (at < 0) node.Children.Add(replacement);
        else node.Children[at] = replacement;
    }

    /// <summary>A yes/no byte as one bit at <paramref name="shift"/>.</summary>
    private static long Bit(BankField node, string name, int shift) => (node.Find(name)?.Long ?? 0) != 0 ? 1L << shift : 0;

    /// <summary>A yes kept as 255 (as 88 kept some) written as 1, as 150 keeps them.</summary>
    private static void Yes(BankField f)
    {
        if (f.Long != 0) f.Value = 1L;
    }
}
// loc-ignore-end
