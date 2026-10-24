using System.IO;
using System.Linq;
using System;

namespace ReimaginedLauncher.Utilities;

internal static class SetupReadiness
{
    internal static bool ShouldOfferSetup(AppSettings settings, bool isSignedIn)
    {
        if (settings.FirstTimeSetupShown || settings.FirstTimeSetupCompleted) return false;
        var hasAccount = isSignedIn || !string.IsNullOrWhiteSpace(settings.NexusModsSSOApiKey)
            || !string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken);
        return !hasAccount || !settings.Profiles.Any(profile => GetIssue(profile) is null);
    }

    internal static string? GetIssue(InstallationProfile profile)
    {
        try { return GetPathIssue(profile); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return "The installation location cannot be read. Choose an accessible folder."; }
    }

    private static string? GetPathIssue(InstallationProfile profile)
    {
        var valid = profile.Type switch
        {
            InstallationType.Steam => InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory),
            InstallationType.D2RMM => Directory.Exists(profile.InstallDirectory) && InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory),
            _ => InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory)
        };
        if (!valid) return "Choose a valid installation location to continue.";
        if (profile.Type == InstallationType.Steam && !File.Exists(profile.SteamDirectory))
            return "Locate the Steam executable to continue.";
        if (profile.Type == InstallationType.Lutris && profile.LutrisGameId is null)
            return "Select your Diablo II: Resurrected entry in Lutris.";
        return null;
    }
}
