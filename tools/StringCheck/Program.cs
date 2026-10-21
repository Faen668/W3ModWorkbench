using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// The localisation check: user-facing text written straight into the app's XAML and C# rather than taken from its
// string tables, keys used that the English table hasn't got, and translations that don't match the English (keys it
// hasn't, placeholders it doesn't name). Exit code 0 when all is well, 1 when something was found.
//
//   dotnet run --project tools/StringCheck [-- options]
//     --summary      a count per file instead of every line
//     --file <part>  only files whose path has <part> in it
//     --unused       also list keys in en.json nothing uses
//
// Text that isn't for people to read (code samples, file names, the licence) is marked so the check passes over it:
// in C#, a "// loc-ignore" comment on the line, or the lines between "// loc-ignore-start" and "// loc-ignore-end";
// in XAML, a <!-- loc-ignore --> comment on the line before the element.

var options = args.ToHashSet(StringComparer.OrdinalIgnoreCase);
string? only = args.SkipWhile(a => a != "--file").Skip(1).FirstOrDefault();
string root = FindRoot();
string app = Path.Combine(root, "W3ModWorkbench");
string tables = Path.Combine(app, "Localisation", "Strings");

var english = ReadTable(Path.Combine(tables, "en.json"), out var englishProblems);
var problems = new List<(string File, int Line, string Kind, string Text)>();
foreach (var p in englishProblems) problems.Add(("Localisation/Strings/en.json", 0, "table", p));
var used = new HashSet<string>(StringComparer.Ordinal);

// ---------------------------------------------------------------- the other languages' tables

foreach (string file in Directory.EnumerateFiles(tables, "*.json").Where(f => !Path.GetFileName(f).Equals("en.json", StringComparison.OrdinalIgnoreCase)))
{
    string rel = Rel(file);
    var table = ReadTable(file, out var tableProblems);
    foreach (var p in tableProblems) problems.Add((rel, 0, "table", p));
    foreach (var (key, values) in table)
    {
        if (!english.TryGetValue(key, out var source)) { problems.Add((rel, 0, "table", $"{key}: not in en.json (renamed or removed?)")); continue; }
        var names = source.SelectMany(PlaceholderNames).ToHashSet();
        foreach (string value in values)
            foreach (string name in PlaceholderNames(value).Where(n => !names.Contains(n)))
                problems.Add((rel, 0, "table", $"{key}: {{{name}}} isn't a placeholder the English names ({string.Join(", ", names.Select(n => "{" + n + "}"))})"));
    }
}

// ---------------------------------------------------------------- XAML

string[] xamlProperties = ["Text", "Content", "Header", "ToolTip", "Title", "Description", "PlaceholderText", "Watermark", "Label"];
foreach (string file in SourceFiles("*.xaml"))
{
    string rel = Rel(file);
    if (only is not null && !rel.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
    XDocument doc;
    try { doc = XDocument.Load(file, LoadOptions.SetLineInfo); }
    catch (XmlException e) { problems.Add((rel, e.LineNumber, "xaml", "doesn't parse: " + e.Message)); continue; }
    foreach (var element in doc.Descendants())
    {
        bool ignored = element.PreviousNode is XComment c && c.Value.Contains("loc-ignore", StringComparison.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes())
        {
            string name = attribute.Name.LocalName;
            string value = attribute.Value;
            CheckKeys(rel, Line(attribute), value);
            // a Setter's Value for one of the text properties
            bool textProperty = xamlProperties.Contains(name)
                || (element.Name.LocalName == "Setter" && name == "Value" && xamlProperties.Contains((string?)element.Attribute("Property") ?? ""));
            if (!textProperty || ignored || value.StartsWith('{')) continue;
            if (IsText(value, uiContext: true)) problems.Add((rel, Line(attribute), "xaml", $"{name}=\"{value}\""));
        }
        if (!ignored)
            foreach (var text in element.Nodes().OfType<XText>())
                if (text is not XCData && IsText(text.Value.Trim(), uiContext: true) && element.Name.LocalName is not ("Code" or "String" or "Style"))
                    problems.Add((rel, Line(text), "xaml", text.Value.Trim()));
    }
}

// ---------------------------------------------------------------- C#

foreach (string file in SourceFiles("*.cs"))
{
    string rel = Rel(file);
    if (only is not null && !rel.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
    // the engine's formats, and the command line (a tool's own English usage), aren't the app's windows
    if (rel.StartsWith("W3ModWorkbench/Core/", StringComparison.Ordinal) || rel == "W3ModWorkbench/CommandLine.cs") continue;
    string source = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(source);
    var lines = tree.GetText().Lines;
    var ignoredLines = IgnoredLines(source);
    foreach (var node in tree.GetRoot().DescendantNodes())
    {
        string? text = node switch
        {
            LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) && l.Parent is not InterpolationSyntax => l.Token.ValueText,
            InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{0}")),
            _ => null,
        };
        if (text is null) continue;
        int line = lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
        // a key given to L.T: it must be in en.json
        if (KeyArgument(node) is { } key)
        {
            used.Add(key);
            if (!english.ContainsKey(key)) problems.Add((rel, line, "key", $"\"{key}\" isn't in en.json"));
            continue;
        }
        if (ignoredLines.Contains(line) || NotShown(node)) continue;
        if (IsText(text, UiContext(node))) problems.Add((rel, line, "cs", text.Replace("\n", "\\n").Replace("\r", "")));
    }
}

// ---------------------------------------------------------------- the report

var found = problems.Where(p => p.Kind != "unused").ToList();
if (options.Contains("--summary"))
{
    foreach (var group in found.GroupBy(p => p.File).OrderByDescending(g => g.Count()))
        Console.WriteLine($"{group.Count(),5}  {group.Key}");
}
else
{
    foreach (var p in found.OrderBy(p => p.File).ThenBy(p => p.Line))
        Console.WriteLine(p.Line > 0 ? $"{p.File}({p.Line}): {p.Kind}: {p.Text}" : $"{p.File}: {p.Kind}: {p.Text}");
}
if (options.Contains("--unused"))
    foreach (string key in english.Keys.Where(k => !used.Contains(k)).Order())
        Console.WriteLine($"unused: {key}");
Console.WriteLine(found.Count == 0
    ? $"Localisation check passed: no hard-coded text, {english.Count:N0} English strings."
    : $"Localisation check: {found.Count:N0} to look at ({found.Count(p => p.Kind is "xaml" or "cs"):N0} hard-coded strings) in {found.Select(p => p.File).Distinct().Count():N0} files.");
return found.Count == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers

string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "W3ModWorkbench", "W3ModWorkbench.csproj"))) return dir.FullName;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "W3ModWorkbench", "W3ModWorkbench.csproj"))) return dir.FullName;
    throw new InvalidOperationException("Run this from inside the W3 Mod Workbench repository.");
}

string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

IEnumerable<string> SourceFiles(string pattern) =>
    Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories)
        .Where(f => !Rel(f).Split('/').Any(part => part is "bin" or "obj"));

static int Line(IXmlLineInfo info) => info.HasLineInfo() ? info.LineNumber : 0;

void CheckKeys(string rel, int line, string value)
{
    foreach (Match m in Regex.Matches(value, @"\{l:T\s+(?:Key=)?([^\s}]+)\s*\}"))
    {
        used.Add(m.Groups[1].Value);
        if (!english.ContainsKey(m.Groups[1].Value)) problems.Add((rel, line, "key", $"\"{m.Groups[1].Value}\" isn't in en.json"));
    }
}

/// <summary>A table's keys and their values (one, or each plural form), and anything wrong with it.</summary>
static Dictionary<string, List<string>> ReadTable(string path, out List<string> problems)
{
    problems = [];
    var table = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('$')) continue;
            if (table.ContainsKey(property.Name)) problems.Add($"{property.Name}: given twice");
            table[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => [property.Value.GetString()!],
                JsonValueKind.Object => [.. property.Value.EnumerateObject().Select(f => f.Value.GetString() ?? "")],
                _ => [],
            };
            if (property.Value.ValueKind == JsonValueKind.Object)
                foreach (var form in property.Value.EnumerateObject().Where(f => f.Name is not ("zero" or "one" or "two" or "few" or "many" or "other")))
                    problems.Add($"{property.Name}: \"{form.Name}\" isn't a plural form (zero, one, two, few, many, other)");
        }
    }
    catch (JsonException e)
    {
        problems.Add("doesn't parse: " + e.Message);
    }
    return table;
}

static List<string> PlaceholderNames(string text) =>
    [.. Regex.Matches(text, @"(?<!\{)\{([A-Za-z_][A-Za-z0-9_]*)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})").Select(m => m.Groups[1].Value).Distinct()];

/// <summary>Lines with a loc-ignore comment, and those between loc-ignore-start and loc-ignore-end.</summary>
static HashSet<int> IgnoredLines(string source)
{
    var set = new HashSet<int>();
    var lines = source.Replace("\r", "").Split('\n');
    bool inside = false;
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Contains("loc-ignore-start", StringComparison.Ordinal)) inside = true;
        if (inside || lines[i].Contains("loc-ignore", StringComparison.Ordinal)) set.Add(i + 1);
        if (lines[i].Contains("loc-ignore-end", StringComparison.Ordinal)) inside = false;
    }
    return set;
}

/// <summary>The key when the literal is the first argument of L.T(…) or a Bind(…, key).</summary>
static string? KeyArgument(SyntaxNode node)
{
    if (node is not LiteralExpressionSyntax literal || literal.Parent is not ArgumentSyntax argument
        || argument.Parent is not ArgumentListSyntax list || list.Parent is not InvocationExpressionSyntax call) return null;
    string callee = call.Expression.ToString();
    if ((callee is "L.T" or "Localisation.L.T") && list.Arguments.IndexOf(argument) == 0) return literal.Token.ValueText;
    if (callee.EndsWith(".Bind", StringComparison.Ordinal) && list.Arguments.IndexOf(argument) == 1) return literal.Token.ValueText;
    return null;
}

/// <summary>Where a string is code rather than text: an attribute, a comparison, a case, a resource key, a path…</summary>
static bool NotShown(SyntaxNode node)
{
    for (var parent = node.Parent; parent is not null; parent = parent.Parent)
    {
        switch (parent)
        {
            case AttributeSyntax:
            case CaseSwitchLabelSyntax or ConstantPatternSyntax:
                return true;
            case SwitchExpressionArmSyntax arm when arm.Pattern.Span.Contains(node.Span):
                return true;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression):
                return true;
            case ElementAccessExpressionSyntax e when e.ArgumentList.Span.Contains(node.Span):
                return true;
            case InvocationExpressionSyntax call:
            {
                string callee = call.Expression.ToString();
                string name = callee[(callee.LastIndexOf('.') + 1)..];
                if (Lists.NotShownCalls.Contains(name) || Lists.NotShownPrefixes.Any(p => callee.StartsWith(p, StringComparison.Ordinal))) return true;
                return false; // the call's result may well be shown: stop looking
            }
            case ObjectCreationExpressionSyntax create when create.Type.ToString() is "Regex" or "Uri" or "XElement" or "XAttribute" or "XName" or "ProcessStartInfo" or "PropertyPath" or "FontFamily":
                return true;
            case StatementSyntax or MemberDeclarationSyntax:
                return false;
        }
    }
    return false;
}

/// <summary>Whether a string is put somewhere it's read: a Text, a title, a message.</summary>
static bool UiContext(SyntaxNode node)
{
    for (var parent = node.Parent; parent is not null; parent = parent.Parent)
    {
        switch (parent)
        {
            case AssignmentExpressionSyntax a:
                string target = a.Left.ToString();
                return Lists.UiNames.Any(n => target.EndsWith(n, StringComparison.Ordinal));
            case ArgumentSyntax { NameColon: { } colon } when Lists.UiNames.Any(n => colon.Name.Identifier.Text.Equals(n, StringComparison.OrdinalIgnoreCase)):
                return true;
            case InvocationExpressionSyntax call:
                string callee = call.Expression.ToString();
                return Lists.UiCalls.Any(c => callee.EndsWith(c, StringComparison.Ordinal));
            case StatementSyntax or MemberDeclarationSyntax:
                return false;
        }
    }
    return false;
}

/// <summary>
/// Whether a string reads as text for people: words with spaces between them, or in a place text goes, a word. Not a
/// file name, a path, a key or an identifier, an icon glyph or a format with no words.
/// </summary>
static bool IsText(string text, bool uiContext)
{
    string t = text.Trim();
    if (Lists.Names.Contains(t)) return false;
    if (t.Length < 2 || !t.Any(char.IsLetter)) return false;
    if (t.All(c => c is >= '\uE000' and <= '\uF8FF' || char.IsWhiteSpace(c) || char.IsPunctuation(c))) return false;
    if (Regex.IsMatch(t, @"^[\w\-.]+\.[A-Za-z0-9]{1,8}$")) return false; // a file name
    if (Regex.IsMatch(t, @"^(\*?\.\w+;?)+$")) return false; // a file pattern
    if (t.Contains("://", StringComparison.Ordinal)) return t.Contains(' ') && Regex.IsMatch(t, @"[A-Za-z]{2,} [A-Za-z]{2,} [A-Za-z]"); // a link, unless in a sentence
    if (Regex.IsMatch(t, @"^[A-Za-z]:\\|^\\\\|^%\w+%")) return false; // a path
    bool words = Regex.IsMatch(t, @"\p{L}{2,}[,.:;!?'’)]*\s+[(\p{L}]");
    if (words) return true;
    // one word: only where text goes, and not an identifier-looking key (MyKey, my_key, my-key) or a code
    if (!uiContext) return false;
    return !Regex.IsMatch(t, @"^[a-z]+[A-Z_]\w*$|^[A-Z][a-z]+[A-Z]\w*$|^[\w]+[_\-][\w\-]+$|^\{0\}$");
}

static class Lists
{
    /// <summary>Calls whose string arguments are names, keys, paths or patterns, not text.</summary>
    public static readonly HashSet<string> NotShownCalls = new(StringComparer.Ordinal)
    {
        "Combine", "GetFullPath", "GetFileName", "GetExtension", "ChangeExtension", "Exists", "ReadAllText", "ReadAllBytes", "WriteAllText",
        "EnumerateFiles", "GetFiles", "EnumerateDirectories", "GetDirectories", "CreateDirectory", "Delete", "Move", "Copy",
        "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Replace", "Split", "Trim", "TrimStart", "TrimEnd", "Equals", "Compare",
        "IsMatch", "Match", "Matches", "Escape",
        "GetManifestResourceStream", "FindResource", "TryFindResource", "SetResourceReference", "GetType", "GetMethod", "GetField",
        "GetProperty", "GetValue", "GetString", "GetProperty", "TryGetProperty", "GetValueOrDefault", "TryGetValue", "ContainsKey", "Remove",
        "Element", "Elements", "Attribute", "Attributes", "Descendants", "Parse", "TryParse", "ParseExact", "TryParseExact", "ToString",
        "WriteLine", "Write", "Assert", "Fail", "Start", "GetEnvironmentVariable", "OpenSubKey", "CreateSubKey", "SetValue", "DeleteSubKey",
        "nameof", "Navigate", "Find", "FirstOrDefault", "Lookup", "Resolve", "Open", "Register", "SetBinding", "Binding",
        "PadLeft", "PadRight",
    };

    public static readonly string[] NotShownPrefixes = ["Debug.", "Trace.", "Console.", "Regex.", "Path.", "File.", "Directory.", "Log."];

    /// <summary>Property and argument names text is given to.</summary>
    public static readonly string[] UiNames =
        ["Text", "Content", "Header", "ToolTip", "Title", "Caption", "Message", "message", "title", "Summary", "Hint", "Label", "Note", "Status", "Description", "primary", "secondary", "Filter"];

    /// <summary>Names that are the same in every language: the app's, the game's.</summary>
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal) { "W3 Mod Workbench", "The Witcher 3" };

    /// <summary>Calls whose arguments are shown.</summary>
    public static readonly string[] UiCalls = ["ShowMessage", "Show", "SetStatus", "Status", "Confirm", "Ask", "AddItem", "MenuItem", "Item", "Note", "Hint"];
}
