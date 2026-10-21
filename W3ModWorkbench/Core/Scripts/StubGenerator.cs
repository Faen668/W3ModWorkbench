using System.IO;
using System.Text;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A function or event the game's scripts declare, with what's needed to write a mod's version of it.</summary>
/// <param name="Owner">The class it belongs to, as annotations name it. A state's is its compiled name, such as CR4PlayerStateIdle.</param>
/// <param name="Header">The declaration up to its body: specifiers, name, parameters and return type.</param>
/// <param name="Body">The original body, braces included.</param>
public sealed record GameFunction(
    string Owner, string Name, bool IsEvent, string Header, string Body, IReadOnlyList<string> Parameters,
    string? ReturnType, string File, int Line)
{
    public string Title => $"{Owner}.{Name}";

    /// <summary>The parameters as a reader wants to see them: name : type, one after another.</summary>
    public string Signature => Header[(Header.IndexOf('(') is var open and >= 0 ? open : 0)..];
}

/// <summary>A class or state the game declares, for adding methods and fields to.</summary>
public sealed record GameClass(string Name, string? Base, bool IsState);

public enum StubKind { Wrap, Replace, AddMethod, AddField }

/// <summary>
/// Reads the game's own scripts for the functions mods wrap, replace or add to, and writes the starting code for
/// each: the annotation, the exact signature and a call to the original.
/// </summary>
public sealed class StubGenerator
{
    private static readonly Dictionary<string, Task<StubGenerator>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GameFunction> Functions { get; }
    public IReadOnlyList<GameClass> Classes { get; }

    private StubGenerator(List<GameFunction> functions, List<GameClass> classes)
    {
        Functions = functions;
        Classes = classes;
    }

    /// <summary>Reads the scripts of a game folder once, in the background, and keeps them until the app closes.</summary>
    public static Task<StubGenerator> LoadAsync(string scriptsFolder)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(scriptsFolder, out var task))
                Cache[scriptsFolder] = task = Task.Run(() => Read(scriptsFolder));
            return task;
        }
    }

    private static StubGenerator Read(string folder)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StubGenerator([], []);
        }
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

        var functions = new List<GameFunction>[paths.Length];
        var classes = new List<GameClass>[paths.Length];
        Parallel.For(0, paths.Length, i =>
        {
            functions[i] = [];
            classes[i] = [];
            string path = paths[i];
            try
            {
                string text = TextDecoding.Decode(File.ReadAllBytes(path));
                var file = new ScriptFile
                {
                    Key = path,
                    Path = Path.GetRelativePath(folder, path),
                    Source = "Game scripts",
                    SourceKey = folder,
                    Read = () => File.ReadAllBytes(path),
                };
                var parsed = ScriptParser.Parse(text, file);
                foreach (var s in parsed.Symbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.State))
                    classes[i].Add(new GameClass(s.TypeKey, s.Base, s.Kind == SymbolKind.State));
                foreach (var f in parsed.Functions)
                {
                    if (f.Symbol.Annotation is not null || f.Symbol.Owner is null) continue;
                    if (Describe(text, parsed, f) is { } fn) functions[i].Add(fn);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a script that can't be read has nothing to offer
            }
        });
        return new StubGenerator([.. functions.SelectMany(f => f)], [.. classes.SelectMany(c => c)]);
    }

    private static GameFunction? Describe(string text, ParsedScript parsed, FunctionScope f)
    {
        var symbol = f.Symbol;
        string code = parsed.Code;

        // the declaration starts after the previous statement or block, and runs to the opening brace
        int start = symbol.Offset;
        while (start > 0 && code[start - 1] is not (';' or '{' or '}')) start--;
        // from the blanked code, so comments between statements don't end up in it
        string header = Collapse(code[start..f.BodyStart]);
        if (header.Length == 0) return null;
        string body = text[f.BodyStart..Math.Min(f.BodyEnd, text.Length)];

        string parameterText = code[(f.ParamsStart + 1)..Math.Max(f.ParamsStart + 1, f.ParamsEnd)];
        return new GameFunction(symbol.Owner!, symbol.Name, symbol.Kind == SymbolKind.Event, header, body,
            ParameterNames(parameterText), symbol.Type, parsed.File.Path, symbol.Line);
    }

    /// <summary>"a, b : int, optional c : float" → a, b, c.</summary>
    internal static List<string> ParameterNames(string parameters)
    {
        var names = new List<string>();
        var chunks = new List<string>();
        int depth = 0, from = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                chunks.Add(parameters[from..i]);
                from = i + 1;
            }
        }
        chunks.Add(parameters[from..]);

        foreach (string chunk in chunks)
        {
            string left = chunk.Contains(':') ? chunk[..chunk.IndexOf(':')] : chunk;
            string? name = left.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString().Replace("( ", "(").Replace(" )", ")");
    }

    // ---------------------------------------------------------------- searching

    /// <summary>
    /// Functions matching a search: every word has to be in the class or function name, or in the file it is in.
    /// "Owner.Name" searches that class's function.
    /// </summary>
    public IEnumerable<GameFunction> Search(string query)
    {
        string[] words = query.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Functions;
        return Functions
            .Where(f => words.All(w => f.Owner.Contains(w, StringComparison.OrdinalIgnoreCase)
                                       || f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => words.Any(w => f.Name.Equals(w, StringComparison.OrdinalIgnoreCase) || f.Owner.Equals(w, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(f => f.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<GameClass> SearchClasses(string query)
    {
        string[] words = query.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        return Classes
            .Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => words.Any(w => c.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- writing

    public sealed record Options(bool CommentOriginal = true, bool BodyForReplace = true);

    /// <summary>The starting code for changing a function.</summary>
    public static string Write(GameFunction f, StubKind kind, Options? options = null)
    {
        options ??= new Options();
        var sb = new StringBuilder();
        bool returns = !string.IsNullOrEmpty(f.ReturnType) && !f.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase);
        string call = $"wrappedMethod({string.Join(", ", f.Parameters)})";

        if (options.CommentOriginal) sb.Append("// from ").Append(f.File).Append(", line ").Append(f.Line).Append('\n');
        switch (kind)
        {
            case StubKind.Wrap:
                sb.Append("@wrapMethod(").Append(f.Owner).Append(")\n").Append(f.Header).Append("\n{\n");
                if (returns)
                {
                    sb.Append("\tvar result : ").Append(f.ReturnType).Append(";\n\n");
                    sb.Append("\t// runs before the original\n\n");
                    sb.Append("\tresult = ").Append(call).Append(";\n\n");
                    sb.Append("\t// runs after the original; change result to change what is returned\n\n");
                    sb.Append("\treturn result;\n");
                }
                else
                {
                    sb.Append("\t// runs before the original\n\n");
                    sb.Append('\t').Append(call).Append(";\n\n");
                    sb.Append("\t// runs after the original\n");
                }
                sb.Append("}\n");
                break;

            case StubKind.Replace:
                sb.Append("@replaceMethod(").Append(f.Owner).Append(")\n").Append(f.Header).Append('\n');
                if (options.BodyForReplace) sb.Append(Dedent(f.Body)).Append('\n');
                else
                {
                    sb.Append("{\n");
                    if (returns) sb.Append("\tvar result : ").Append(f.ReturnType).Append(";\n\n\treturn result;\n");
                    sb.Append("}\n");
                }
                break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The function under an annotation already typed (<c>@wrapMethod(CR4Player)</c>): its signature, and a body that
    /// calls the original (a wrap) or is the original's (a replace), with no comment above and no annotation. The
    /// place to type next is marked <c>$0</c>, for a snippet.
    /// </summary>
    public static string WriteUnderAnnotation(GameFunction f, StubKind kind)
    {
        string text = Write(f, kind, new Options(CommentOriginal: false));
        text = text[(text.IndexOf('\n') + 1)..];
        // the cursor goes where the mod's own code starts
        int before = text.IndexOf("// runs before the original", StringComparison.Ordinal);
        return before >= 0 ? text[..before] + "$0" + text[(before + "// runs before the original".Length)..] : text.TrimEnd('\n') + "$0";
    }

    /// <summary>Takes the indentation the closing brace has off every line, so the body starts at the left margin.</summary>
    private static string Dedent(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        string last = lines[^1];
        int indent = 0;
        while (indent < last.Length && (last[indent] == ' ' || last[indent] == '\t')) indent++;
        if (indent == 0) return string.Join('\n', lines);
        for (int i = 1; i < lines.Length; i++)
        {
            int n = 0;
            while (n < indent && n < lines[i].Length && (lines[i][n] == ' ' || lines[i][n] == '\t')) n++;
            lines[i] = lines[i][n..];
        }
        return string.Join('\n', lines);
    }

    /// <summary>The starting code for adding a method to a class.</summary>
    public static string WriteAddMethod(string owner, string name = "MyMethod") =>
        $"@addMethod({owner})\nfunction {name}()\n{{\n\t\n}}\n";

    /// <summary>The starting code for adding a field to a class.</summary>
    public static string WriteAddField(string owner, string name = "myField", string type = "bool") =>
        $"@addField({owner})\nvar {name} : {type};\n";
}
