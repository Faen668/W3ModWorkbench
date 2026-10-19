namespace W3ModWorkbench.Core;

/// <summary>What a kind of file is, by its extension: a short name and a sentence on what it holds.</summary>
public sealed record FileKind(string Name, string Description);

/// <summary>The files the game and its mods are made of, in a few words each, so a list of them can say what each one is.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // scripts, data and text
        [".ws"] = new("WitcherScript", "Script source: classes, functions and states the game compiles at start-up."),
        [".rsblob"] = new("Compiled scripts", "Scripts compiled by REDkit. The tool decompiles them back to WitcherScript."),
        [".xml"] = new("XML", "Game data: items, abilities, menus, loot and more, read when the game starts."),
        [".csv"] = new("CSV table", "A table of data, such as strings for one language, or a list the game looks things up in."),
        [".w3strings"] = new("Strings", "The game's text for one language, each line under a number that scripts and data refer to."),
        [".json"] = new("JSON", "Settings or data in plain text, such as a mod's info.json."),
        [".txt"] = new("Text", "Plain text."),
        [".ini"] = new("Settings", "Plain-text settings."),
        [".settings"] = new("Settings", "Plain-text settings, such as input.settings or mods.settings."),

        // models and animation
        [".w2mesh"] = new("Mesh", "A 3D model: its materials, LODs and how it's split up. The shape itself is in the .buffer files beside it."),
        [".buffer"] = new("Data buffer", "The bulk data of the file it's named after, such as a mesh's vertices. Only that file can read it, so keep the two together."),
        [".w2ent"] = new("Entity", "A template for something in the world: an NPC, a monster, an item or a piece of armour, with its components."),
        [".w2rig"] = new("Rig", "A skeleton: the bones a model is skinned to and that animations move."),
        [".w2anims"] = new("Animation set", "Animations for a rig."),
        [".w2animev"] = new("Animation events", "Events timed to animations: sounds, effects, footsteps and hits."),
        [".w2ragdoll"] = new("Ragdoll", "How a body falls and collides when it goes limp."),
        [".w2p"] = new("Particle system", "A visual effect made of particles: fire, sparks, blood, magic."),
        [".redfur"] = new("Fur", "Hair and fur, drawn with NVIDIA HairWorks."),
        [".redcloth"] = new("Cloth", "Simulated cloth, such as capes and skirts."),
        [".w2cloth"] = new("Cloth", "Simulated cloth, such as capes and skirts."),
        [".apb"] = new("Apex cloth", "Simulated cloth made with NVIDIA APEX."),
        [".srt"] = new("SpeedTree", "A tree or plant made with SpeedTree."),

        // textures and materials
        [".xbm"] = new("Texture", "A texture's description. In a mod or the game its pixels are in a texture.cache under the same path."),
        [".dds"] = new("DDS texture", "A texture with its mip levels, as graphics tools and the game's cooker read them."),
        [".png"] = new("PNG picture", "A picture, usually a texture before it's cooked or an icon."),
        [".tga"] = new("TGA picture", "A picture, usually a texture before it's cooked."),
        [".w2mi"] = new("Material instance", "A material with its settings filled in: which textures and colours a surface uses."),
        [".w2mg"] = new("Material graph", "A shader: how a kind of surface is drawn."),
        [".w2cube"] = new("Cube map", "A texture of the surroundings, for reflections and the sky."),
        [".texarray"] = new("Texture array", "Several textures of the same size kept as one, such as terrain layers."),
        [".env"] = new("Environment", "Lighting, fog, sky and colour grading for an area or a time of day."),

        // world and quests
        [".w2w"] = new("World", "A world's settings: its terrain, sky and the layers of things placed in it."),
        [".w2l"] = new("Layer", "Things placed in a world together: buildings, NPCs, triggers, lights."),
        [".w2ter"] = new("Terrain tile", "A piece of a world's ground."),
        [".flyr"] = new("Foliage", "The grass, bushes and trees placed on a piece of a world."),
        [".navmesh"] = new("Navigation mesh", "Where characters can walk."),
        [".w2quest"] = new("Quest", "The top of a quest: its phases and how they connect."),
        [".w2phase"] = new("Quest phase", "A step of a quest: the blocks that run its scenes, objectives and facts."),
        [".w2scene"] = new("Scene", "A conversation or cutscene: lines, cameras, animations and choices."),
        [".w2cutscene"] = new("Cutscene", "A recorded cutscene, played as it is: its actors, their animations and the camera's, and events timed along them."),
        [".w2comm"] = new("Community", "NPCs who live somewhere: who they are, where they go and when."),
        [".w2job"] = new("Action job", "Something an NPC does in place, such as sweeping or sitting, as a run of animations."),
        [".w2beh"] = new("Behaviour graph", "How a character's animations blend and change from one to another."),
        [".w2behtree"] = new("Behaviour tree", "An NPC's or monster's AI: what it decides to do and when."),
        [".journal"] = new("Journal entry", "A page of the journal: a quest, a character, a creature or a glossary entry."),
        [".w2je"] = new("Journal entry", "A page of the journal: a quest, a character, a creature or a glossary entry."),
        [".reddlc"] = new("DLC description", "Says what a DLC adds to the game, and when."),

        // interface, sound and video
        [".redswf"] = new("Flash interface", "A menu or HUD panel: a Flash movie the game shows and scripts talk to."),
        [".w2fnt"] = new("Font", "A font for the interface."),
        [".bnk"] = new("Sound bank", "Wwise sounds and the events that play them."),
        [".wem"] = new("Sound", "A Wwise sound: speech, music or an effect."),
        [".usm"] = new("Video", "A video, such as an intro or an ending."),
        [".bundle"] = new("Bundle", "An archive of the game's or a mod's files."),
        [".cache"] = new("Cache", "An archive of textures, sounds, collision or shaders."),
    };

    /// <summary>What the file is, or null for a kind not known. A numbered buffer (x.w2mesh.1.buffer) is a buffer of its file.</summary>
    public static FileKind? Of(string name)
    {
        if (Companions.BufferOwner(name) is { } owner)
        {
            var of = Of(owner);
            return new FileKind("Data buffer", of is null
                ? Kinds[".buffer"].Description
                : $"The bulk data of {System.IO.Path.GetFileName(owner)} ({of.Name.ToLowerInvariant()}). Only that file can read it, so keep the two together.");
        }
        return Kinds.TryGetValue(System.IO.Path.GetExtension(name), out var kind) ? kind : null;
    }
}
