using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

/// <summary>What a kind of file is, by its extension: a short name and a sentence on what it holds.</summary>
public sealed record FileKind(string Name, string Description);

/// <summary>The files the game and its mods are made of, in a few words each, so a list of them can say what each one is.</summary>
public static class FileKinds
{
    // each kind's name and description are in the string tables, under FileKinds.<key> and FileKinds.<key>.Description,
    // and looked up each time, so they follow the app's language
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // scripts, data and text
        [".ws"] = "Ws",
        [".rsblob"] = "Rsblob",
        [".xml"] = "Xml",
        [".csv"] = "Csv",
        [".w3strings"] = "W3strings",
        [".json"] = "Json",
        [".txt"] = "Txt",
        [".ini"] = "Ini",
        [".settings"] = "Settings",

        // models and animation
        [".w2mesh"] = "W2mesh",
        [".buffer"] = "Buffer",
        [".w2ent"] = "W2ent",
        [".w2rig"] = "W2rig",
        [".w2anims"] = "W2anims",
        [".w2animev"] = "W2animev",
        [".w2ragdoll"] = "W2ragdoll",
        [".w2p"] = "W2p",
        [".redfur"] = "Redfur",
        [".redcloth"] = "Redcloth",
        [".w2cloth"] = "Redcloth",
        [".apb"] = "Apb",
        [".srt"] = "Srt",

        // textures and materials
        [".xbm"] = "Xbm",
        [".dds"] = "Dds",
        [".png"] = "Png",
        [".tga"] = "Tga",
        [".w2mi"] = "W2mi",
        [".w2mg"] = "W2mg",
        [".w2cube"] = "W2cube",
        [".texarray"] = "Texarray",
        [".env"] = "Env",

        // world and quests
        [".w2w"] = "W2w",
        [".w2l"] = "W2l",
        [".w2ter"] = "W2ter",
        [".flyr"] = "Flyr",
        [".navmesh"] = "Navmesh",
        [".w2quest"] = "W2quest",
        [".w2phase"] = "W2phase",
        [".w2scene"] = "W2scene",
        [".w2cutscene"] = "W2cutscene",
        [".w2comm"] = "W2comm",
        [".w2job"] = "W2job",
        [".w2beh"] = "W2beh",
        [".w2behtree"] = "W2behtree",
        [".journal"] = "Journal",
        [".w2je"] = "Journal",
        [".reddlc"] = "Reddlc",

        // interface, sound and video
        [".redswf"] = "Redswf",
        [".w2fnt"] = "W2fnt",
        [".bnk"] = "Bnk",
        [".wem"] = "Wem",
        [".usm"] = "Usm",
        [".subs"] = "Subs",
        [".bundle"] = "Bundle",
        [".cache"] = "Cache",
    };

    /// <summary>What the file is, or null for a kind not known. A numbered buffer (x.w2mesh.1.buffer) is a buffer of its file.</summary>
    public static FileKind? Of(string name)
    {
        if (Companions.BufferOwner(name) is { } owner)
        {
            var of = Of(owner);
            return new FileKind(L.T("FileKinds.Buffer"), of is null
                ? L.T("FileKinds.Buffer.Description")
                : L.T("FileKinds.BufferOf", System.IO.Path.GetFileName(owner), of.Name.ToLowerInvariant()));
        }
        return Kinds.TryGetValue(System.IO.Path.GetExtension(name), out var key) ? Kind(key) : null;
    }

    private static FileKind Kind(string key) => new(L.T("FileKinds." + key), L.T("FileKinds." + key + ".Description"));
}
