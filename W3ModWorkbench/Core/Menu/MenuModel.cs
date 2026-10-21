using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Menu;

/// <summary>Something the menu builder edits: tells whoever is watching when any of its values change.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any change, to this or anything inside it, so the XML can be written again.</summary>
    public event Action? Changed;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke();
    }

    protected void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void NotifyChanged() => Changed?.Invoke();

    /// <summary>Passes on changes from a collection and everything in it.</summary>
    protected void Watch<T>(ObservableCollection<T> items) where T : Observable
    {
        items.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (T item in e.OldItems) item.Changed -= NotifyChanged;
            if (e.NewItems is not null) foreach (T item in e.NewItems) item.Changed += NotifyChanged;
            NotifyChanged();
        };
    }
}

/// <summary>How a setting is shown in the game's menu.</summary>
public enum MenuVarKind { Toggle, Options, Slider, Separator, Other }

/// <summary>One choice of an options setting. Picking it sets the setting to <see cref="Id"/>.</summary>
public sealed class MenuOption : Observable
{
    private string _id = "";
    private string _displayName = "";

    public string Id { get => _id; set => Set(ref _id, value.Trim()); }

    /// <summary>The key of the text shown for the choice.</summary>
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value.Trim()); }
}

/// <summary>A setting in a menu page: an on/off switch, a slider, a list of choices, or a gap between settings.</summary>
public sealed class MenuVar : Observable
{
    private string _id = "";
    private string _displayName = "";
    private MenuVarKind _kind;
    private string _rawType = "";
    private string _min = "0";
    private string _max = "10";
    private string _steps = "10";
    private string _default = "";

    public MenuVar()
    {
        Watch(Options);
    }

    public string Id { get => _id; set { Set(ref _id, value.Trim()); Raise(nameof(Summary)); } }

    /// <summary>The key of the text shown for the setting.</summary>
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value.Trim()); }

    public MenuVarKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            Set(ref _kind, value);
            Raise(nameof(IsSlider));
            Raise(nameof(IsOptions));
            Raise(nameof(IsSeparator));
            Raise(nameof(HasDefault));
            Raise(nameof(Summary));
        }
    }

    /// <summary>The displayType of a kind this editor doesn't know, kept as it was.</summary>
    public string RawType { get => _rawType; set => Set(ref _rawType, value); }

    public string Min { get => _min; set => Set(ref _min, value.Trim()); }
    public string Max { get => _max; set => Set(ref _max, value.Trim()); }

    /// <summary>How many steps the slider has between its ends. The game's own sliders use max - min for whole numbers.</summary>
    public string Steps { get => _steps; set => Set(ref _steps, value.Trim()); }

    /// <summary>The value the first preset gives the setting.</summary>
    public string Default { get => _default; set => Set(ref _default, value.Trim()); }

    public ObservableCollection<MenuOption> Options { get; } = [];

    /// <summary>Attributes the editor doesn't know, written back as they were.</summary>
    public List<KeyValuePair<string, string>> ExtraAttributes { get; } = [];

    public bool IsSlider => _kind == MenuVarKind.Slider;
    public bool IsOptions => _kind == MenuVarKind.Options;
    public bool IsSeparator => _kind == MenuVarKind.Separator;
    public bool HasDefault => _kind is not (MenuVarKind.Separator or MenuVarKind.Other);

    public string Summary => _kind switch
    {
        MenuVarKind.Separator => "— separator —",
        _ => _id.Length == 0 ? L.T("MenuModel.NewSetting") : _id,
    };

    /// <summary>The displayType as written to the file.</summary>
    public string DisplayType => _kind switch
    {
        MenuVarKind.Toggle => "TOGGLE",
        MenuVarKind.Options => "OPTIONS",
        MenuVarKind.Slider => $"SLIDER;{_min};{_max};{_steps}",
        MenuVarKind.Separator => "SUBTLE_SEPARATOR",
        _ => _rawType,
    };

    public static MenuVarKind KindOf(string displayType, out string min, out string max, out string steps)
    {
        min = "0";
        max = "10";
        steps = "10";
        string type = displayType.Trim();
        if (type.StartsWith("SLIDER", StringComparison.OrdinalIgnoreCase))
        {
            var parts = type.Split(';');
            if (parts.Length > 1) min = parts[1].Trim();
            if (parts.Length > 2) max = parts[2].Trim();
            steps = parts.Length > 3 ? parts[3].Trim()
                : double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
                  && double.TryParse(min, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
                    ? Math.Max(1, hi - lo).ToString(CultureInfo.InvariantCulture) : "10";
            return MenuVarKind.Slider;
        }
        return type.ToUpperInvariant() switch
        {
            "TOGGLE" => MenuVarKind.Toggle,
            "OPTIONS" => MenuVarKind.Options,
            "SUBTLE_SEPARATOR" => MenuVarKind.Separator,
            _ => MenuVarKind.Other,
        };
    }
}

/// <summary>One line of a preset: the value it gives a setting.</summary>
public sealed class MenuEntry : Observable
{
    private string _varId = "";
    private string _value = "";

    public string VarId { get => _varId; set => Set(ref _varId, value.Trim()); }
    public string Value { get => _value; set => Set(ref _value, value.Trim()); }
}

/// <summary>A set of values for a page's settings that the player can pick in one go. The first is what a fresh install starts with.</summary>
public sealed class MenuPreset : Observable
{
    private string _id = "0";
    private string _displayName = "";

    public MenuPreset()
    {
        Watch(Entries);
    }

    public string Id { get => _id; set => Set(ref _id, value.Trim()); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value.Trim()); }
    public ObservableCollection<MenuEntry> Entries { get; } = [];
}

/// <summary>A page of the mod menu, with its settings and presets.</summary>
public sealed class MenuGroup : Observable
{
    private string _id = "";
    private string _displayName = "";

    public MenuGroup()
    {
        Watch(Vars);
        Watch(Presets);
    }

    /// <summary>What scripts name the page when they read its settings.</summary>
    public string Id { get => _id; set { Set(ref _id, value.Trim()); Raise(nameof(Summary)); } }

    /// <summary>The page's place in the menu: dots separate the levels (Mods.Author.ModName.Page), each one's text is a key.</summary>
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value.Trim()); }

    public ObservableCollection<MenuVar> Vars { get; } = [];
    public ObservableCollection<MenuPreset> Presets { get; } = [];

    /// <summary>Whether the file lists the presets before the settings, as some do. Kept so the file reads the same.</summary>
    public bool PresetsFirst { get; set; }

    public List<KeyValuePair<string, string>> ExtraAttributes { get; } = [];

    public string Summary => _id.Length == 0 ? L.T("MenuModel.NewPage") : _id;
}

/// <summary>A mod menu file (the XML in user_config_matrix) as the builder edits it.</summary>
public sealed class MenuFile : Observable
{
    public MenuFile()
    {
        Watch(Groups);
    }

    public ObservableCollection<MenuGroup> Groups { get; } = [];

    /// <summary>Things in the file the builder can't keep, such as comments, shown before they're lost.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The text of the keys the file uses, as the builder was told them. Not part of the XML.</summary>
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- reading

    public static bool LooksLikeMenu(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root?.Name.LocalName == "UserConfig";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <exception cref="XmlException">The text isn't well-formed XML.</exception>
    /// <exception cref="InvalidDataException">It's XML, but not a mod menu.</exception>
    public static MenuFile Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        if (doc.Root is not { Name.LocalName: "UserConfig" } root)
            throw new InvalidDataException(L.T("MenuModel.IsntModMenuFirst"));

        var file = new MenuFile();
        bool comments = doc.DescendantNodes().OfType<XComment>().Any();
        if (comments) file.Warnings.Add("comments");
        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var g in root.Elements())
        {
            if (g.Name.LocalName != "Group")
            {
                unknown.Add($"<{g.Name.LocalName}>");
                continue;
            }
            var group = new MenuGroup { Id = Attr(g, "id"), DisplayName = Attr(g, "displayName") };
            foreach (var a in g.Attributes().Where(a => a.Name.LocalName is not ("id" or "displayName")))
                group.ExtraAttributes.Add(new(a.Name.LocalName, a.Value));

            bool seenVars = false;
            foreach (var part in g.Elements())
            {
                switch (part.Name.LocalName)
                {
                    case "VisibleVars":
                        seenVars = true;
                        foreach (var v in part.Elements())
                        {
                            if (v.Name.LocalName != "Var") { unknown.Add(L.T("MenuModel.VisibleVars", v.Name.LocalName)); continue; }
                            group.Vars.Add(ReadVar(v, unknown));
                        }
                        break;
                    case "PresetsArray":
                        group.PresetsFirst = !seenVars;
                        foreach (var p in part.Elements())
                        {
                            if (p.Name.LocalName != "Preset") { unknown.Add(L.T("MenuModel.PresetsArray", p.Name.LocalName)); continue; }
                            var preset = new MenuPreset { Id = Attr(p, "id"), DisplayName = Attr(p, "displayName") };
                            foreach (var e in p.Elements("Entry"))
                                preset.Entries.Add(new MenuEntry { VarId = Attr(e, "varId"), Value = Attr(e, "value") });
                            group.Presets.Add(preset);
                        }
                        break;
                    default:
                        unknown.Add(L.T("MenuModel.Group", part.Name.LocalName));
                        break;
                }
            }

            // the first preset's values are the settings' defaults
            if (group.Presets.Count > 0)
                foreach (var entry in group.Presets[0].Entries)
                    if (group.Vars.FirstOrDefault(v => v.Id == entry.VarId && v.HasDefault) is { } var)
                        var.Default = entry.Value;
            file.Groups.Add(group);
        }
        if (unknown.Count > 0) file.Warnings.Add(L.T("MenuModel.ElementsDoesntKnow", string.Join(", ", unknown.Take(6))));
        return file;
    }

    private static MenuVar ReadVar(XElement v, SortedSet<string> unknown)
    {
        var var = new MenuVar { Id = Attr(v, "id"), DisplayName = Attr(v, "displayName") };
        string type = Attr(v, "displayType");
        var.Kind = MenuVar.KindOf(type, out string min, out string max, out string steps);
        var.RawType = type;
        if (var.Kind == MenuVarKind.Slider) (var.Min, var.Max, var.Steps) = (min, max, steps);
        foreach (var a in v.Attributes().Where(a => a.Name.LocalName is not ("id" or "displayName" or "displayType")))
            var.ExtraAttributes.Add(new(a.Name.LocalName, a.Value));
        foreach (var o in v.Elements("OptionsArray").Elements("Option"))
            var.Options.Add(new MenuOption { Id = Attr(o, "id"), DisplayName = Attr(o, "displayName") });
        foreach (var other in v.Elements().Where(e => e.Name.LocalName != "OptionsArray"))
            unknown.Add(L.T("MenuModel.Var", other.Name.LocalName));
        return var;
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    // ---------------------------------------------------------------- writing

    /// <summary>The XML the file reads as, indented with tabs the way the game's own are.</summary>
    public string ToXml(string declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>") // loc-ignore
    {
        var root = new XElement("UserConfig");
        foreach (var g in Groups)
        {
            var group = new XElement("Group", new XAttribute("id", g.Id), new XAttribute("displayName", g.DisplayName));
            foreach (var (k, v) in g.ExtraAttributes) group.Add(new XAttribute(k, v));

            var vars = new XElement("VisibleVars");
            foreach (var v in g.Vars)
            {
                var element = new XElement("Var", new XAttribute("id", v.Id), new XAttribute("displayName", v.DisplayName),
                    new XAttribute("displayType", v.DisplayType));
                foreach (var (k, value) in v.ExtraAttributes) element.Add(new XAttribute(k, value));
                if (v.Kind == MenuVarKind.Options)
                {
                    var options = new XElement("OptionsArray");
                    foreach (var o in v.Options)
                        options.Add(new XElement("Option", new XAttribute("id", o.Id), new XAttribute("displayName", o.DisplayName),
                            new XElement("Entry", new XAttribute("varId", v.Id), new XAttribute("value", o.Id))));
                    element.Add(options);
                }
                vars.Add(element);
            }

            var presets = new XElement("PresetsArray");
            for (int i = 0; i < g.Presets.Count; i++)
            {
                var p = g.Presets[i];
                var preset = new XElement("Preset", new XAttribute("id", p.Id), new XAttribute("displayName", p.DisplayName));
                var written = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in p.Entries)
                {
                    // the first preset follows the defaults typed into the settings
                    string value = i == 0 && g.Vars.FirstOrDefault(v => v.Id == e.VarId && v.HasDefault) is { Default.Length: > 0 } d ? d.Default : e.Value;
                    preset.Add(new XElement("Entry", new XAttribute("varId", e.VarId), new XAttribute("value", value)));
                    written.Add(e.VarId);
                }
                if (i == 0)
                    foreach (var v in g.Vars.Where(v => v.HasDefault && v.Id.Length > 0 && v.Default.Length > 0 && written.Add(v.Id)))
                        preset.Add(new XElement("Entry", new XAttribute("varId", v.Id), new XAttribute("value", v.Default)));
                presets.Add(preset);
            }

            bool anyPreset = g.Presets.Count > 0;
            if (g.PresetsFirst && anyPreset) group.Add(presets);
            group.Add(vars);
            if (!g.PresetsFirst && anyPreset) group.Add(presets);
            root.Add(group);
        }
        return Indent(new XDocument(root), declaration);
    }

    /// <summary>Indents with tabs and a line break per element, under the given XML declaration (or none, when it is empty).</summary>
    public static string Indent(XDocument doc, string declaration)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
        };
        using (var writer = XmlWriter.Create(sb, settings))
            doc.Save(writer);
        string body = sb.ToString();
        return declaration.Length == 0 ? body : declaration + "\r\n" + body + "\r\n";
    }

    // ---------------------------------------------------------------- checking

    public sealed record Problem(string Message, MenuGroup? Group = null, MenuVar? Var = null);

    public List<Problem> Check()
    {
        var problems = new List<Problem>();
        if (Groups.Count == 0) problems.Add(new(L.T("MenuModel.MenuHasNoPages")));
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Groups)
        {
            if (g.Id.Length == 0) problems.Add(new(L.T("MenuModel.PageHasNoId"), g));
            else if (!groupIds.Add(g.Id)) problems.Add(new(L.T("MenuModel.TwoPagesCalled", g.Id), g));
            if (g.DisplayName.Length == 0) problems.Add(new(L.T("MenuModel.PageHasNoDisplay", g.Id), g));
            if (g.Vars.Count == 0) problems.Add(new(L.T("MenuModel.PageHasNoSettings", g.Id), g));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in g.Vars)
            {
                if (v.IsSeparator) continue;
                string at = $"'{g.Id}' / '{v.Id}'";
                if (v.Id.Length == 0) { problems.Add(new(L.T("MenuModel.SettingHasNoId", g.Id), g, v)); continue; }
                if (!ids.Add(v.Id)) problems.Add(new(L.T("MenuModel.AnotherSettingPageHas", at), g, v));
                if (v.Id.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                    problems.Add(new(L.T("MenuModel.IdsSpacesSymbolsHard", at), g, v));
                if (v.DisplayName.Length == 0) problems.Add(new(L.T("MenuModel.NoDisplayNameSo", at), g, v));
                switch (v.Kind)
                {
                    case MenuVarKind.Toggle:
                        if (v.Default.Length > 0 && v.Default is not ("true" or "false"))
                            problems.Add(new(L.T("MenuModel.TogglesDefaultHasTrue", at), g, v));
                        break;
                    case MenuVarKind.Slider:
                        if (!double.TryParse(v.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
                            || !double.TryParse(v.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out double hi))
                        {
                            problems.Add(new(L.T("MenuModel.SlidersEndsHaveNumbers", at), g, v));
                            break;
                        }
                        if (lo >= hi) problems.Add(new(L.T("MenuModel.SlidersMinimumHasBelow", at), g, v));
                        if (!double.TryParse(v.Steps, NumberStyles.Float, CultureInfo.InvariantCulture, out double steps) || steps <= 0)
                            problems.Add(new(L.T("MenuModel.SlidersStepsHaveNumber", at), g, v));
                        if (v.Default.Length > 0)
                        {
                            if (!double.TryParse(v.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                                problems.Add(new(L.T("MenuModel.DefaultIsntNumber", at), g, v));
                            else if (d < lo || d > hi)
                                problems.Add(new(L.T("MenuModel.DefaultOutside", at, v.Default, v.Min, v.Max), g, v));
                        }
                        break;
                    case MenuVarKind.Options:
                        if (v.Options.Count == 0) problems.Add(new(L.T("MenuModel.OptionsSettingNeedsLeast", at), g, v));
                        var optionIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var o in v.Options)
                        {
                            if (o.Id.Length == 0) problems.Add(new(L.T("MenuModel.OptionHasNoId", at), g, v));
                            else if (!optionIds.Add(o.Id)) problems.Add(new(L.T("MenuModel.TwoOptionsHaveId", at, o.Id), g, v));
                            if (o.DisplayName.Length == 0) problems.Add(new(L.T("MenuModel.OptionHasNoDisplay", at, o.Id), g, v));
                        }
                        if (v.Default.Length > 0 && v.Options.Count > 0 && !optionIds.Contains(v.Default))
                            problems.Add(new(L.T("MenuModel.DefaultIsntOneOptions", at, v.Default), g, v));
                        break;
                }
            }

            var known = new HashSet<string>(g.Vars.Where(v => !v.IsSeparator).Select(v => v.Id), StringComparer.Ordinal);
            var presetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in g.Presets)
            {
                if (!presetIds.Add(p.Id)) problems.Add(new(L.T("MenuModel.TwoPresetsHaveId", g.Id, p.Id), g));
                if (p.DisplayName.Length == 0) problems.Add(new(L.T("MenuModel.PresetHasNoDisplay", g.Id, p.Id), g));
                foreach (var e in p.Entries.Where(e => !known.Contains(e.VarId)))
                    problems.Add(new(L.T("MenuModel.PresetSetsWhichIsnt", g.Id, p.Id, e.VarId), g));
            }
        }
        return problems;
    }

    // ---------------------------------------------------------------- strings

    /// <summary>A key the menu shows text for, and where the text is used.</summary>
    public sealed record StringKey(string Key, string Use);

    /// <summary>
    /// The keys the menu needs text for, named the way the game and the modding tools expect: panel_ for each level
    /// of a page's place in the menu, option_ for a setting, preset_ for a page's presets and preset_value_ for options
    /// and preset names.
    /// </summary>
    public List<StringKey> RequiredKeys()
    {
        var keys = new List<StringKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string key, string use)
        {
            if (key.Length > 0 && seen.Add(key)) keys.Add(new(key, use));
        }
        foreach (var g in Groups)
        {
            if (g.DisplayName.Length > 0)
            {
                foreach (string part in g.DisplayName.Split('.', StringSplitOptions.RemoveEmptyEntries))
                    Add("panel_" + part, L.T("MenuModel.MenuPage"));
                Add("preset_" + g.DisplayName.Replace('.', '_'), L.T("MenuModel.Presets", g.Id));
            }
            foreach (var v in g.Vars.Where(v => !v.IsSeparator))
            {
                Add("option_" + v.DisplayName, "Setting " + v.Id);
                foreach (var o in v.Options) Add("preset_value_" + o.DisplayName, L.T("MenuModel.Option", o.Id, v.Id));
            }
            foreach (var p in g.Presets) Add("preset_value_" + p.DisplayName, "Preset " + p.Id);
        }
        return keys;
    }

    // ---------------------------------------------------------------- scripts

    /// <summary>WitcherScript that reads every setting of every page into variables, the way mods do it.</summary>
    public string ToScript()
    {
        var sb = new StringBuilder();
        foreach (var g in Groups)
        {
            var vars = g.Vars.Where(v => !v.IsSeparator && v.Id.Length > 0).ToList();
            string name = "Load" + Identifier(g.Id.Length > 0 ? g.Id : "Menu");
            sb.Append("// reads the settings of the page '").Append(g.Id).Append("' from the mod menu\n"); // loc-ignore
            sb.Append("function ").Append(name).Append("()\n{\n");
            sb.Append("\tvar config : CInGameConfigWrapper;\n"); // loc-ignore
            foreach (var v in vars) sb.Append("\tvar ").Append(Identifier(v.Id)).Append(" : ").Append(ScriptType(v)).Append(";\n");
            sb.Append("\n\tconfig = theGame.GetInGameConfigWrapper();\n");
            foreach (var v in vars)
                sb.Append('\t').Append(Identifier(v.Id)).Append(" = ").Append(ScriptConvert(v))
                  .Append("(config.GetVarValue('").Append(g.Id).Append("', '").Append(v.Id).Append("'));\n");
            sb.Append("}\n\n");
        }
        if (sb.Length == 0) sb.Append("// add a page and some settings to get the script that reads them\n"); // loc-ignore
        else
            sb.Append("// to change a setting from a script:\n//   theGame.GetInGameConfigWrapper().SetVarValue('Page', 'Setting', \"value\");\n//   theGame.SaveUserSettings();\n"); // loc-ignore
        return sb.ToString();
    }

    private static string Identifier(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static bool WholeNumbers(MenuVar v) =>
        double.TryParse(v.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
        && double.TryParse(v.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
        && double.TryParse(v.Steps, NumberStyles.Float, CultureInfo.InvariantCulture, out double steps)
        && lo == Math.Floor(lo) && hi == Math.Floor(hi) && Math.Abs(steps - (hi - lo)) < 1e-9;

    private static string ScriptType(MenuVar v) => v.Kind switch
    {
        MenuVarKind.Toggle => "bool",
        MenuVarKind.Slider => WholeNumbers(v) ? "int" : "float",
        MenuVarKind.Options => v.Options.All(o => int.TryParse(o.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ? "int" : "string",
        _ => "string",
    };

    private static string ScriptConvert(MenuVar v) => ScriptType(v) switch
    {
        "bool" => "StringToBool",
        "int" => "StringToInt",
        "float" => "StringToFloat",
        _ => "",
    };

    // ---------------------------------------------------------------- a starting point

    /// <summary>A menu with one page and a few settings, to start from.</summary>
    public static MenuFile Starter(string mod = "MyMod")
    {
        var file = new MenuFile();
        var group = new MenuGroup { Id = mod + "_General", DisplayName = $"Mods.{mod}.{mod}_General" };
        group.Vars.Add(new MenuVar { Id = mod + "_Enabled", DisplayName = mod + "_Enabled", Kind = MenuVarKind.Toggle, Default = "true" });
        group.Vars.Add(new MenuVar { Kind = MenuVarKind.Separator, Id = "Separator", DisplayName = "" });
        group.Vars.Add(new MenuVar { Id = mod + "_Range", DisplayName = mod + "_Range", Kind = MenuVarKind.Slider, Min = "1", Max = "30", Steps = "29", Default = "10" });
        var mode = new MenuVar { Id = mod + "_Mode", DisplayName = mod + "_Mode", Kind = MenuVarKind.Options, Default = "0" };
        mode.Options.Add(new MenuOption { Id = "0", DisplayName = mod + "_Mode_00" });
        mode.Options.Add(new MenuOption { Id = "1", DisplayName = mod + "_Mode_01" });
        group.Vars.Add(mode);
        group.Presets.Add(new MenuPreset { Id = "0", DisplayName = mod + "_Preset_Default" });
        file.Groups.Add(group);
        return file;
    }
}
