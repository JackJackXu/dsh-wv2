using System.IO;
using System.Text.Json;

namespace DeepSeekHarness.Desktop.Services;

/// <summary>Minimal per-user settings, persisted under %LOCALAPPDATA%\DSH WV2.</summary>
public sealed class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2");
    private static readonly string SettingsFilePath = Path.Combine(Dir, "settings.json");

    public int LastPort { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 840;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool NotificationsEnabled { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFilePath)) ?? new Settings();
        }
        catch { /* corrupt/first-run -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
