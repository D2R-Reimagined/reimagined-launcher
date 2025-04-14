using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

public static class SettingsManager
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReimaginedLauncher");

    private static readonly string SettingsFilePath = Path.Combine(AppDir, "settings.json");

    public static async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(SettingsFilePath))
            return new AppSettings();

        var json = await File.ReadAllTextAsync(SettingsFilePath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        if (!Directory.Exists(AppDir))
            Directory.CreateDirectory(AppDir);

        var json = JsonSerializer.Serialize(settings, SerializerOptions.Indented);
        await File.WriteAllTextAsync(SettingsFilePath, json);
    }
}