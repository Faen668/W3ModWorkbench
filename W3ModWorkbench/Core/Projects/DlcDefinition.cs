using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Projects;

/// <summary>
/// A DLC's definition (<c>dlc\NAME\NAME.reddlc</c>, a CDLCDefinition): its ID, and the mounters that tell the game what
/// it adds. Build writes one for the project's own DLC, with a definitions mounter for each XML file in its
/// gameplay\items folder and a New Game+ one for each in items_plus, as the Remastered's XML overrides are mounted
/// (and as Stack Your Items does it: one mounter for each file).
/// </summary>
public static class DlcDefinition
{
    /// <param name="id">The DLC's ID, its folder name.</param>
    /// <param name="normal">The game paths of the XML for the normal game.</param>
    /// <param name="newGamePlus">The game paths of the XML for New Game+.</param>
    public static byte[] Make(string id, IReadOnlyList<string> normal, IReadOnlyList<string> newGamePlus)
    {
        var objects = new JsonArray();
        var mounters = new JsonArray();
        int index = 1;
        foreach (var (path, plus) in normal.Select(p => (p, false)).Concat(newGamePlus.Select(p => (p, true))))
        {
            mounters.Add($"export:{index++}");
            objects.Add(new JsonObject
            {
                ["$class"] = plus ? "CR4DefinitionsNGPlusDLCMounter" : "CR4DefinitionsDLCMounter",
                ["$parent"] = 0,
                ["$flags"] = 8200,
                ["definitionXmlFilePath"] = path.Replace('/', '\\'),
            });
        }
        objects.Insert(0, new JsonObject
        {
            ["$class"] = "CDLCDefinition",
            ["$flags"] = 8192,
            ["id"] = id,
            ["localizedNameKey"] = id,
            ["localizedDescriptionKey"] = id + "_desc",
            ["mounters"] = mounters,
            // a mod's DLC isn't one to list in the game's DLC menu
            ["visibleInDLCMenu"] = false,
        });
        var root = new JsonObject
        {
            ["$format"] = Cr2wJson.FormatName,
            ["$formatVersion"] = 1,
            ["objects"] = objects,
            ["file"] = new JsonObject
            {
                ["version"] = 164,
                ["flags"] = 0,
                ["timestamp"] = 0,
                ["build"] = 0,
                ["chunks"] = 6,
                ["names"] = new JsonArray("", "CDLCDefinition", "id", "CName", id, "localizedNameKey", "String", "localizedDescriptionKey", "mounters",
                    "array:2,0,ptr:IGameplayDLCMounter", "CR4DefinitionsDLCMounter", "definitionXmlFilePath", "CR4DefinitionsNGPlusDLCMounter", "visibleInDLCMenu", "Bool"),
                ["imports"] = new JsonArray(),
                ["types"] = new JsonObject
                {
                    ["CDLCDefinition.id"] = "CName",
                    ["CDLCDefinition.localizedNameKey"] = "String",
                    ["CDLCDefinition.localizedDescriptionKey"] = "String",
                    ["CDLCDefinition.mounters"] = "array:2,0,ptr:IGameplayDLCMounter",
                    ["CDLCDefinition.visibleInDLCMenu"] = "Bool",
                    ["CR4DefinitionsDLCMounter.definitionXmlFilePath"] = "String",
                    ["CR4DefinitionsNGPlusDLCMounter.definitionXmlFilePath"] = "String",
                },
            },
        };
        // through its text, as a node made here holds its numbers as the type it was given, not as a parsed one
        return Cr2wJson.FromJsonText(root.ToJsonString());
    }
}
