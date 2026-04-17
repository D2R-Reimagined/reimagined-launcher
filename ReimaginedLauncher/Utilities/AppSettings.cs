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
    public bool RemoveHelmetVisual { get; set; }
    public bool TerrorizeAllZones { get; set; }
    public bool TerrorZonePurpleOverlay { get; set; }
    public bool RestoreTerrorZoneFanfare { get; set; }
}

public class AppSettings
{
    public double UiScale { get; set; } = 1.0;
    public bool MinimizeToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; }
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
}
