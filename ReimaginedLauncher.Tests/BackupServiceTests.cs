using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reimagined-backup-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LadderBackupUsesPreparedFolderDespiteNormalModAndManualOverride(bool normalInstalled)
    {
        var profile = CreateProfile(LaunchExperience.Ladder);
        WriteModInfo("ReimaginedLadder", "UntrustedBundlePath/");
        if (normalInstalled) WriteModInfo("Reimagined", "NormalCharacters/");
        profile.SaveDirectory = Path.Combine(_root, "stale-override");

        var preparation = await LadderSaveDirectoryService.PrepareAsync(
            profile.InstallDirectory, Guid.NewGuid(), "Test Season", SavedGamesPath);

        Assert.NotNull(preparation.DirectoryPath);
        Assert.Equal(preparation.DirectoryPath, BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
    }

    [Fact]
    public void MissingLadderMetadataDoesNotFallBackToNormalCharacters()
    {
        var profile = CreateProfile(LaunchExperience.Ladder);
        WriteModInfo("Reimagined", "NormalCharacters/");
        profile.SaveDirectory = _root;
        Assert.Equal(string.Empty, BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
    }

    [Theory]
    [InlineData(LaunchExperience.Offline)]
    [InlineData(LaunchExperience.Online)]
    public void NormalLaunchUsesNormalMetadataAndPreservesManualOverride(LaunchExperience experience)
    {
        var profile = CreateProfile(experience);
        WriteModInfo("Reimagined", "NormalCharacters/");
        WriteModInfo("ReimaginedLadder", "LadderCharacters/");
        Directory.CreateDirectory(Path.Combine(SavedGamesPath, "Diablo II Resurrected", "mods"));
        Assert.Equal(Path.Combine(SavedGamesPath, "Diablo II Resurrected", "mods", "NormalCharacters"),
            BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
        profile.SaveDirectory = Path.Combine(_root, "custom");
        Assert.Equal(profile.SaveDirectory, BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
    }

    [Fact]
    public void D2RmmPreservesExplicitDirectoryEvenWithLadderExperience()
    {
        var profile = CreateProfile(LaunchExperience.Ladder);
        profile.Type = InstallationType.D2RMM;
        Assert.Equal(string.Empty, BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
        profile.SaveDirectory = Path.Combine(_root, "custom");
        Assert.Equal(profile.SaveDirectory, BackupService.GetResolvedSaveDirectory(profile, SavedGamesPath));
    }

    private string SavedGamesPath => Path.Combine(_root, "Saved Games");

    private InstallationProfile CreateProfile(LaunchExperience experience) => new()
    {
        Type = InstallationType.BattleNet,
        LaunchExperience = experience,
        InstallDirectory = Path.Combine(_root, "install")
    };

    private void WriteModInfo(string modName, string savePath)
    {
        var directory = Path.Combine(_root, "install", "mods", modName, modName + ".mpq");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "modinfo.json"), "{\"savepath\":\"" + savePath + "\"}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
