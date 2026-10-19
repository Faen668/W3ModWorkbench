using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench.Core;

/// <summary>
/// The engine class a game file holds, as REDkit's asset browser filters by: the class of the resource at the top of a
/// CR2W file, which its extension says (each read from the game's own files), and C2dArray for a .csv table.
/// </summary>
public static class ResourceClasses
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = "C2dArray",
        [".redicsv"] = "CIndexed2dArray",
        [".redcloth"] = "CApexClothResource",
        [".redapex"] = "CApexDestructionResource",
        [".w2am"] = "CAreaMapPinsResource",
        [".w2beh"] = "CBehaviorGraph",
        [".w2behtree"] = "CBehTree",
        [".xbm"] = "CBitmapTexture",
        [".w2cent"] = "CCharacterEntityTemplate",
        [".w2comm"] = "CCommunity",
        [".redexp"] = "CCookedExplorations",
        [".w2cube"] = "CCubeTexture",
        [".w2cutscene"] = "CCutsceneTemplate",
        [".reddlc"] = "CDLCDefinition",
        [".w3dyng"] = "CDyngResource",
        [".w3app"] = "CEntityExternalAppearance",
        [".w2em"] = "CEntityMapPinsResource",
        [".w2ent"] = "CEntityTemplate",
        [".env"] = "CEnvironmentDefinition",
        [".w2animev"] = "CExtAnimEventsFile",
        [".flyr"] = "CFoliageResource",
        [".w2fnt"] = "CFont",
        [".formation"] = "CFormation",
        [".redfur"] = "CFurMeshResource",
        [".w2w"] = "CGameWorld",
        [".grassmask"] = "CGenericGrassMask",
        [".guiconfig"] = "CGuiConfigResource",
        [".hud"] = "CHudResource",
        [".w2job"] = "CJobTree",
        [".w2je"] = "CJournalInitialEntriesResource",
        [".journal"] = "CJournalResource",
        [".w2l"] = "CLayer",
        [".w2mg"] = "CMaterialGraph",
        [".w2mi"] = "CMaterialInstance",
        [".menu"] = "CMenuResource",
        [".w2mesh"] = "CMesh",
        [".w3fac"] = "CMimicFace",
        [".w2steer"] = "CMoveSteeringBehavior",
        [".w2p"] = "CParticleSystem",
        [".reddest"] = "CPhysicsDestructionResource",
        [".popup"] = "CPopupResource",
        [".w2quest"] = "CQuest",
        [".w2qm"] = "CQuestMapPinsResource",
        [".w2phase"] = "CQuestPhase",
        [".w2ragdoll"] = "CRagdoll",
        [".w3simplex"] = "CResourceSimplexTree",
        [".w2anims"] = "CSkeletalAnimationSet",
        [".w2rig"] = "CSkeleton",
        [".spawntree"] = "CSpawnTree",
        [".srt"] = "CSRTBaseTree",
        [".w2scene"] = "CStoryScene",
        [".w2dset"] = "CStorySceneDialogset",
        [".cellmap"] = "CSwarmCellMap",
        [".redswf"] = "CSwfResource",
        [".w2sf"] = "CSwitchableFoliageResource",
        [".w2ter"] = "CTerrainTile",
        [".texarray"] = "CTextureArray",
        [".w3occlusion"] = "CUmbraTile",
        [".w3occlusiondef"] = "CUmbraScene",
        [".vbrush"] = "CVegetationBrush",
        [".redwpset"] = "CWayPointsCollectionsSet",
        [".redgame"] = "CWitcherGameResource",
    };

    /// <summary>
    /// The class of the resource a file holds, or null for one that holds none (scripts, XML, buffers, videos…). A texture
    /// only in a texture cache is the texture, cube map or array its pixels are.
    /// </summary>
    public static string? Of(ArchiveEntry entry)
    {
        if (entry is TextureEntry texture)
            return texture.Info.IsCube ? "CCubeTexture" : texture.Info.Slices > 1 ? "CTextureArray" : "CBitmapTexture";
        return ByExtension.GetValueOrDefault(System.IO.Path.GetExtension(entry.Path));
    }
}
