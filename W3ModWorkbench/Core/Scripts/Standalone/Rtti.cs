using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace W3ModWorkbench.Core.Scripts.Standalone;

public sealed class RttiClass
{
    public required string Name { get; init; }
    public string? Base { get; init; }
    public List<(string Name, string Type)> Properties { get; } = [];
    /// <summary>The methods the engine implements. The dump lists only their names, not their parameters.</summary>
    public List<string> Methods { get; } = [];

    private static readonly string[] RootClasses = ["IScriptable", "ISerializable"];

    public bool IsStruct => Base is null && !RootClasses.Contains(Name);
}

public sealed class RttiEnum
{
    public required string Name { get; init; }
    public List<(string Name, int Value)> Options { get; } = [];
}

/// <summary>
/// The engine's native classes, properties and enums, as the game's own <c>-dumprtti</c> writes them to rttidump.xml.
/// Scripts only declare part of what they use; the rest comes from here. A slimmed copy for the current game version
/// is built into the app, so compiling needs neither REDkit nor the game running.
/// </summary>
public sealed class Rtti
{
    public List<RttiClass> Classes { get; } = [];
    public List<RttiEnum> Enums { get; } = [];
    public List<string> GlobalFunctions { get; } = [];
    /// <summary>Engine types that aren't classes: EntityHandle, TagList, CGUID and the like.</summary>
    public List<string> SimpleTypes { get; } = [];

    private const string ResourceName = "W3ModWorkbench.Core.Scripts.Standalone.rtti.txt.gz";

    /// <summary>The copy built into the app, or an empty one when the build doesn't have it.</summary>
    public static Rtti LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return new Rtti();
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return LoadSlim(reader);
    }

    /// <summary>Reads the slim text form: C name base, P name type, E name, O name value, one per line.</summary>
    public static Rtti LoadSlim(TextReader reader)
    {
        var rtti = new Rtti();
        RttiClass? cls = null;
        RttiEnum? en = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 3) continue;
            string[] f = line[2..].Split('\t');
            switch (line[0])
            {
                case 'C':
                    cls = new RttiClass { Name = f[0], Base = f.Length > 1 && f[1].Length > 0 ? f[1] : null };
                    rtti.Classes.Add(cls);
                    break;
                case 'P' when cls is not null:
                    cls.Properties.Add((f[0], f[1]));
                    break;
                case 'F' when cls is not null:
                    cls.Methods.Add(f[0]);
                    break;
                case 'G':
                    rtti.GlobalFunctions.Add(f[0]);
                    break;
                case 'T':
                    rtti.SimpleTypes.Add(f[0]);
                    break;
                case 'E':
                    en = new RttiEnum { Name = f[0] };
                    rtti.Enums.Add(en);
                    break;
                case 'O' when en is not null:
                    en.Options.Add((f[0], int.Parse(f[1])));
                    break;
            }
        }
        return rtti;
    }

    /// <summary>Reads the game's rttidump.xml, line by line (it's one tag per line, and 7 MB).</summary>
    public static Rtti LoadDump(string path)
    {
        var rtti = new Rtti();
        int functionDepth = 0;
        bool skipOptions = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("<function") || line.StartsWith("<globalFunction"))
            {
                if (functionDepth == 0 && Attribute(line, "Name") is { } functionName)
                {
                    if (line.StartsWith("<globalFunction")) rtti.GlobalFunctions.Add(functionName);
                    else if (rtti.Classes.Count > 0) rtti.Classes[^1].Methods.Add(functionName);
                }
                if (!line.EndsWith("/>")) functionDepth++;
                continue;
            }
            if (line.StartsWith("</function") || line.StartsWith("</globalFunction"))
            {
                functionDepth--;
                continue;
            }
            if (functionDepth > 0) continue;

            if (line.StartsWith("<simple ") && Attribute(line, "Name") is { } simple)
                rtti.SimpleTypes.Add(simple);
            else if (line.StartsWith("<class "))
                rtti.Classes.Add(new RttiClass { Name = Attribute(line, "Name") ?? "", Base = Attribute(line, "BaseClass") });
            else if (line.StartsWith("<property ") && rtti.Classes.Count > 0)
                rtti.Classes[^1].Properties.Add((Attribute(line, "Name") ?? "", Attribute(line, "Type") ?? ""));
            else if (line.StartsWith("<enum "))
            {
                skipOptions = Attribute(line, "IsScripted") == "true";
                if (!skipOptions) rtti.Enums.Add(new RttiEnum { Name = Attribute(line, "Name") ?? "" });
            }
            else if (line.StartsWith("<option ") && !skipOptions && rtti.Enums.Count > 0
                && int.TryParse(Attribute(line, "Value"), out int value))
                rtti.Enums[^1].Options.Add((Attribute(line, "Name") ?? "", value));
        }
        return rtti;
    }

    /// <summary>Writes the slim form that <see cref="LoadSlim"/> reads.</summary>
    public void WriteSlim(TextWriter writer)
    {
        foreach (string g in GlobalFunctions) writer.Write($"G\t{g}\n");
        foreach (string t in SimpleTypes) writer.Write($"T\t{t}\n");
        foreach (var c in Classes)
        {
            writer.Write($"C\t{c.Name}\t{c.Base}\n");
            foreach (var (name, type) in c.Properties) writer.Write($"P\t{name}\t{type}\n");
            foreach (string method in c.Methods) writer.Write($"F\t{method}\n");
        }
        foreach (var e in Enums)
        {
            writer.Write($"E\t{e.Name}\n");
            foreach (var (name, value) in e.Options) writer.Write($"O\t{name}\t{value}\n");
        }
    }

    private static string? Attribute(string line, string key)
    {
        string marker = $" {key}=\"";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = line.IndexOf('"', start);
        if (end < 0) return null;
        return line[start..end].Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            .Replace("&apos;", "'").Replace("&amp;", "&");
    }
}
