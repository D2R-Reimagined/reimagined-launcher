using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;

namespace ReimaginedLauncher.Utilities;

public enum InstallationType
{
    BattleNet,
    Steam,
    D2RMM,
    Lutris
}

public static class InstallationTypes
{
    /// <summary>
    /// Lutris is a Linux application, so on Windows the type is shown in the
    /// dropdown as unavailable rather than being selectable. Unavailable types
    /// get no profile, which keeps a Windows settings file exactly as it was.
    /// </summary>
    public static bool IsAvailable(InstallationType type)
        => type != InstallationType.Lutris || OperatingSystem.IsLinux();
}

public enum LaunchExperience
{
    Offline,
    Online,
    Ladder
}

public enum StackDropOption
{
    Default,
    Unstacked,
    Stacked,
    Disabled
}

public class InstallationProfile
{
    public InstallationType Type { get; set; }
    public LaunchExperience LaunchExperience { get; set; }
    public Guid? SelectedLadderId { get; set; }
    public Dictionary<string, List<Guid>> SelectedLadderExtensions { get; set; } = [];
    public string? InstallDirectory { get; set; }
    public string? SteamDirectory { get; set; }

    // The id drives the launch URI; the slug locates the game's YAML config.
    public int? LutrisGameId { get; set; }
    public string? LutrisGameSlug { get; set; }
    public string? LutrisGameName { get; set; }
    public bool IsInstallDirectoryValidated { get; set; }
    public string? SaveDirectory { get; set; }

    // User-located path to the Reimagined.mpq game mod folder. Only set when the
    // folder cannot be resolved automatically from the install directory.
    public string? ReimaginedModFolderOverride { get; set; }
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
    public bool CustomMapSeedEnabled { get; set; }
    public uint CustomMapSeed { get; set; }
    public int SkillPointsPerLevel { get; set; } = 1;
    public int AttributesPerLevel { get; set; } = 5;
    public int MaxSkillLevel { get; set; } = 25;
    public int NormalResistPenalty { get; set; }
    public int NightmareResistPenalty { get; set; } = -60;
    public int HellResistPenalty { get; set; } = -120;
    public bool RemovePaladinAuraSound { get; set; }
    public bool RemoveSplashVfx { get; set; }
    public bool DisableExtraBlood { get; set; }
    public List<PluginRegistration> Plugins { get; set; } = [];
    public bool MakeTooltipBackgroundOpaque { get; set; }
    public bool RemoveHelmetVisual { get; set; }
    public bool TerrorizeAllZones { get; set; }
    public int ZoneDurationMinutes { get; set; } = 60;
    public bool TerrorZonePurpleOverlay { get; set; }
    public bool RemoveFadeEffect { get; set; }
    public bool RestoreTerrorZoneFanfare { get; set; }
    public StackDropOption OrbStackDrops { get; set; }
    public StackDropOption RuneStackDrops { get; set; }
    public bool RemoveVignette { get; set; }
}

public class AppSettings
{
    public double UiScale { get; set; } = 1.0;
    public bool LadderPlayModeUnlocked { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; }
    public bool DisableLauncherUpdates { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool IsMaximized { get; set; }
    public int LastReadAnnouncementNumber { get; set; }

    // User-located overrides for the quick-open folder buttons on the Settings
    // tab. Only populated when the default location is unavailable or the user
    // forces a folder picker (Ctrl+Click).
    public string? LauncherSettingsFolderOverride { get; set; }
    public string? LauncherInstallFolderOverride { get; set; }

    [JsonIgnore]
    public string? NexusModsSSOApiKey { get; set; }

    // Persisted form of the Nexus API key. Stored encrypted-at-rest via
    // SecretProtector so that settings.json never holds the raw key. Legacy
    // plaintext values are read transparently and re-encrypted on next save.
    [JsonPropertyName("NexusModsSSOApiKey")]
    public string? NexusModsSSOApiKeyEncrypted
    {
        get => SecretProtector.Protect(NexusModsSSOApiKey);
        set => NexusModsSSOApiKey = SecretProtector.Unprotect(value);
    }

    [JsonIgnore]
    public string? D2RReimaginedRefreshToken { get; set; }

    [JsonPropertyName("D2RReimaginedRefreshToken")]
    public string? D2RReimaginedRefreshTokenEncrypted
    {
        get => SecretProtector.Protect(D2RReimaginedRefreshToken);
        set => D2RReimaginedRefreshToken = SecretProtector.Unprotect(value);
    }

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

    /// <summary>
    /// Adds the Lutris profile to a settings file written before the type
    /// existed. It is appended, so existing profiles keep their position and
    /// <see cref="SelectedProfileIndex"/> still refers to the same installation.
    /// Windows never gets one: the type cannot be selected there.
    /// </summary>
    public void EnsureLutrisProfile()
    {
        if (!InstallationTypes.IsAvailable(InstallationType.Lutris))
        {
            return;
        }

        // Seeds the default profiles when the settings file is brand new.
        _ = CurrentProfile;

        if (!Profiles.Exists(profile => profile.Type == InstallationType.Lutris))
        {
            Profiles.Add(new InstallationProfile { Type = InstallationType.Lutris });
        }
    }
}
