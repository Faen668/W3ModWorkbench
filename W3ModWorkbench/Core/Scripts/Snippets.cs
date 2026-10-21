using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>
/// A piece of WitcherScript to insert: <c>${name}</c> marks a place to type, filled with its name to start with, and
/// <c>$0</c> where the caret goes after. Typing <see cref="Prefix"/> and pressing Tab inserts it too.
/// </summary>
/// <param name="File">The file it came from, for the person's own; null for a built-in one.</param>
public sealed record Snippet(string Prefix, string Title, string Body, string? File = null)
{
    public bool IsOwn => File is not null;
}

/// <summary>A snippet ready to go in the text: its text indented to fit, and the places to type in it.</summary>
public sealed record ExpandedSnippet(string Text, IReadOnlyList<(int Offset, int Length)> Fields, int Caret);

/// <summary>
/// The snippets the script editor offers (Ctrl+J, or a prefix and Tab): common WitcherScript patterns built in, and any
/// .ws files the person keeps in the snippets folder, each a snippet named after the file.
/// </summary>
public static partial class Snippets
{
    /// <summary>%AppData%\W3ModWorkbench\snippets: one .ws file per snippet. A first line starting with // is its title.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "snippets");

    // the titles are looked up each time, so they follow the app's language; the bodies are WitcherScript
    public static IReadOnlyList<Snippet> BuiltIn =>
    // loc-ignore-start
    [
        new("wrap", L.T("Snippets.Wrap"), """
            @wrapMethod(${Class}) function ${Name}(${parameters})
            {
                var result : ${type};

                result = wrappedMethod(${arguments});
                $0
                return result;
            }
            """),
        new("replace", L.T("Snippets.Replace"), """
            @replaceMethod(${Class}) function ${Name}(${parameters})
            {
                $0
            }
            """),
        new("addmethod", L.T("Snippets.Addmethod"), """
            @addMethod(${Class}) function ${Name}()
            {
                $0
            }
            """),
        new("addfield", L.T("Snippets.Addfield"), """
            @addField(${Class})
            var ${name} : ${type};
            """),
        new("onspawned", L.T("Snippets.Onspawned"), """
            @wrapMethod(CR4Player) function OnSpawned(spawnData : SEntitySpawnData)
            {
                wrappedMethod(spawnData);
                $0
            }
            """),
        new("class", L.T("Snippets.Class"), """
            class ${Name} extends ${CObject}
            {
                $0
            }
            """),
        new("statemachine", L.T("Snippets.Statemachine"), """
            statemachine class ${Name} extends ${CEntity}
            {
                public function Start()
                {
                    GotoState('Running');
                }
            }

            state Running in ${Name}
            {
                event OnEnterState(prevStateName : name)
                {
                    super.OnEnterState(prevStateName);
                    Loop();
                }

                entry function Loop()
                {
                    while (true)
                    {
                        $0
                        Sleep(1.0);
                    }
                }
            }
            """),
        new("state", L.T("Snippets.State"), """
            state ${Name} in ${Owner}
            {
                event OnEnterState(prevStateName : name)
                {
                    super.OnEnterState(prevStateName);
                    $0
                }

                event OnLeaveState(nextStateName : name)
                {
                    super.OnLeaveState(nextStateName);
                }
            }
            """),
        new("struct", L.T("Snippets.Struct"), """
            struct ${Name}
            {
                var ${field} : ${int};
            }
            """),
        new("enum", L.T("Snippets.Enum"), """
            enum ${Name}
            {
                ${First},
                ${Second},
            }
            """),
        new("exec", L.T("Snippets.Exec"), """
            exec function ${name}()
            {
                $0
            }
            """),
        new("timer", L.T("Snippets.Timer"), """
            AddTimer('${OnTick}', ${1.0}, ${true});

            timer function ${OnTick}(dt : float, id : int)
            {
                $0
            }
            """),
        new("for", L.T("Snippets.For"), """
            for (${i} = 0; ${i} < ${array}.Size(); ${i} += 1)
            {
                $0
            }
            """),
        new("listen", L.T("Snippets.Listen"), """
            theInput.RegisterListener(this, '${OnMyAction}', '${MyAction}');

            event ${OnMyAction}(action : SInputAction)
            {
                if (IsPressed(action))
                {
                    $0
                }
            }
            """),
        new("setting", L.T("Snippets.Setting"), """
            theGame.GetInGameConfigWrapper().GetVarValue('${Group}', '${Var}')
            """),
        new("log", L.T("Snippets.Log"), """
            LogChannel('${MyMod}', "${message}");
            """),
        new("notify", L.T("Snippets.Notify"), """
            theGame.GetGuiManager().ShowNotification(${GetLocStringByKeyExt("key")});
            """),
        new("fact", L.T("Snippets.Fact"), """
            if (FactsQuerySum("${fact}") > 0)
            {
                $0
            }
            """),
        new("additem", L.T("Snippets.Additem"), """
            thePlayer.GetInventory().AddAnItem('${item}', ${1});
            """),
    ];
    // loc-ignore-end

    /// <summary>The built-in snippets, then the person's own, which replace a built-in one with the same prefix.</summary>
    public static List<Snippet> All()
    {
        var own = Own();
        var prefixes = own.Select(s => s.Prefix).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. BuiltIn.Where(s => !prefixes.Contains(s.Prefix)), .. own];
    }

    private static List<Snippet> Own()
    {
        var result = new List<Snippet>();
        try
        {
            if (!Directory.Exists(Folder)) return result;
            foreach (string path in Directory.EnumerateFiles(Folder, "*.ws").Order(StringComparer.OrdinalIgnoreCase))
            {
                string text = TextDecoding.Decode(System.IO.File.ReadAllBytes(path)).Replace("\r\n", "\n");
                string title = Path.GetFileNameWithoutExtension(path);
                if (text.StartsWith("//"))
                {
                    int end = text.IndexOf('\n');
                    title = (end < 0 ? text[2..] : text[2..end]).Trim();
                    text = end < 0 ? "" : text[(end + 1)..];
                }
                result.Add(new Snippet(Path.GetFileNameWithoutExtension(path), title, text.TrimEnd('\n'), path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable folder just has no snippets
        }
        return result;
    }

    /// <summary>
    /// The snippet's text with each line after the first indented like the line it goes on, using <paramref name="newline"/>.
    /// Each ${name} becomes its name, as a place to type; the same name twice is listed each time.
    /// </summary>
    public static ExpandedSnippet Expand(Snippet snippet, string indent, string newline)
    {
        string body = snippet.Body.Replace("\r\n", "\n").Replace("\n", newline + indent);
        var sb = new StringBuilder();
        var fields = new List<(int, int)>();
        int caret = -1, last = 0;
        foreach (Match m in Field().Matches(body))
        {
            sb.Append(body, last, m.Index - last);
            if (m.Value == "$0") caret = sb.Length;
            else
            {
                string name = m.Groups[1].Value;
                fields.Add((sb.Length, name.Length));
                sb.Append(name);
            }
            last = m.Index + m.Length;
        }
        sb.Append(body, last, body.Length - last);
        return new ExpandedSnippet(sb.ToString(), fields, caret < 0 ? sb.Length : caret);
    }

    [GeneratedRegex(@"\$\{([^}]*)\}|\$0")]
    private static partial Regex Field();
}
