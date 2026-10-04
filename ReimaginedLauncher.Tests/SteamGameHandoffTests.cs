using System.Text;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamGameHandoffTests
{
    [Fact]
    public void ShortcutReplacementPreservesExistingUnixPermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "steam-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "shortcuts.vdf");
            foreach (var mode in new[] { UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                UnixFileMode.UserRead | UnixFileMode.UserWrite })
            {
                File.WriteAllText(path, "old");
                File.SetUnixFileMode(path, mode);
                SteamGameHandoff.WriteShortcutFile(path, [1, 2, 3]);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
                Assert.Equal(mode, File.GetUnixFileMode(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SetupUsesOriginalAppImageInsteadOfTemporaryMountedExecutable()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(Path.GetFullPath(path), SteamGameHandoff.ResolveSetupLauncherPath(path, "/tmp/.mount_example/usr/bin/ReimaginedLauncher"));
            Assert.Throws<InvalidOperationException>(() => SteamGameHandoff.ResolveSetupLauncherPath(path + ".missing", path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetupCopiesAppImageToStableLocationAndPreservesPreviousBuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Download.AppImage");
            var destinationDirectory = Path.Combine(root, "installed");
            File.WriteAllText(source, "first");
            var installed = SteamGameHandoff.PrepareSetupAppImage(source, destinationDirectory);
            Assert.Equal("first", File.ReadAllText(installed));
            File.WriteAllText(source, "second");
            Assert.Equal(installed, SteamGameHandoff.PrepareSetupAppImage(source, destinationDirectory));
            Assert.Equal("second", File.ReadAllText(installed));
            Assert.Equal("first", File.ReadAllText(Assert.Single(Directory.GetFiles(destinationDirectory, "*.bak"))));
            Assert.Equal(installed, SteamGameHandoff.PrepareSetupAppImage(installed, destinationDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SetupFindsExistingAccountsAndTheirMostRecentDisplayName()
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        foreach (var id in new[] { "71303878", "123", "anonymous", "0" })
            Directory.CreateDirectory(Path.Combine(root, "userdata", id, "config"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"),
                "\"users\" { \"76561198031569606\" { \"PersonaName\" \"Deck Player\" \"MostRecent\" \"1\" } }");
            var accounts = SteamGameHandoff.GetSetupAccounts(root);
            Assert.Equal(2, accounts.Length);
            Assert.Equal("71303878", accounts[0].UserId);
            Assert.Equal("Deck Player", accounts[0].Name);
            Assert.True(accounts[0].MostRecent);
            Assert.False(accounts[1].MostRecent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SetupWithoutLoginMetadataStillListsExistingAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "userdata", "123", "config"));
        try
        {
            Assert.Equal("123", Assert.Single(SteamGameHandoff.GetSetupAccounts(root)).UserId);
            Assert.Empty(SteamGameHandoff.GetSetupAccounts(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RegisteringShortcutsPreservesExistingBinaryEntriesAndIsIdempotent()
    {
        var unrelated = new LauncherSteamShortcut("Unrelated — Game", "\"/games/original.exe\"", "-custom");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], unrelated);
        var owned = new LauncherSteamShortcut("Launcher Session", "\"/apps/launcher\"", "--steam-game \"/state with spaces\"");
        var appended = SteamShortcutFile.AppendOwnedShortcuts(original, owned);
        Assert.Equal(original[..^2], appended[..(original.Length - 2)]);
        Assert.Equal(appended, SteamShortcutFile.AppendOwnedShortcuts(appended, owned));
        Assert.Contains("Unrelated — Game", Encoding.UTF8.GetString(appended));
    }

    [Fact]
    public void RenamingOwnedShortcutsPreservesIdsAndOtherEntryBytesWithoutDuplicates()
    {
        var unrelated = new LauncherSteamShortcut("Other game", "\"/games/other\"", "");
        var legacy = new LauncherSteamShortcut("D2R Reimagined (Native Launcher)", "\"/apps/launcher\"", "");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], unrelated, legacy);
        var renamed = new LauncherSteamShortcut("Diablo II: Reimagined (Native Launcher)", legacy.Executable,
            legacy.Arguments, legacy.Name);
        var updated = SteamShortcutFile.AppendOwnedShortcuts(original, renamed);
        var before = Encoding.UTF8.GetBytes(legacy.Name + "\0");
        var after = Encoding.UTF8.GetBytes(renamed.Name + "\0");
        var offset = original.AsSpan().IndexOf(before);
        var expected = original[..offset].Concat(after).Concat(original[(offset + before.Length)..]).ToArray();
        Assert.Equal(legacy.AppId, renamed.AppId);
        Assert.Equal(expected, updated);
        Assert.Equal(updated, SteamShortcutFile.AppendOwnedShortcuts(updated, renamed));
        Assert.Equal(updated, SteamShortcutFile.AppendOwnedShortcuts([], unrelated, renamed));
    }

    [Fact]
    public void LegacyRenameRejectsModifiedLaunchArguments()
    {
        var legacy = new LauncherSteamShortcut("Old name", "\"/apps/launcher\"", "--changed");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], legacy);
        var renamed = new LauncherSteamShortcut("New name", legacy.Executable, "--expected", legacy.Name);
        Assert.Throws<InvalidDataException>(() => SteamShortcutFile.AppendOwnedShortcuts(original, renamed));
    }

    [Fact]
    public void RegistrationRejectsIdCollisionsWithoutMutatingInput()
    {
        var shortcut = new LauncherSteamShortcut("Session", "\"/apps/launcher\"", "--original");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], shortcut);
        var snapshot = original.ToArray();
        Assert.Throws<InvalidDataException>(() => SteamShortcutFile.AppendOwnedShortcuts(original, shortcut with { Arguments = "--different" }));
        Assert.Equal(snapshot, original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TruncatedShortcutFilesAreRejected(int trim)
    {
        var original = SteamShortcutFile.AppendOwnedShortcuts([], new LauncherSteamShortcut("Session", "\"/apps/launcher\"", ""));
        var corrupted = original[..^(trim + 1)];
        Assert.ThrowsAny<IOException>(() => SteamShortcutFile.AppendOwnedShortcuts(corrupted, new LauncherSteamShortcut("Another", "\"/apps/other\"", "")));
    }

    [Fact]
    public void ShortcutIdsUseUnsignedCrcAndNonSteamGameIdLayout()
    {
        var shortcut = new LauncherSteamShortcut("Example", "\"/apps/launcher\"", "");
        Assert.True(shortcut.AppId >= 0x80000000);
        Assert.Equal(((ulong)shortcut.AppId << 32) | 0x02000000, shortcut.GameId);
        Assert.Equal(0xcbf43926u, SteamShortcutFile.CalculateAppId("123456789", ""));
    }

    [Fact]
    public void ArgumentSplittingPreservesQuotedValuesAndShellMetacharactersAsData()
    {
        Assert.Equal(new[] { "-mod", "Reimagined", "-txt", "-forcedesktop", "-ip", "host name", ";", "$(no-shell)" },
            SteamGameHandoff.SplitArguments("-mod Reimagined -txt -forcedesktop -ip \"host name\" ; $(no-shell)"));
        Assert.Throws<ArgumentException>(() => SteamGameHandoff.SplitArguments("-mod \"unfinished"));
    }

    [Theory]
    [InlineData("/games/D2RLoader.exe\0-mod\0Reimagined", true)]
    [InlineData("Z:\\games\\D2RLoader.exe\0-txt", true)]
    [InlineData("/usr/bin/python3\0/proton\0run\0/games/D2RLoader.exe", false)]
    [InlineData("/apps/ReimaginedLauncher\0--steam-game\0/games/D2RLoader.exe", false)]
    [InlineData("/other-game/D2RLoader.exe\0", false)]
    public void OnlyTheActualWineGameIsMonitored(string commandLine, bool expected)
        => Assert.Equal(expected, SteamGameHandoff.MatchesGameCommandLine(commandLine, "/games/D2RLoader.exe"));

    [Fact]
    public void WineDriveMappingsResolveTheActualGameWithoutMatchingUnrelatedCopies()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Steam Library"));
        var executable = Path.Combine(root, "steamapps", "common", "Diablo II Resurrected", "D2RLoader.exe");
        var drives = new Dictionary<char, string> { ['S'] = root };
        const string command = "S:\\steamapps\\common\\Diablo II Resurrected\\D2RLoader.exe\0-mod\0Reimagined";
        Assert.True(SteamGameHandoff.MatchesGameCommandLine(command, executable, drives));
        Assert.False(SteamGameHandoff.MatchesGameCommandLine(command, executable));
        Assert.False(SteamGameHandoff.MatchesGameCommandLine(command, Path.Combine(root, "other", "D2RLoader.exe"), drives));
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Offline)]
    [InlineData(LaunchExperience.Ladder)]
    public void HandoffPreservesModeFlagsPrefixAndD2RAuthentication(LaunchExperience experience)
    {
        var root = Path.Combine(Path.GetTempPath(), "handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            var steam = Path.Combine(root, "Steam");
            var apps = Path.Combine(steam, "steamapps");
            var game = Path.Combine(apps, "common", "Diablo II Resurrected");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(Path.Combine(apps, "compatdata", "2536520", "pfx"));
            File.WriteAllText(Path.Combine(apps, "appmanifest_2536520.acf"), "");
            var proton = Path.Combine(root, "proton"); File.WriteAllText(proton, "");
            var profile = new InstallationProfile { LaunchExperience = experience, ForceDesktop = true,
                NoRumble = true, PlayersCount = 5, ResetOfflineMaps = true };
            var executable = Path.Combine(game, experience == LaunchExperience.Offline ? "D2R.exe" : "D2RLoader.exe");
            var request = new SteamHandoffRequest("test", DateTimeOffset.UtcNow, steam, game, proton,
                executable, SteamGameHandoff.SplitArguments(GameLauncherService.BuildLaunchParameters(profile)));
            var info = SteamGameHandoff.CreateGameStartInfo(request);
            Assert.Equal(proton, info.FileName);
            Assert.Contains("-forcedesktop", info.ArgumentList);
            Assert.Contains("-norumble", info.ArgumentList);
            Assert.Contains(experience == LaunchExperience.Ladder ? "ReimaginedLadder" : "Reimagined", info.ArgumentList);
            Assert.Equal(experience == LaunchExperience.Offline, info.ArgumentList.Contains("-players"));
            Assert.Equal("2536520", info.Environment["SteamAppId"]);
            Assert.Equal(Path.Combine(apps, "compatdata", "2536520"), info.Environment["STEAM_COMPAT_DATA_PATH"]);
            Assert.False(info.UseShellExecute);
        }
        finally { Directory.Delete(root, true); }
    }
}
