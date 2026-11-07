namespace W3ModWorkbench.Core.Quests;

/// <summary>
/// The names quest blocks have when their file keeps none. A block's name is saved only when it differs from its
/// class's own default, so cooked and uncooked files alike leave most of them out ("Script", "Pause", "Interaction
/// dialog"…). These are the game's own words, not ours, so they aren't translated.
/// </summary>
public static class QuestNames
{
    /// <summary>
    /// Each class's default name. Where they come from:
    /// <list type="bullet">
    /// <item>the running game (Native Quest Hook's log names every block it sees; these are the names of blocks whose
    /// files keep none): AND, Context dialog, Cut control, Encounter manager, FactsDB Change, Hide/Show layers,
    /// Interaction dialog, Map Pin State, Pause, Phase, In, Quest, Scene, Story phase setter, XOR;</item>
    /// <item>REDkit's quest editor, showing a block whose file keeps no name: Script, Reward, Checkpoint, Scripted
    /// actions, Deletion marker, Out, Condition, Start, End;</item>
    /// <item>REDkit's add-block menu, whose words are also in the game's exe: the rest.</item>
    /// </list>
    /// </summary>
    // the game's own names, not the app's text: loc-ignore-start
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        // the running game
        ["CQuestAndBlock"] = "AND",
        ["CQuestContextDialogBlock"] = "Context dialog",
        ["CQuestCutControlBlock"] = "Cut control",
        ["CQuestEncounterManagerBlock"] = "Encounter manager",
        ["CQuestFactsDBChangingBlock"] = "FactsDB Change",
        ["CQuestLayersHiderBlock"] = "Hide/Show layers",
        ["CQuestInteractionDialogBlock"] = "Interaction dialog",
        ["CJournalQuestMappinStateBlock"] = "Map Pin State",
        ["CQuestPauseConditionBlock"] = "Pause",
        ["CQuestPhaseBlock"] = "Phase",
        ["CQuestPhaseInputBlock"] = "In",
        ["CJournalQuestBlock"] = "Quest",
        ["CQuestSceneBlock"] = "Scene",
        ["CQuestStoryPhaseSetterBlock"] = "Story phase setter",
        ["CQuestXorBlock"] = "XOR",
        // REDkit's quest editor
        ["CQuestScriptBlock"] = "Script",
        ["CQuestRewardBlock"] = "Reward",
        ["CQuestCheckpointBlock"] = "Checkpoint",
        ["CQuestScriptedActionsBlock"] = "Scripted actions",
        ["CQuestDeletionMarkerBlock"] = "Deletion marker",
        ["CQuestPhaseOutputBlock"] = "Out",
        ["CQuestConditionBlock"] = "Condition",
        ["CQuestStartBlock"] = "Start",
        ["CQuestEndBlock"] = "End",
        // REDkit's add-block menu
        ["CQuestRandomBlock"] = "Random",
        ["CQuestTeleportBlock"] = "Teleport",
        ["CQuestTimeManagementBlock"] = "Time management",
        ["CQuestPlayAnimationBlock"] = "Play animation",
        ["CQuestPokeScriptedActionsBlock"] = "Poke Scripted Action",
        ["CQuestResetScriptedActionsBlock"] = "Reset scripted actions",
        ["CQuestRiderScriptedActionsBlock"] = "Rider Scripted actions",
        ["CJournalBlock"] = "Entry",
        ["CJournalQuestTrackBlock"] = "Track Quest",
        ["CJournalQuestObjectiveCounterGraphBlock"] = "Objective Counter",
        ["CJournalQuestMonsterKnownGraphBlock"] = "Quest Monster Known",
        ["CQuestGraphMinigameBlock"] = "Minigame",
        ["CQuestFastForwardCommunitiesBlock"] = "Fast forward communities",
        ["CQuestSpawnVehicleBlock"] = "Spawn Player's Vehicle",
        ["CQuestManageFastTravelBlock"] = "Manage fast travel",
        ["CDeniedAreaBlock"] = "Denied area",
        ["CQuestChangeWorldBlock"] = "Change world",
        ["CQuestEntityMotionBlock"] = "Entity Motion",
        ["CManageSwitchBlock"] = "Manage switch",
        ["CQuestLookAtBlock"] = "Look at",
        ["CQuestScenePrepareBlock"] = "Scene prepare",
        ["CQuestContentActivatorBlock"] = "Activate Content",
        ["CQuestEncounterFullRespawn"] = "Encounter full respawn",
        ["CQuestEncounterPhaseBlock"] = "Encounter phase setter",
        ["CQuestEncounterActivator"] = "Encounter manual activation",
        ["CQuestSpawnNotStreamedBoatBlock"] = "Spawn Not Streamed Boat",
        ["CQuestStaticCameraRunBlock"] = "Run Static Camera",
        ["CQuestStaticCameraSwitchBlock"] = "Switch Static Camera",
        ["CQuestStaticCameraStopBlock"] = "Activate Game Camera",
        ["CDescriptionGraphBlock"] = "Description note",
        ["CCommentGraphBlock"] = "Comment block",
    };
    // loc-ignore-end

    /// <summary>A class's default block name, or null when it isn't known.</summary>
    public static string? Default(string className) => Defaults.GetValueOrDefault(className);

    /// <summary>
    /// A class's name without its C and Block ("CQuestTeleportBlock" is "QuestTeleport"), for a class with no known
    /// default.
    /// </summary>
    public static string Short(string className)
    {
        string name = className.Length > 1 && className[0] == 'C' && char.IsUpper(className[1]) ? className[1..] : className;
        return name.EndsWith("Block", StringComparison.Ordinal) && name.Length > 5 ? name[..^5] : name;
    }
}
