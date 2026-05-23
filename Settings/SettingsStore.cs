using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheGrandNotch.Settings;

public static class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheGrandNotch");

    public static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static NotchSettings Load()
    {
        NotchSettings settings;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<NotchSettings>(json, JsonOptions) ?? new NotchSettings();
            }
            else
            {
                settings = new NotchSettings();
            }
        }
        catch
        {
            settings = new NotchSettings();
        }

        Save(settings);
        return settings;
    }

    public static void Save(NotchSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
