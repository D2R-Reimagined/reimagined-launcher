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

    // Keyed by ladder id ("N"). A missing region entry means automatic; pins are admin-only.
    public Dictionary<string, List<string>> SelectedLadderRegions { get; set; } = [];
    public Dictionary<string, string> PinnedLadderServers { get; set; } = [];
    public string? InstallDirectory { get; set; }
    public string? SteamDirectory { get; set; }
    public string? ProtonExecutable { get; set; }
    public bool EnableLsfg { get; set; }
    public bool UseSteamHelperFallback { get; set; }

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
    public bool WindowedMode { get; set; }
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

    // Install directory, offline save folder and installed TXT fingerprint the automatic Convert Save Data prompt was last shown for.
    public string? SaveConversionPromptKey { get; set; }
}

public class AppSettings
{
    public bool FirstTimeSetupShown { get; set; }
    public bool FirstTimeSetupCompleted { get; set; }
    public Dictionary<string, bool> LaunchCardExpansion { get; set; } = [];
    public Dictionary<string, InstalledLootFilter> InstalledLootFilters { get; set; } = [];
    public double UiScale { get; set; } = 1.0;
    public bool MinimizeToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; }
    public bool DisableLauncherUpdates { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool IsMaximized { get; set; }
    public int LastReadAnnouncementNumber { get; set; }
    public string LastNavigationTag { get; set; } = "Launch";

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
                EnsureDefaultProfiles();
            }
            if (SelectedProfileIndex < 0 || SelectedProfileIndex >= Profiles.Count)
            {
                SelectedProfileIndex = 0;
            }
            return Profiles[SelectedProfileIndex];
        }
    }

    /// <summary>
    /// Ensures Battle.Net, Steam, and D2RMM profiles all exist in canonical enum
    /// order so the Installation Type dropdown index always maps to its matching
    /// profile, and repairs settings where a profile's Type was mutated in place.
    /// </summary>
    public void EnsureDefaultProfiles()
    {
        Profiles ??= [];
        Profiles.RemoveAll(profile => profile is null);

        var selected = SelectedProfileIndex >= 0 && SelectedProfileIndex < Profiles.Count
            ? Profiles[SelectedProfileIndex]
            : null;
        var selectedType = selected?.Type;

        // First, repair profiles whose Type was mutated in place between Battle.Net
        // and Steam by restoring their Type directly on the existing profile object
        // so all of that profile's settings remain intact.
        var hasValidBattleNet = Profiles.Exists(profile =>
            profile.Type == InstallationType.BattleNet
            && !string.IsNullOrWhiteSpace(profile.InstallDirectory)
            && !InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
            && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory));
        if (!hasValidBattleNet)
        {
            var mutatedBattleNet = Profiles.Find(profile =>
                profile.Type == InstallationType.Steam
                && !string.IsNullOrWhiteSpace(profile.InstallDirectory)
                && !InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
                && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory));
            if (mutatedBattleNet is not null)
            {
                mutatedBattleNet.Type = InstallationType.BattleNet;
                mutatedBattleNet.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(mutatedBattleNet.InstallDirectory);
                mutatedBattleNet.IsInstallDirectoryValidated = true;
                if (ReferenceEquals(selected, mutatedBattleNet))
                {
                    selectedType = InstallationType.BattleNet;
                }
            }
        }

        var hasValidSteam = Profiles.Exists(profile =>
            profile.Type == InstallationType.Steam
            && !string.IsNullOrWhiteSpace(profile.InstallDirectory)
            && InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory));
        if (!hasValidSteam)
        {
            var mutatedSteam = Profiles.Find(profile =>
                profile.Type == InstallationType.BattleNet
                && !string.IsNullOrWhiteSpace(profile.InstallDirectory)
                && InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory));
            if (mutatedSteam is not null)
            {
                mutatedSteam.Type = InstallationType.Steam;
                mutatedSteam.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(mutatedSteam.InstallDirectory);
                mutatedSteam.IsInstallDirectoryValidated = true;
                if (ReferenceEquals(selected, mutatedSteam))
                {
                    selectedType = InstallationType.Steam;
                }
            }
        }

        InstallationProfile ResolveCanonicalProfile(InstallationType type, bool defaultAutoBackup = true)
        {
            var candidates = Profiles.FindAll(profile => profile.Type == type);
            if (candidates.Count == 0)
            {
                return new InstallationProfile { Type = type, AutomaticBackupsEnabled = defaultAutoBackup };
            }

            var winner = (selected is not null && selected.Type == type ? selected : null)
                         ?? candidates.Find(profile => !string.IsNullOrWhiteSpace(profile.InstallDirectory))
                         ?? candidates[0];

            if (string.IsNullOrWhiteSpace(winner.InstallDirectory))
            {
                var withDirectory = candidates.Find(profile => !string.IsNullOrWhiteSpace(profile.InstallDirectory));
                if (withDirectory is not null)
                {
                    winner.InstallDirectory = withDirectory.InstallDirectory;
                    winner.IsInstallDirectoryValidated = withDirectory.IsInstallDirectoryValidated;
                    if (string.IsNullOrWhiteSpace(winner.SteamDirectory))
                    {
                        winner.SteamDirectory = withDirectory.SteamDirectory;
                    }
                }
            }

            return winner;
        }

        var battleNet = ResolveCanonicalProfile(InstallationType.BattleNet);
        var steam = ResolveCanonicalProfile(InstallationType.Steam);
        var d2rmm = ResolveCanonicalProfile(InstallationType.D2RMM, defaultAutoBackup: false);
        var lutris = Profiles.Exists(profile => profile.Type == InstallationType.Lutris)
            ? ResolveCanonicalProfile(InstallationType.Lutris)
            : null;

        Profiles.Clear();
        Profiles.Add(battleNet);
        Profiles.Add(steam);
        Profiles.Add(d2rmm);
        if (lutris is not null)
        {
            Profiles.Add(lutris);
        }

        if (selected is not null && Profiles.IndexOf(selected) is var selectedIndex && selectedIndex >= 0)
        {
            SelectedProfileIndex = selectedIndex;
        }
        else if (selectedType is { } targetType)
        {
            var index = Profiles.FindIndex(profile => profile.Type == targetType);
            SelectedProfileIndex = index >= 0 ? index : 0;
        }
        else if (SelectedProfileIndex < 0 || SelectedProfileIndex >= Profiles.Count)
        {
            SelectedProfileIndex = 0;
        }
    }

    public InstallationProfile SelectProfile(InstallationType type)
    {
        if (type == InstallationType.Lutris)
        {
            EnsureLutrisProfile();
        }
        else
        {
            EnsureDefaultProfiles();
        }

        var index = Profiles.FindIndex(profile => profile.Type == type);
        if (index < 0)
        {
            var created = new InstallationProfile
            {
                Type = type,
                AutomaticBackupsEnabled = type != InstallationType.D2RMM
            };
            Profiles.Add(created);
            index = Profiles.Count - 1;
        }

        SelectedProfileIndex = index;
        return Profiles[index];
    }

    /// <summary>
    /// Adds the Lutris profile to a settings file written before the type
    /// existed. It is appended, so existing profiles keep their position and
    /// <see cref="SelectedProfileIndex"/> still refers to the same installation.
    /// Windows never gets one: the type cannot be selected there.
    /// </summary>
    public void EnsureLutrisProfile()
    {
        EnsureDefaultProfiles();

        if (!InstallationTypes.IsAvailable(InstallationType.Lutris))
        {
            return;
        }

        if (!Profiles.Exists(profile => profile.Type == InstallationType.Lutris))
        {
            Profiles.Add(new InstallationProfile { Type = InstallationType.Lutris });
        }
    }
}
