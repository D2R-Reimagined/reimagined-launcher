using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ReimaginedLauncher.Utilities;

public enum InstallationType
{
    BattleNet,
    Steam,
    D2RMM
}

public class InstallationProfile
{
    public InstallationType Type { get; set; }
    public string? InstallDirectory { get; set; }
    public string? SteamDirectory { get; set; }
    public bool IsInstallDirectoryValidated { get; set; }
    public string? BackupSaveDirectory { get; set; }
    public bool AutomaticBackupsEnabled { get; set; } = true;
    public int BackupIntervalMinutes { get; set; } = 60;
    public int BackupAmount { get; set; } = 10;
    public bool NoSound { get; set; }
    public bool NoRumble { get; set; }
    public bool ForceDesktop { get; set; }
    public bool ResetOfflineMaps { get; set; }
    public bool EnableRespec { get; set; }
    public int? PlayersCount { get; set; }
    public int SkillPointsPerLevel { get; set; } = 1;
    public int AttributesPerLevel { get; set; } = 5;
    public int MaxSkillLevel { get; set; } = 25;
    public int NormalResistPenalty { get; set; }
    public int NightmareResistPenalty { get; set; } = -60;
    public int HellResistPenalty { get; set; } = -120;
    public bool RemovePaladinAuraSound { get; set; }
    public bool RemoveSplashVfx { get; set; }
    public List<PluginRegistration> Plugins { get; set; } = [];
    public bool MakeTooltipBackgroundOpaque { get; set; }
}

public class AppSettings
{
    public double UiScale { get; set; } = 1.0;
    public int LastReadAnnouncementNumber { get; set; }
    public string? NexusModsSSOApiKey { get; set; }
    public bool? NexusPremiumDownloadAccess { get; set; }
    
    public List<InstallationProfile> Profiles { get; set; } = [];
    public int SelectedProfileIndex { get; set; }

    [JsonIgnore]
    public InstallationProfile CurrentProfile
    {
        get
        {
            if (Profiles.Count == 0)
            {
                // Default profiles
                Profiles.Add(new InstallationProfile { Type = InstallationType.BattleNet });
                Profiles.Add(new InstallationProfile { Type = InstallationType.Steam });
                Profiles.Add(new InstallationProfile { Type = InstallationType.D2RMM, AutomaticBackupsEnabled = false });
            }
            if (SelectedProfileIndex < 0 || SelectedProfileIndex >= Profiles.Count)
            {
                SelectedProfileIndex = 0;
            }
            return Profiles[SelectedProfileIndex];
        }
    }

    // Proxy properties for backward compatibility
    [JsonIgnore] public string? InstallDirectory { get => CurrentProfile.InstallDirectory; set => CurrentProfile.InstallDirectory = value; }
    [JsonIgnore] public bool IsInstallDirectoryValidated { get => CurrentProfile.IsInstallDirectoryValidated; set => CurrentProfile.IsInstallDirectoryValidated = value; }
    [JsonIgnore] public string? BackupSaveDirectory { get => CurrentProfile.BackupSaveDirectory; set => CurrentProfile.BackupSaveDirectory = value; }
    [JsonIgnore] public bool AutomaticBackupsEnabled { get => CurrentProfile.AutomaticBackupsEnabled; set => CurrentProfile.AutomaticBackupsEnabled = value; }
    [JsonIgnore] public int BackupIntervalMinutes { get => CurrentProfile.BackupIntervalMinutes; set => CurrentProfile.BackupIntervalMinutes = value; }
    [JsonIgnore] public int BackupAmount { get => CurrentProfile.BackupAmount; set => CurrentProfile.BackupAmount = value; }
    [JsonIgnore] public bool NoSound { get => CurrentProfile.NoSound; set => CurrentProfile.NoSound = value; }
    [JsonIgnore] public bool NoRumble { get => CurrentProfile.NoRumble; set => CurrentProfile.NoRumble = value; }
    [JsonIgnore] public bool ForceDesktop { get => CurrentProfile.ForceDesktop; set => CurrentProfile.ForceDesktop = value; }
    [JsonIgnore] public bool ResetOfflineMaps { get => CurrentProfile.ResetOfflineMaps; set => CurrentProfile.ResetOfflineMaps = value; }
    [JsonIgnore] public bool EnableRespec { get => CurrentProfile.EnableRespec; set => CurrentProfile.EnableRespec = value; }
    [JsonIgnore] public int? PlayersCount { get => CurrentProfile.PlayersCount; set => CurrentProfile.PlayersCount = value; }
    [JsonIgnore] public int SkillPointsPerLevel { get => CurrentProfile.SkillPointsPerLevel; set => CurrentProfile.SkillPointsPerLevel = value; }
    [JsonIgnore] public int AttributesPerLevel { get => CurrentProfile.AttributesPerLevel; set => CurrentProfile.AttributesPerLevel = value; }
    [JsonIgnore] public int MaxSkillLevel { get => CurrentProfile.MaxSkillLevel; set => CurrentProfile.MaxSkillLevel = value; }
    [JsonIgnore] public int NormalResistPenalty { get => CurrentProfile.NormalResistPenalty; set => CurrentProfile.NormalResistPenalty = value; }
    [JsonIgnore] public int NightmareResistPenalty { get => CurrentProfile.NightmareResistPenalty; set => CurrentProfile.NightmareResistPenalty = value; }
    [JsonIgnore] public int HellResistPenalty { get => CurrentProfile.HellResistPenalty; set => CurrentProfile.HellResistPenalty = value; }
    [JsonIgnore] public bool RemovePaladinAuraSound { get => CurrentProfile.RemovePaladinAuraSound; set => CurrentProfile.RemovePaladinAuraSound = value; }
    [JsonIgnore] public bool RemoveSplashVfx { get => CurrentProfile.RemoveSplashVfx; set => CurrentProfile.RemoveSplashVfx = value; }
    [JsonIgnore] public List<PluginRegistration> Plugins { get => CurrentProfile.Plugins; set => CurrentProfile.Plugins = value; }
    [JsonIgnore] public bool MakeTooltipBackgroundOpaque { get => CurrentProfile.MakeTooltipBackgroundOpaque; set => CurrentProfile.MakeTooltipBackgroundOpaque = value; }
}
