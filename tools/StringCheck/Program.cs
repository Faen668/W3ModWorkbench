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
//     --strict       count single words in C# as text wherever they are, for a sweep by eye (it lists names and keys too)
//
// Text that isn't for people to read (code samples, file names, the licence) is marked so the check passes over it:
// in C#, a "// loc-ignore" comment on the line, or the lines between "// loc-ignore-start" and "// loc-ignore-end";
// in XAML, a <!-- loc-ignore --> comment on the line before the element.

var options = args.ToHashSet(StringComparer.OrdinalIgnoreCase);
bool strict = options.Contains("--strict");
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
        var names = source.SelectMany(Detection.PlaceholderNames).ToHashSet();
        foreach (string value in values)
            foreach (string name in Detection.PlaceholderNames(value).Where(n => !names.Contains(n)))
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
            if (Detection.IsText(value, uiContext: true)) problems.Add((rel, Line(attribute), "xaml", $"{name}=\"{value}\""));
        }
        if (!ignored)
            foreach (var text in element.Nodes().OfType<XText>())
                if (text is not XCData && Detection.IsText(text.Value.Trim(), uiContext: true) && element.Name.LocalName is not ("Code" or "String" or "Style" or "FontFamily"))
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
    var ignoredLines = Detection.IgnoredLines(source);
    foreach (var node in tree.GetRoot().DescendantNodes())
    {
        string? text = Detection.TextOf(node);
        if (text is null) continue;
        int line = lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
        // a key given to L.T: it must be in en.json
        if (Detection.KeyArgument(node) is { } key)
        {
            used.Add(key);
            if (!english.ContainsKey(key)) problems.Add((rel, line, "key", $"\"{key}\" isn't in en.json"));
            continue;
        }
        // a key named anywhere else (a key picked by a test, a section's key in a list) counts as used
        if (english.ContainsKey(text)) { used.Add(text); continue; }
        if (ignoredLines.Contains(line) || Detection.NotShown(node)) continue;
        if (Detection.IsText(text, strict || Detection.UiContext(node))) problems.Add((rel, line, "cs", text.Replace("\n", "\\n").Replace("\r", "")));
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
    foreach (Match m in Regex.Matches(value, @"\{l:T\s+(?:Key=)?([^\s}]+)\s*\}|LocFormat\.Instance\},\s*ConverterParameter=([\w.]+)"))
    {
        string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        used.Add(key);
        if (!english.ContainsKey(key)) problems.Add((rel, line, "key", $"\"{key}\" isn't in en.json"));
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
