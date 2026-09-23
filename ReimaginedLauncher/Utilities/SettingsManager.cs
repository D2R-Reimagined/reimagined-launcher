using System;
using System.Collections.Generic;
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
    public static string AppDirectoryPath => AppDir;

    // Set when settings.json was unreadable this session and defaults were used instead.
    public static bool RecoveredFromCorruptSettings { get; private set; }
    public static string? QuarantinedSettingsFilePath { get; private set; }

    public static async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(SettingsFilePath))
            return new AppSettings();

        var json = await File.ReadAllTextAsync(SettingsFilePath);
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException exception)
        {
            QuarantineCorruptSettingsFile(exception);
            return new AppSettings();
        }

        // Migration for old settings format
        if (settings.Profiles.Count == 0)
        {
            // Trigger default profile creation
            _ = settings.CurrentProfile;
            
            // Populate the first profile (BattleNet) with old settings if they exist
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var profile = settings.Profiles[0];

            if (root.TryGetProperty("InstallDirectory", out var prop)) profile.InstallDirectory = prop.GetString();
            if (root.TryGetProperty("IsInstallDirectoryValidated", out prop)) profile.IsInstallDirectoryValidated = prop.GetBoolean();
            if (root.TryGetProperty("BackupSaveDirectory", out prop)) profile.BackupSaveDirectory = prop.GetString();
            if (root.TryGetProperty("AutomaticBackupsEnabled", out prop)) profile.AutomaticBackupsEnabled = prop.GetBoolean();
            if (root.TryGetProperty("BackupIntervalMinutes", out prop)) profile.BackupIntervalMinutes = prop.GetInt32();
            if (root.TryGetProperty("BackupAmount", out prop)) profile.BackupAmount = prop.GetInt32();
            if (root.TryGetProperty("NoSound", out prop)) profile.NoSound = prop.GetBoolean();
            if (root.TryGetProperty("NoRumble", out prop)) profile.NoRumble = prop.GetBoolean();
            if (root.TryGetProperty("ForceDesktop", out prop)) profile.ForceDesktop = prop.GetBoolean();
            if (root.TryGetProperty("WindowedMode", out prop)) profile.WindowedMode = prop.GetBoolean();
            if (root.TryGetProperty("ResetOfflineMaps", out prop)) profile.ResetOfflineMaps = prop.GetBoolean();
            if (root.TryGetProperty("EnableRespec", out prop)) profile.EnableRespec = prop.GetBoolean();
            if (root.TryGetProperty("PlayersCount", out prop)) profile.PlayersCount = prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : null;
            if (root.TryGetProperty("SkillPointsPerLevel", out prop)) profile.SkillPointsPerLevel = prop.GetInt32();
            if (root.TryGetProperty("AttributesPerLevel", out prop)) profile.AttributesPerLevel = prop.GetInt32();
            if (root.TryGetProperty("MaxSkillLevel", out prop)) profile.MaxSkillLevel = prop.GetInt32();
            if (root.TryGetProperty("NormalResistPenalty", out prop)) profile.NormalResistPenalty = prop.GetInt32();
            if (root.TryGetProperty("NightmareResistPenalty", out prop)) profile.NightmareResistPenalty = prop.GetInt32();
            if (root.TryGetProperty("HellResistPenalty", out prop)) profile.HellResistPenalty = prop.GetInt32();
            if (root.TryGetProperty("RemovePaladinAuraSound", out prop)) profile.RemovePaladinAuraSound = prop.GetBoolean();
            if (root.TryGetProperty("RemoveSplashVfx", out prop)) profile.RemoveSplashVfx = prop.GetBoolean();
            if (root.TryGetProperty("MakeTooltipBackgroundOpaque", out prop)) profile.MakeTooltipBackgroundOpaque = prop.GetBoolean();
            if (root.TryGetProperty("TerrorizeAllZones", out prop)) profile.TerrorizeAllZones = prop.GetBoolean();
            if (root.TryGetProperty("TerrorZonePurpleOverlay", out prop)) profile.TerrorZonePurpleOverlay = prop.GetBoolean();
            if (root.TryGetProperty("RemoveFadeEffect", out prop)) profile.RemoveFadeEffect = prop.GetBoolean();
            if (root.TryGetProperty("RestoreTerrorZoneFanfare", out prop)) profile.RestoreTerrorZoneFanfare = prop.GetBoolean();
            if (root.TryGetProperty("OrbStackDrops", out prop)) profile.OrbStackDrops = (StackDropOption)prop.GetInt32();
            if (root.TryGetProperty("RuneStackDrops", out prop)) profile.RuneStackDrops = (StackDropOption)prop.GetInt32();

            if (root.TryGetProperty("Plugins", out prop) && prop.ValueKind == JsonValueKind.Array)
            {
                profile.Plugins = JsonSerializer.Deserialize<List<PluginRegistration>>(prop.GetRawText()) ?? [];
            }
        }

        return settings;
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        if (!Directory.Exists(AppDir))
            Directory.CreateDirectory(AppDir);

        var json = JsonSerializer.Serialize(settings, SerializerOptions.Indented);

        // Write to a unique temp file and swap it in, so an interrupted save can't truncate settings.json.
        var tempPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void QuarantineCorruptSettingsFile(JsonException exception)
    {
        RecoveredFromCorruptSettings = true;
        var quarantinePath = Path.Combine(AppDir, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Move(SettingsFilePath, quarantinePath, overwrite: true);
            QuarantinedSettingsFilePath = quarantinePath;
            LaunchDiagnostics.LogException($"settings.json was unreadable and was moved to '{quarantinePath}'", exception);
        }
        catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException("settings.json was unreadable and could not be moved aside", moveException);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
