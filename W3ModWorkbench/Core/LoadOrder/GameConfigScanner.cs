using System.IO;
using System.Xml;
using System.Xml.Linq;
using W3ModWorkbench.Core.Input;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>One group of the game's options menus, from an XML file in bin\config\r4game\user_config_matrix\pc.</summary>
/// <param name="Vars">The settings in it, by id.</param>
public sealed record MenuGroup(string Id, string File, int Line, IReadOnlyList<string> Vars);

/// <summary>
/// What mods put in the game's own folders when they're installed, outside their mod folder: menus in
/// bin\config\r4game\user_config_matrix\pc (one XML a mod, read by the game whatever its name), the key bindings menu's
/// input.xml there, and the keys in Documents\The Witcher 3\input.settings.
/// </summary>
public sealed class GameConfig
{
    /// <summary>The menu XML files the game ships; any other in the folder came with a mod.</summary>
    public static readonly HashSet<string> GameMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio.xml", "display.xml", "gameplay.xml", "gamma.xml", "graphics.xml", "hdr.xml", "hidden.xml", "hud.xml", "input.xml", "localization.xml",
    };

    public string? MenuFolder { get; init; }
    public List<MenuGroup> Groups { get; } = [];

    /// <summary>Menu files that aren't valid XML, which the game can't read (and with them, often, the whole options menu).</summary>
    public List<(string File, string Error)> BrokenMenus { get; } = [];

    /// <summary>The actions input.xml offers in the key bindings menu.</summary>
    public HashSet<string> MenuActions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public InputSettings? Input { get; init; }

    /// <summary>The names of the menu files that came with mods checked before they're installed.</summary>
    public HashSet<string> CandidateMenus { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A menu file of a mod checked before it's installed takes the place of the installed file of the same name.</summary>
    public static GameConfig Read(string? gameFolder, IEnumerable<string> extraMenus, string? inputSettings)
    {
        string? folder = gameFolder is null ? null : Path.Combine(gameFolder, "bin", "config", "r4game", "user_config_matrix", "pc");
        InputSettings? input = null;
        if (inputSettings is not null && File.Exists(inputSettings))
        {
            try { input = InputSettings.Read(inputSettings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // without input.settings there are no keys to check
            }
        }
        var config = new GameConfig { MenuFolder = folder is not null && Directory.Exists(folder) ? folder : null, Input = input };

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.MenuFolder is not null)
            foreach (string f in Directory.EnumerateFiles(config.MenuFolder, "*.xml")) files[Path.GetFileName(f)] = f;
        foreach (string f in extraMenus)
        {
            files[Path.GetFileName(f)] = f;
            config.CandidateMenus.Add(Path.GetFileName(f));
        }

        foreach (var (name, path) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            XDocument doc;
            try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
            catch (XmlException ex)
            {
                config.BrokenMenus.Add((name, ex.Message));
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                config.BrokenMenus.Add((name, ex.Message));
                continue;
            }
            foreach (var group in doc.Descendants().Where(e => e.Name.LocalName == "Group"))
            {
                string? id = group.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id)) continue;
                // a group's own settings, not those of a group inside it
                var vars = group.Descendants().Where(e => e.Name.LocalName == "Var" && e.Ancestors().First(a => a.Name.LocalName == "Group") == group)
                    .Select(e => e.Attribute("id")?.Value).OfType<string>().Where(v => v.Length > 0).ToList();
                config.Groups.Add(new MenuGroup(id, name, ((IXmlLineInfo)group).LineNumber, vars));
                if (name.Equals("input.xml", StringComparison.OrdinalIgnoreCase))
                    foreach (var v in group.Descendants().Where(e => e.Name.LocalName == "Var"))
                        foreach (string action in (v.Attribute("actions")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            config.MenuActions.Add(action);
            }
        }
        return config;
    }
}
