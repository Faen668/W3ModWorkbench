// loc-ignore-start
namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// How a bank of a given version lays its objects out, as named facts: each Wwise release moved fields around, packed
/// flags into bits, widened or narrowed numbers. Every reader asks this sheet rather than testing versions itself, so
/// what changed when is written down once. Worked out against the game's own banks (150), the next-gen update's (140)
/// and the original game's (88).
/// </summary>
internal sealed class BankFormat(int version, bool feedback)
{
    public const int Oldest = 72;
    public const int Newest = 154;

    public int Version { get; } = version;

    /// <summary>Whether the bank says it carries motion feedback (up to 126): every sound then names a feedback bus.</summary>
    public bool Feedback { get; } = feedback && version <= 126;

    // ---- object types

    /// <summary>Types from 16 on: up to 126 they start with two motion feedback kinds, and the rest come two later.</summary>
    public bool OldTypeNumbers => Version <= 126;

    // ---- flags

    /// <summary>Yes/no settings packed as bits in one byte, rather than a byte each.</summary>
    public bool PackedFlags => Version > 89;

    // ---- a sound's source

    public bool WideStorage => Version <= 89;

    /// <summary>The storage number that means streamed: 1 up to 89, then 2 (1 becoming "prefetched").</summary>
    public int StreamedCode => Version <= 89 ? 1 : 2;

    /// <summary>Up to 112 a source names the file that keeps it (a bank, or a .wem by its own ID).</summary>
    public bool SourceNamesItsFile => Version <= 112;

    /// <summary>Up to 88 the source's size comes only when it's kept in a bank; later it always does.</summary>
    public bool SizeOnlyWhenKept => Version <= 88;

    /// <summary>From 151 a source keeps a cache ID before its size.</summary>
    public bool SourceCacheId => Version > 150;

    /// <summary>Up to 112 the source's flag bits are language/has source/external; later language/prefetch/no cache/has source.</summary>
    public bool OldSourceFlags => Version <= 112;

    /// <summary>Whether a plugin of this type keeps settings after a source: sources always, motion sources up to 126.</summary>
    public bool SourceHasSettings(long pluginType) => pluginType == 2 || pluginType == 5 && Version <= 126;

    // ---- effects

    public bool EffectFlagsPacked => Version > 145;
    public bool MetadataSlots => Version > 136;
    public bool AttachmentOverride => Version > 89 && Version <= 145;
    public bool BusEffectCountFirst => Version <= 135;
    public bool BusSecondEffect => Version > 89 && Version <= 145;

    // ---- properties

    public int AttenuationCurveSlots => Version switch { <= 72 => 4, <= 89 => 5, <= 141 => 7, _ => 19 };
    public bool ConeHighPass => Version > 89;
    public bool HeightSpread => Version > 136;

    // ---- routing

    public bool UserSendFlagsPacked => Version > 89;
    public bool ReflectionsBus => Version > 134;
    public bool MidiFlags => Version > 89;

    // ---- limits

    public bool LimitFlagsPacked => Version > 89;
    public bool HdrEnvelopeFlags => Version > 72;

    // ---- states

    /// <summary>From 123 state groups are counted with variable-length numbers, and list which properties they change.</summary>
    public bool CompactStates => Version > 122;

    /// <summary>From 127 each changed property says whether it's in decibels.</summary>
    public bool StatePropertyInDb => Version > 126;

    /// <summary>Up to 145 a state points at a State object holding its values; later it holds them itself.</summary>
    public bool StateValuesInline => Version > 145;

    /// <summary>A state object's value list: up to 126 a byte count and byte numbers, later two bytes each.</summary>
    public bool WideStateValues => Version > 126;

    // ---- game parameters (RTPCs)

    public bool CurveKindAndBlend => Version > 89;

    /// <summary>The width of a curve's target: four bytes up to 89, one up to 113, then a variable-length number.</summary>
    public int CurveTargetWidth => Version switch { <= 89 => 4, <= 113 => 1, _ => 0 };

    // ---- positioning

    public enum PositionLayout { Separate2d3d, Bits112, Bits122, Bits129, Modern }

    public PositionLayout Positioning => Version switch
    {
        <= 89 => PositionLayout.Separate2d3d,
        <= 112 => PositionLayout.Bits112,
        <= 122 => PositionLayout.Bits122,
        <= 129 => PositionLayout.Bits129,
        _ => PositionLayout.Modern,
    };

    public bool PositionAttenuationId => Version <= 129;
    public bool PathZRange => Version > 89;

    // ---- actions and events

    public bool ExceptionCountCompact => Version > 122;
    public bool ActionListCompact => Version > 122;
    public bool PlayNamesBankType => Version >= 144;

    /// <summary>From 150 the action types 0x1A and 0x1B are break and trigger rather than effect bypasses.</summary>
    public bool ActionCodes150 => Version >= 150;

    public bool EventLimits => Version > 154;

    // ---- containers

    public bool LoopRange => Version > 72;
    public bool SwitchModeWide => Version <= 89;
    public bool SwitchKeepsMode => Version <= 150;
    public bool BlendContinuousCheck => Version > 118;

    // ---- music

    public bool MarkerNamesSized => Version <= 136;
    public bool TrackFlagsFirst => Version > 89 && Version <= 152;
    public bool TrackFlagsAfterSources => Version > 152;
    public bool TrackEventIds => Version > 132;
    public bool TrackCacheIds => Version > 150;
    public bool TrackTypeByte => Version > 89;
    public bool PlaylistLoopRange => Version > 89;
    public bool TransitionCounts => Version > 72;
    public bool JumpToType => Version > 132;
    public bool OldSwitchAssociations => Version <= 72;

    // ---- effects and devices

    public bool EffectInitValues => Version > 89 && Version <= 126;
    public bool EffectStatesAndValues => Version > 126;
    public bool DeviceSlots => Version > 136;
}
// loc-ignore-end
