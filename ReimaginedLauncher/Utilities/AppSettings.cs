namespace ReimaginedLauncher.Utilities;

public class AppSettings
{
    public string? InstallDirectory { get; set; }
    public bool IsInstallDirectoryValidated { get; set; }
    public string? BackupSaveDirectory { get; set; }
    public bool AutomaticBackupsEnabled { get; set; } = true;
    public int BackupIntervalMinutes { get; set; } = 60;
    public int BackupAmount { get; set; } = 10;
    public string? NexusModsSSOApiKey { get; set; }
    public bool? NexusPremiumDownloadAccess { get; set; }
    public bool NoSound { get; set; }
    public bool NoRumble { get; set; }
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
    public bool MakeTooltipBackgroundOpaque { get; set; }
}
