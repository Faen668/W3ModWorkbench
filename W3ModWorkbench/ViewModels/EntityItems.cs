using System.Globalization;
using System.IO;
using System.Windows;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;

namespace W3ModWorkbench.ViewModels;

/// <summary>
/// A row in the entity viewer's property lists: a class's heading, or a property with its name, type and value in
/// words, under the struct, array or object it's in, which folds it away. A value that refers to a file says the file,
/// which <see cref="Link"/> opens. A property the file doesn't keep has its class's default, which isn't known here.
/// </summary>
public sealed class PropertyRow(string name, string type, string value, int depth, string? link, bool heading = false, bool @default = false)
    : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string Value { get; } = value;
    public int Depth { get; } = depth;
    public string? Link { get; } = link;
    public bool Heading { get; } = heading;
    public bool Default { get; } = @default;

    /// <summary>The row it's under, or null at the top.</summary>
    public PropertyRow? Parent { get; private set; }
    public bool HasChildren { get; private set; }

    /// <summary>A bool's value, for its tick or cross.</summary>
    public bool? Bool { get; init; }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new(nameof(Glyph)));
        }
    }
    // headings start open, as REDkit has them; structs and arrays start folded
    private bool _expanded = heading;

    /// <summary>Whether every row it's under is open.</summary>
    public bool IsShown
    {
        get
        {
            for (var p = Parent; p is not null; p = p.Parent)
                if (!p.IsExpanded) return false;
            return true;
        }
    }

    public string Glyph => !HasChildren ? "" : IsExpanded ? char.ConvertFromUtf32(0xE70D) : char.ConvertFromUtf32(0xE76C); // chevron down, right
    public string BoolGlyph => Bool is null ? "" : char.ConvertFromUtf32(Bool.Value ? 0xE73E : 0xE711); // check, cancel
    public System.Windows.Media.Brush BoolBrush => Bool == true ? TrueBrush : FalseBrush;
    public Visibility BoolVisibility => Bool is null ? Visibility.Collapsed : Visibility.Visible;
    public Thickness Indent => new(Depth * 16 + (Heading ? 0 : 4), 0, 0, 0);
    public Visibility LinkVisibility => Link is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ValueVisibility => Link is null ? Visibility.Visible : Visibility.Collapsed;
    public FontWeight Weight => Heading ? FontWeights.SemiBold : FontWeights.Normal;
    public double Opacity => Default ? 0.45 : 1;

    private static readonly System.Windows.Media.Brush TrueBrush = Frozen(System.Windows.Media.Color.FromRgb(0x2E, 0xB6, 0x7D));
    private static readonly System.Windows.Media.Brush FalseBrush = Frozen(System.Windows.Media.Color.FromRgb(0xE5, 0x67, 0x5F));

    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether it matched the last search, or has a row under it that did.</summary>
    public bool Found { get; private set; }

    /// <summary>
    /// A list of rows as a view that hides those under a folded row, or, while <paramref name="search"/> says
    /// something, all but the rows that match it and those they're under. Refresh it after folding a row or searching.
    /// </summary>
    public static System.ComponentModel.ICollectionView View(List<PropertyRow> rows, Func<string> search)
    {
        var view = new System.Windows.Data.ListCollectionView(rows);
        view.Filter = r => search().Length > 0 ? ((PropertyRow)r).Found : ((PropertyRow)r).IsShown;
        return view;
    }

    /// <summary>Marks the rows whose name or value has the text, and the rows they're under.</summary>
    public static void Search(List<PropertyRow> rows, string text)
    {
        foreach (var row in rows) row.Found = false;
        if (text.Length == 0) return;
        foreach (var row in rows)
            if (!row.Heading && (row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || row.Value.Contains(text, StringComparison.OrdinalIgnoreCase)))
                for (var r = row; r is not null && !r.Found; r = r.Parent) r.Found = true;
    }

    // arrays longer than this show their first items and how many more there are
    private const int MostItems = 50;

    /// <summary>The engine's classes and enums, as REDkit lists them, read once.</summary>
    private static readonly Lazy<(Dictionary<string, Core.Scripts.Standalone.RttiClass> Classes, HashSet<string> Enums)> Rtti = new(() =>
    {
        var rtti = Core.Scripts.Standalone.Rtti.LoadBuiltIn();
        return (rtti.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First()), rtti.Enums.Select(e => e.Name).ToHashSet());
    });

    /// <summary>
    /// An object's properties as rows, under the class that declares each, from the most basic class to its own (as
    /// REDkit shows them), with structs, arrays and the objects they point to beneath them. Those the file doesn't
    /// keep are listed too, dimmed. An object of a class REDkit doesn't list has its properties as they are.
    /// </summary>
    public static List<PropertyRow> Of(RedObject o, Cr2wFile file, IEnumerable<string>? leaveOut = null)
    {
        var rows = new List<PropertyRow>();
        AddObject(rows, o, file, null, 0, leaveOut?.ToHashSet() ?? [], [o]);
        return rows;
    }

    private static void AddObject(List<PropertyRow> rows, RedObject o, Cr2wFile file, PropertyRow? parent, int depth, HashSet<string> skip, HashSet<RedObject> path)
    {
        var stored = o.Properties.Where(p => !skip.Contains(p.Name)).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        var chain = Chain(o.ClassName);
        if (chain.Count == 0)
        {
            foreach (var (name, type, value) in stored.Values) Add(rows, name, type, value, depth, file, parent, path);
            return;
        }
        var shown = new HashSet<string>();
        foreach (var cls in chain)
        {
            var own = cls.Properties.Where(p => shown.Add(p.Name) && !skip.Contains(p.Name)).ToList();
            if (own.Count == 0) continue;
            var heading = Child(rows, new PropertyRow(cls.Name, "", "", depth, null, heading: true), parent);
            foreach (var (name, type) in own)
            {
                if (stored.TryGetValue(name, out var p)) Add(rows, p.Name, p.Type, p.Value, depth + 1, file, heading, path);
                else Child(rows, new PropertyRow(name, type, "default", depth + 1, null, @default: true), heading);
            }
        }
        var other = stored.Values.Where(p => !shown.Contains(p.Name)).ToList();
        if (other.Count > 0)
        {
            var heading = Child(rows, new PropertyRow("Other", "", "", depth, null, heading: true), parent);
            foreach (var (name, type, value) in other) Add(rows, name, type, value, depth + 1, file, heading, path);
        }
    }

    private static PropertyRow Child(List<PropertyRow> rows, PropertyRow row, PropertyRow? parent)
    {
        row.Parent = parent;
        if (parent is not null) parent.HasChildren = true;
        rows.Add(row);
        return row;
    }

    /// <summary>A class and its bases, the most basic first, each with the properties it adds.</summary>
    private static List<Core.Scripts.Standalone.RttiClass> Chain(string className)
    {
        var chain = new List<Core.Scripts.Standalone.RttiClass>();
        var classes = Rtti.Value.Classes;
        for (string? name = className; name is not null && classes.TryGetValue(name, out var cls) && chain.Count < 32; name = cls.Base)
            chain.Insert(0, cls);
        return chain;
    }

    private static void Add(List<PropertyRow> rows, string name, string type, object? value, int depth, Cr2wFile file, PropertyRow? parent, HashSet<RedObject> path)
    {
        if (depth > 12) return;
        switch (value)
        {
            case RedObject s when Summary(s) is { Length: > 0 } summary:
                Child(rows, new PropertyRow(name, type, summary, depth, null), parent);
                break;
            case RedObject s:
                var row = Child(rows, new PropertyRow(name, type, $"[{s.ClassName}]", depth, null), parent);
                foreach (var (field, t, v) in s.Properties) Add(rows, field, t, v, depth + 1, file, row, path);
                break;
            case List<object?> list:
                var array = Child(rows, new PropertyRow(name, type, list.Count == 0 ? "empty" : list.Count == 1 ? "1 item" : $"{list.Count:N0} items", depth, null), parent);
                string element = type.Contains(',') ? type[(type.LastIndexOf(',') + 1)..] : "";
                for (int i = 0; i < list.Count && i < MostItems; i++) Add(rows, $"[{i}]", element, list[i], depth + 1, file, array, path);
                if (list.Count > MostItems) Child(rows, new PropertyRow("…", "", $"and {list.Count - MostItems:N0} more", depth + 1, null), array);
                break;
            case RedHandle h when file.Import(h) is { } import:
                Child(rows, new PropertyRow(name, type, import.Path, depth, import.Path), parent);
                break;
            case RedHandle h when file.Object(h) is { } target:
                // the object it points to, opened beneath it as REDkit does, unless it's one it's already inside
                string label = $"[{target.ClassName}]" + (target["name"] is string n && n.Length > 0 ? $" {n}" : "");
                var pointer = Child(rows, new PropertyRow(name, type, label, depth, null), parent);
                if (path.Add(target))
                {
                    AddObject(rows, target, file, pointer, depth + 1, [], path);
                    path.Remove(target);
                }
                break;
            case byte[] raw:
                Child(rows, new PropertyRow(name, type, Raw(raw, type, file), depth, null), parent);
                break;
            case bool b:
                Child(rows, new PropertyRow(name, type, b ? "true" : "false", depth, null) { Bool = b }, parent);
                break;
            default:
                Child(rows, new PropertyRow(name, type, Text(value), depth, null), parent);
                break;
        }
    }

    /// <summary>A value the reader keeps as its bytes, in words where its type is known: a transform, tags, an enum.</summary>
    private static string Raw(byte[] raw, string type, Cr2wFile file)
    {
        switch (type)
        {
            case "EngineTransform":
                return Transform(raw);
            case "TagList":
                var tags = file.Tags(raw);
                return tags.Count == 0 ? "no tags" : string.Join(", ", tags);
        }
        // an enum named like a struct (S…): its value's name
        if (raw.Length == 2 && Rtti.Value.Enums.Contains(type) && (raw[0] | raw[1] << 8) is var n && n < file.Names.Count) return file.Names[n];
        return Text(raw);
    }

    /// <summary>An EngineTransform in words: its position, rotation and scale, or "identity".</summary>
    public static string Transform(byte[] raw)
    {
        if (raw.Length < 1 || raw[0] == 0) return "identity";
        int flags = raw[0], at = 1;
        string Three()
        {
            if (at + 12 > raw.Length) return "?";
            var v = new[] { 0, 4, 8 }.Select(o => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at + o)).ToString("0.###", CultureInfo.InvariantCulture)).ToArray();
            at += 12;
            return string.Join(", ", v);
        }
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add($"position {Three()}");
        if ((flags & 2) != 0) parts.Add($"rotation {Three()}");
        if ((flags & 4) != 0) parts.Add($"scale {Three()}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>A small struct on one line: a vector, angles, a colour, a box.</summary>
    private static string Summary(RedObject s) => s.ClassName switch
    {
        "Vector" or "Vector2" or "Vector3" or "EulerAngles" or "Color" => string.Join(", ", s.Properties.Select(p => $"{p.Name} {Text(p.Value)}")),
        "Box" => $"min {Summary(s.Struct("Min") ?? new RedObject("Vector"))}  ·  max {Summary(s.Struct("Max") ?? new RedObject("Vector"))}",
        _ => "",
    };

    public static string Text(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        byte[] bytes => bytes.Length <= 32 ? Convert.ToHexString(bytes) : $"{bytes.Length:N0} bytes",
        RedHandle { Raw: 0 } => "none",
        RedBufferRef r => $"buffer {r.Index}",
        _ => value.ToString() ?? "",
    };
}

/// <summary>A component in the entity viewer's list.</summary>
public sealed class EntityComponentItem(EntityComponent component)
{
    public EntityComponent Component { get; } = component;
    public string Name => Component.Name;
    public string Kind => Component.ClassName;
    public string Detail => Component.Mesh is { } mesh ? Path.GetFileName(mesh) : Component.Resources.Count > 0 ? Path.GetFileName(Component.Resources[0]) : "";
}
