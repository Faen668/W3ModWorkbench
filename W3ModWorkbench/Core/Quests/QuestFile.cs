using System.IO;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Quests;

/// <summary>
/// A quest (.w2quest) or quest phase (.w2phase) read as its graph of blocks. A cooked file keeps each block's links as
/// <c>cachedConnections</c> (an output's name, then the blocks and inputs it leads to) and nothing of the layout; an
/// uncooked one (REDkit's r4data) keeps sockets, connections and each block's place instead. Either way the blocks and
/// their links come out the same here.
/// </summary>
public sealed class QuestFile
{
    public required string Path { get; init; }
    public required Cr2wFile Cr2w { get; init; }
    /// <summary>The top graph: the quest's or the phase's own.</summary>
    public required QuestGraph Graph { get; init; }
    /// <summary>Whether the layout was taken out by cooking (no sockets, no places): the game's and mods' copies.</summary>
    public bool Cooked { get; init; }
    /// <summary>Whether it's a quest (.w2quest, CQuest) rather than a phase.</summary>
    public bool IsQuest { get; init; }

    /// <summary>Every block in the file, in its embedded phases' graphs too.</summary>
    public IEnumerable<QuestBlock> AllBlocks() => Graph.AllBlocks();

    /// <summary>The GUIDs the file's deletion markers name: blocks a DLC takes out of the quest.</summary>
    public IEnumerable<Guid> DeletedGuids() => AllBlocks().SelectMany(b => b.DeletedGuids);

    /// <summary>Reads a quest or phase. Throws <see cref="InvalidDataException"/> when it's neither.</summary>
    public static QuestFile Read(byte[] data, string path) => Read(Cr2wFile.Read(data), path);

    public static QuestFile Read(Cr2wFile file, string path)
    {
        if (file.Exports.Count == 0) throw new InvalidDataException("empty");
        var top = file.Object(0);
        if (top.ClassName is not ("CQuest" or "CQuestPhase") || top["graph"] is not RedHandle handle || file.Object(handle) is not { } graphObject)
            throw new InvalidDataException(top.ClassName);
        var reader = new Reader(file);
        var graph = reader.Graph(graphObject, null);
        reader.Link();
        return new QuestFile
        {
            Path = path,
            Cr2w = file,
            Graph = graph,
            Cooked = reader.Cooked,
            IsQuest = top.ClassName == "CQuest",
        };
    }

    /// <summary>Reads the graphs, then the links once every block is known (a link can point at a later object).</summary>
    private sealed class Reader(Cr2wFile file)
    {
        private readonly Dictionary<int, QuestBlock> _blocks = [];
        public bool Cooked;

        public QuestGraph Graph(RedObject graphObject, QuestBlock? owner)
        {
            var graph = new QuestGraph { Owner = owner, Object = graphObject };
            if (graphObject["sourceDataRemoved"] is true) Cooked = true;
            foreach (var item in file.Array(graphObject, "graphBlocks"))
            {
                if (item is not RedHandle h || h.Export is not int index || _blocks.ContainsKey(index)) continue;
                var block = Block(file.Object(index), index, graph);
                graph.Blocks.Add(block);
                _blocks[index] = block;
                // a block that runs an external phase can still keep an old embedded graph in an uncooked file; the
                // external phase is what runs (and what REDkit opens), and cooking drops the other
                if (block.ExternalPhase is null && block.Object["embeddedGraph"] is RedHandle eh && file.Object(eh) is { } embedded)
                    block.Embedded = Graph(embedded, block);
            }
            return graph;
        }

        private QuestBlock Block(RedObject o, int index, QuestGraph graph)
        {
            var block = new QuestBlock
            {
                Export = index,
                Object = o,
                Graph = graph,
                Guid = o["guid"] is Guid g ? g : null,
                StoredName = o["name"] as string,
                Comment = o["comment"] as string ?? "",
                Caption = o["caption"] as string,
                DescriptionText = o["descriptionText"] as string,
                ExternalPhase = o["phase"] is RedHandle ph ? file.PathOf(ph) : null,
                SocketId = o["socketID"] as string,
            };
            if (o["position"] is RedObject p) block.Position = (p.Float("X"), p.Float("Y"));
            if (o.ClassName == "CQuestDeletionMarkerBlock")
                foreach (var item in file.Array(o, "guids"))
                    if (item is Guid d) block.DeletedGuids.Add(d);
            return block;
        }

        public void Link()
        {
            foreach (var block in _blocks.Values)
            {
                var o = block.Object;
                // cooked: the links as the game follows them
                foreach (var item in file.Array(o, "cachedConnections"))
                {
                    if (item is not RedObject connection) continue;
                    string output = connection["socketId"] as string ?? "";
                    block.CachedOutputs.Add(output);
                    foreach (var target in connection.Array("blocks"))
                        if (target is RedObject desc && desc["ock"] is RedHandle th && th.Export is int ti && _blocks.TryGetValue(ti, out var to))
                            Add(new QuestLink(block, output, to, desc["putName"] as string ?? ""));
                }
                // uncooked: sockets and the connections between them
                foreach (var item in file.Array(o, "sockets"))
                {
                    if (item is not RedHandle sh || file.Object(sh) is not { } socketObject) continue;
                    var socket = new QuestSocket
                    {
                        Name = socketObject["name"] as string ?? "",
                        IsOutput = socketObject["direction"] is RedEnum { Values: ["LSD_Output"] } || socketObject["direction"] as string == "LSD_Output",
                        Flags = (uint)socketObject.Integer("flags"),
                        Object = socketObject,
                    };
                    block.Sockets.Add(socket);
                }
            }
            // a connection is kept on both of its sockets: follow it from the output's side only
            foreach (var block in _blocks.Values)
                foreach (var socket in block.Sockets.Where(s => s.IsOutput))
                    foreach (var item in file.Array(socket.Object, "connections"))
                    {
                        if (item is not RedHandle ch || file.Object(ch) is not { } connection) continue;
                        var other = Other(connection, socket.Object);
                        if (other?["block"] is RedHandle bh && bh.Export is int bi && _blocks.TryGetValue(bi, out var to))
                            Add(new QuestLink(block, socket.Name, to, other["name"] as string ?? ""));
                    }
        }

        private RedObject? Other(RedObject connection, RedObject socket)
        {
            var source = connection["source"] is RedHandle s ? file.Object(s) : null;
            var destination = connection["destination"] is RedHandle d ? file.Object(d) : null;
            return ReferenceEquals(source, socket) ? destination : source;
        }

        private static void Add(QuestLink link)
        {
            if (link.From.Links.Contains(link)) return;
            link.From.Links.Add(link);
            link.To.Incoming.Add(link);
        }
    }
}

/// <summary>A graph of blocks: a quest's, a phase file's, or one embedded in a phase block.</summary>
public sealed class QuestGraph
{
    /// <summary>The phase block it's embedded in; null for the file's own graph.</summary>
    public QuestBlock? Owner { get; init; }
    public required RedObject Object { get; init; }
    public List<QuestBlock> Blocks { get; } = [];

    public IEnumerable<QuestBlock> AllBlocks()
    {
        foreach (var block in Blocks)
        {
            yield return block;
            if (block.Embedded is { } inner)
                foreach (var b in inner.AllBlocks()) yield return b;
        }
    }

    /// <summary>How deep it is: 0 for the file's own graph.</summary>
    public int Depth => Owner is null ? 0 : Owner.Graph.Depth + 1;
}

/// <summary>A link from a block's output to another block's input.</summary>
public sealed record QuestLink(QuestBlock From, string Output, QuestBlock To, string Input);

/// <summary>A socket of an uncooked block: one of its inputs or outputs as REDkit draws them.</summary>
public sealed class QuestSocket
{
    public required string Name { get; init; }
    public bool IsOutput { get; init; }
    public uint Flags { get; init; }
    public required RedObject Object { get; init; }

    /// <summary>
    /// Whether REDkit draws it: its flags' 0x2 bit, which REDkit clears for a socket hidden as unused (a scene's other
    /// inputs on a block that uses one); 0x100 marks the Cut input every block has, which it never draws.
    /// </summary>
    public bool Shown => (Flags & 0x2) != 0 && (Flags & 0x100) == 0;
}

/// <summary>One block of a quest graph.</summary>
public sealed class QuestBlock
{
    public int Export { get; init; }
    public required RedObject Object { get; init; }
    public required QuestGraph Graph { get; init; }
    public string ClassName => Object.ClassName;
    /// <summary>The block's GUID: what saves and Native Quest Hook know it by. Null for notes and comments, which have none.</summary>
    public Guid? Guid { get; init; }
    /// <summary>The name as the file keeps it; null when it's the class's own default, which isn't saved.</summary>
    public string? StoredName { get; init; }
    public string Comment { get; init; } = "";
    /// <summary>A note's or comment's caption.</summary>
    public string? Caption { get; init; }
    public string? DescriptionText { get; init; }
    /// <summary>The external .w2phase a phase block runs, when it runs one rather than an embedded graph.</summary>
    public string? ExternalPhase { get; init; }
    /// <summary>The embedded graph a phase block runs.</summary>
    public QuestGraph? Embedded { get; set; }
    /// <summary>A phase input or output block's socket, when the file keeps it (uncooked files do).</summary>
    public string? SocketId { get; init; }
    /// <summary>Where REDkit puts it; null in a cooked file.</summary>
    public (float X, float Y)? Position { get; set; }
    public List<QuestLink> Links { get; } = [];
    public List<QuestLink> Incoming { get; } = [];
    public List<QuestSocket> Sockets { get; } = [];
    /// <summary>The output names a cooked block keeps, linked or not, in order.</summary>
    public List<string> CachedOutputs { get; } = [];
    public List<Guid> DeletedGuids { get; } = [];

    /// <summary>
    /// The block's name as REDkit shows it: its own, else its class's default (the file leaves that out), else a note's
    /// caption, else its class.
    /// </summary>
    public string Name => !string.IsNullOrEmpty(StoredName) ? StoredName
        : QuestNames.Default(ClassName) ?? (!string.IsNullOrEmpty(Caption) ? Caption : QuestNames.Short(ClassName));

    /// <summary>Whether a link is a cut control's: its Thunder output to a block's Cut input, which stops that block.</summary>
    public static bool IsCut(QuestLink link) => link.Input == "Cut" && link.From.ClassName == "CQuestCutControlBlock";

    public bool IsPhaseInput => ClassName == "CQuestPhaseInputBlock";
    public bool IsPhaseOutput => ClassName == "CQuestPhaseOutputBlock";

    /// <summary>
    /// A phase input or output block's socket on the phase block that runs its graph: its socketID, else its name (a
    /// cooked file keeps only the name), else In or Out.
    /// </summary>
    public string? PhaseSocket => IsPhaseInput || IsPhaseOutput
        ? !string.IsNullOrEmpty(SocketId) ? SocketId : !string.IsNullOrEmpty(StoredName) ? StoredName : IsPhaseInput ? "In" : "Out"
        : null;

    /// <summary>The block's outputs: its output sockets when it has them, else the outputs it keeps and links from.</summary>
    public IEnumerable<string> Outputs() => Sockets.Count > 0
        ? Sockets.Where(s => s.IsOutput).Select(s => s.Name)
        : CachedOutputs.Concat(Links.Select(l => l.Output)).Distinct();

    public override string ToString() => $"{ClassName} {StoredName} {Guid}";
}
