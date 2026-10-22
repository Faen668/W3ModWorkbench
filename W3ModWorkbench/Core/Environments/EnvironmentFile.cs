using System.IO;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Core.Scripts.Standalone;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Environments;

/// <summary>
/// A setting of an environment: a curve over the day, a switch, a number, a texture or another value, by its name and
/// where it is (m_globalLight › sunColor). <see cref="Set"/> is false for one the file leaves at the engine's default,
/// which only its type is known of.
/// </summary>
public sealed class EnvSetting
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public RedCurve? Curve { get; init; }
    public object? Value { get; init; }
    public bool Set { get; init; } = true;

    /// <summary>The name in words: sunColor as "Sun color", m_globalLight as "Global light".</summary>
    public string Title => EnvironmentFile.Words(Name);
}

/// <summary>A group of settings (one of the parameters' structs), its settings and the groups in it.</summary>
public sealed class EnvGroup
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public List<EnvGroup> Groups { get; } = [];
    public List<EnvSetting> Settings { get; } = [];
    /// <summary>Whether the file sets anything in it.</summary>
    public bool Set { get; init; } = true;

    public string Title => EnvironmentFile.Words(Name);

    /// <summary>Its settings and every group's in it.</summary>
    public IEnumerable<EnvSetting> All() => Settings.Concat(Groups.SelectMany(g => g.All()));

    /// <summary>Whether the group is turned on, by its own activated switch, or null when it has none.</summary>
    public bool? Activated => Settings.FirstOrDefault(s => s.Name == "activated") is { Set: true, Value: bool on } ? on : null;
}

/// <summary>
/// An environment definition (.env, a CEnvironmentDefinition): how a world looks through the day, as a big struct of
/// parameters (CAreaEnvironmentParams) in groups (global light, fog, sky, tone mapping, colour balance, bloom, water,
/// wind…), most of them curves over the 24 hours. Worlds name one in their environment parameters; area environment
/// components blend others in where the player is. REDkit edits them in its Environment System panel.
/// </summary>
public sealed class EnvironmentFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required EnvGroup Root { get; init; }

    public static bool IsEnvironment(string path) => path.EndsWith(".env", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<Dictionary<string, RttiClass>> Types = new(() =>
        Rtti.LoadBuiltIn().Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First()));

    public static EnvironmentFile Read(byte[] data) => Read(data, "envParams");

    /// <summary>A definition with one of its parameter sets as the root: envParams, envParamsInteriors or envParamsCaves.</summary>
    public static EnvironmentFile Read(byte[] data, string parameterSet)
    {
        var file = Cr2wFile.Read(data);
        var definition = file.First("CEnvironmentDefinition") ?? throw new InvalidDataException(L.T("EnvironmentFile.NotEnvironment"));
        var parameters = definition.Struct(parameterSet) ?? new RedObject("CAreaEnvironmentParams");
        return new EnvironmentFile { File = file, Resource = definition, Root = Group(parameterSet, parameters, "") };
    }

    /// <summary>
    /// The groups and settings of a parameters struct: the file's own, then those its type has that the file leaves
    /// at their defaults, from the engine's types.
    /// </summary>
    public static EnvGroup Group(string name, RedObject values, string path, bool set = true)
    {
        var group = new EnvGroup { Name = name, Type = values.ClassName, Set = set };
        var seen = new HashSet<string>();
        foreach (var (property, type, value) in values.Properties)
        {
            seen.Add(property);
            string at = path.Length == 0 ? property : $"{path} › {property}";
            if (value is RedObject inner && RedCurve.FromSimpleCurve(inner) is { } curve)
                group.Settings.Add(new EnvSetting { Name = property, Path = at, Type = type, Curve = curve, Value = inner });
            else if (value is RedObject child && IsGroup(child.ClassName))
                group.Groups.Add(Group(property, child, at));
            else
                group.Settings.Add(new EnvSetting { Name = property, Path = at, Type = type, Value = value });
        }
        if (Types.Value.TryGetValue(values.ClassName, out var cls))
            foreach (var (property, type) in cls.Properties.Where(p => !seen.Contains(p.Name)))
            {
                string at = path.Length == 0 ? property : $"{path} › {property}";
                if (IsGroup(type)) group.Groups.Add(Group(property, new RedObject(type), at, set: false));
                else group.Settings.Add(new EnvSetting { Name = property, Path = at, Type = type, Set = false });
            }
        // the engine's order: switches first, then the rest as declared
        group.Settings.Sort((a, b) => IsSwitch(b).CompareTo(IsSwitch(a)));
        return group;
    }

    private static bool IsSwitch(EnvSetting s) => s.Type == "Bool";

    /// <summary>Whether a struct is a group of settings rather than a value (a curve, a vector, a colour).</summary>
    private static bool IsGroup(string type) =>
        type.StartsWith("CEnv", StringComparison.Ordinal) || type.StartsWith("SEnv", StringComparison.Ordinal) || type == "CAreaEnvironmentParams"
        || type.StartsWith("SDayPoint", StringComparison.Ordinal) || type.StartsWith("SGlobalSpeedTree", StringComparison.Ordinal)
        || type.StartsWith("SWorldRenderSettings", StringComparison.Ordinal);

    /// <summary>A name in words: m_globalLight as "Global light", sunColor as "Sun color", fxSkyNoonAlpha as "Fx sky noon alpha".</summary>
    public static string Words(string name)
    {
        string bare = name.StartsWith("m_", StringComparison.Ordinal) ? name[2..] : name;
        string spaced = Regex.Replace(bare, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[a-zA-Z])(?=[0-9])", " ").Replace('_', ' ');
        if (spaced.Length == 0) return name;
        string words = char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
        return Regex.Replace(words, @"\b(ssao|nv|ms|pbr|ibl|rtx|rtao|gtao|pt|dof|gi|fx|lod)\b", m => m.Value.ToUpperInvariant(), RegexOptions.IgnoreCase);
    }
}
