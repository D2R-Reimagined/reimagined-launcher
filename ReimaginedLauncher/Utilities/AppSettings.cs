namespace ReimaginedLauncher.Utilities;

public class AppSettings
{
    public string? InstallDirectory { get; set; }
    public bool IsInstallDirectoryValidated { get; set; }
    public string? BackupSaveDirectory { get; set; }
    public string? NexusModsSSOApiKey { get; set; }
    public bool UseDirectLaunch { get; set; }
    public bool NoSound { get; set; }
    public bool SkipLogoVideo { get; set; }
    public bool NoRumble { get; set; }
    public bool ResetOfflineMaps { get; set; }
    public bool EnableRespec { get; set; }
    public int? PlayersCount { get; set; }
}
