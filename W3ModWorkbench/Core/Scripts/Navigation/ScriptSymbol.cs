namespace W3ModWorkbench.Core.Scripts.Navigation;

public enum SymbolKind { Class, State, Struct, Enum, EnumMember, Function, Event, Field, Parameter, Local }

/// <summary>A .ws file that definitions can point into: one in an open archive, or one of the game's own scripts.</summary>
public sealed class ScriptFile
{
    /// <summary>Unique across everything open: the archive and entry path, or the full path on disk.</summary>
    public required string Key { get; init; }

    /// <summary>The path shown in the preview title, relative to its archive or the game's scripts folder.</summary>
    public required string Path { get; init; }

    /// <summary>Where it comes from, e.g. the mod and bundle name, or "Game scripts".</summary>
    public required string Source { get; init; }

    /// <summary>Groups files of one archive (or the game), so the same definition is only listed once per source.</summary>
    public required string SourceKey { get; init; }

    public required Func<byte[]> Read { get; init; }

    private string? _text;

    /// <summary>The file's text, read once and kept, for searches that look through every file.</summary>
    public string Text => _text ??= TextDecoding.Decode(Read());
}

/// <summary>
/// A declaration found in a script: a type, an enum value, a function or event, or a variable.
/// <see cref="Owner"/> is the class it belongs to (a state's compiled name for state members, the
/// annotation's target for <c>@addMethod</c> and friends), or null for globals.
/// </summary>
public sealed record ScriptSymbol(
    string Name,
    SymbolKind Kind,
    string? Owner,
    string? Type,
    ScriptFile File,
    int Offset,
    int Line)
{
    /// <summary>For classes and states: the base type's key. For states: the statemachine class is <see cref="Owner"/>.</summary>
    public string? Base { get; init; }

    /// <summary>wrapMethod, replaceMethod, addMethod or addField when the declaration is an annotation.</summary>
    public string? Annotation { get; init; }

    /// <summary>The owner as people write it: W3PlayerWitcher.Combat for a member of that state.</summary>
    public string? OwnerTitle { get; init; }

    public bool IsType => Kind is SymbolKind.Class or SymbolKind.State or SymbolKind.Struct or SymbolKind.Enum;
    public bool IsMember => Kind is SymbolKind.Function or SymbolKind.Event or SymbolKind.Field;

    /// <summary>The name other code uses for this type. States compile to OwnerStateName.</summary>
    public string TypeKey => Kind == SymbolKind.State ? StateKey(Owner!, Name) : Name;

    public static string StateKey(string owner, string state) => owner + "State" + state;

    /// <summary>How the declaration reads in a list of choices, e.g. "@wrapMethod CR4Player.OnSpawned".</summary>
    public string Title
    {
        get
        {
            string owner = Kind == SymbolKind.State ? $"{Owner}."
                : (IsMember || Kind == SymbolKind.EnumMember) && Owner is not null ? $"{OwnerTitle ?? Owner}."
                : "";
            string prefix = Annotation is null ? Kind switch
            {
                SymbolKind.Class => "class ",
                SymbolKind.State => "state ",
                SymbolKind.Struct => "struct ",
                SymbolKind.Enum => "enum ",
                SymbolKind.Event => "event ",
                SymbolKind.Field or SymbolKind.Local or SymbolKind.Parameter => "var ",
                _ => "",
            } : $"@{Annotation} ";
            string suffix = Kind is SymbolKind.Function or SymbolKind.Event ? "()" : "";
            return prefix + owner + Name + suffix;
        }
    }
}
