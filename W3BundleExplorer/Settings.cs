using System.IO;
using System.Text.Json;

namespace W3BundleExplorer;

public sealed class Settings
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3BundleExplorer", "settings.json");

    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";
    public string OutputFolder { get; set; } = "";
    public string LastOpenFolder { get; set; } = "";
    public bool KeepStructure { get; set; } = true;
    public bool OpenWhenDone { get; set; } = true;

    /// <summary>The Witcher 3 install whose script sources name enum values in decompiled scripts.</summary>
    public string GameFolder { get; set; } = "";

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // a broken settings file just means defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // not worth interrupting the user over
        }
    }
}
