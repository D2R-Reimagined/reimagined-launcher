using System.Text;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamAppImageShortcutTests
{
    private static readonly string Payload = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "apps", "launcher.AppImage"));
    private static string Quote(string text) => "\"" + text + "\"";
    private static byte[] ReplaceField(byte[] data, string key, string before, string after)
    {
        var original = Encoding.UTF8.GetBytes("\u0001" + key + "\0" + before + "\0");
        var offset = data.AsSpan().IndexOf(original);
        Assert.True(offset >= 0);
        return data[..offset].Concat(Encoding.UTF8.GetBytes("\u0001" + key + "\0" + after + "\0"))
            .Concat(data[(offset + original.Length)..]).ToArray();
    }

    [Theory]
    [InlineData("")]
    [InlineData("--appimage-extract-and-run")]
    public void KnownOldOptionsMigrateOnlyOwnedExeAndArgumentsWithoutChangingIds(string oldArguments)
    {
        var owned = new LauncherSteamShortcut("Native Launcher", Quote(Payload), oldArguments, "Legacy Native");
        var other = new LauncherSteamShortcut("Other game", "\"/apps/other\"", "--custom");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], other, owned);
        var updatedShortcut = SteamGameHandoff.CreateLauncherShortcut("Native Launcher", Payload, "", "Legacy Native");
        var updated = SteamShortcutFile.AppendOwnedShortcuts(original, updatedShortcut);
        var expected = ReplaceField(original, "Exe", owned.Executable, updatedShortcut.Executable);
        expected = ReplaceField(expected, "LaunchOptions", oldArguments, "");
        Assert.Equal(expected, updated);
        Assert.Equal(owned.AppId, updatedShortcut.AppId);
        Assert.Equal(owned.GameId, updatedShortcut.GameId);
        Assert.Equal(updated, SteamShortcutFile.AppendOwnedShortcuts(updated, updatedShortcut));
    }

    [Fact]
    public void MigrationRejectsUnrecognizedUserOptions()
    {
        var owned = new LauncherSteamShortcut("Native Launcher", Quote(Payload), "custom-command %command%", "Legacy Native");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], owned);
        var updated = SteamGameHandoff.CreateLauncherShortcut("Native Launcher", Payload, "", "Legacy Native");
        Assert.Throws<InvalidDataException>(() => SteamShortcutFile.AppendOwnedShortcuts(original, updated));
    }

    [Fact]
    public void MigrationRejectsAnotherExecutableClaimingTheSameId()
    {
        var updated = SteamGameHandoff.CreateLauncherShortcut("Native Launcher", Payload, "", "Legacy Native");
        var collision = updated with { Executable = "\"/apps/other\"" };
        var original = SteamShortcutFile.AppendOwnedShortcuts([], collision);
        Assert.Throws<InvalidDataException>(() => SteamShortcutFile.AppendOwnedShortcuts(original, updated));
    }

    [Fact]
    public void HelperRetainsItsQuotedStatePathAndLegacyIdentity()
    {
        const string arguments = "--steam-game \"/home/deck/state with spaces\"";
        var original = new LauncherSteamShortcut("Game Session", Quote(Payload), arguments, "Legacy Session");
        var helper = SteamGameHandoff.CreateLauncherShortcut("Game Session", Payload, arguments, "Legacy Session", true);
        Assert.Equal(arguments, helper.Arguments);
        Assert.Equal(original.AppId, helper.AppId);
        Assert.Equal(original.GameId, helper.GameId);
        Assert.True(helper.Hidden);
        Assert.EndsWith(".sh\"", helper.Executable);
    }

    [Fact]
    public void BootstrapDoesNotChangeTheValidatedAppImageSessionIdentity()
    {
        var shortcut = SteamGameHandoff.CreateLauncherShortcut("Diablo II: Reimagined (Native Launcher)", Payload, "", "D2R Reimagined (Native Launcher)");
        var registration = new SteamHandoffRegistration(1, "/steam", Payload);
        Assert.NotNull(SteamGameHandoff.GetLauncherSession(registration, Payload, shortcut.AppId.ToString(), shortcut.GameId.ToString()));
        Assert.Null(SteamGameHandoff.GetLauncherSession(registration, Payload + ".other", shortcut.AppId.ToString(), shortcut.GameId.ToString()));
    }

    [Fact]
    public void NonAppImageExecutablesKeepExistingTargetAndArguments()
    {
        const string arguments = "--steam-game \"/state\"";
        var path = Path.ChangeExtension(Payload, null);
        var original = new LauncherSteamShortcut("Native Launcher", Quote(path), arguments, "Legacy Native");
        var shortcut = SteamGameHandoff.CreateLauncherShortcut("Native Launcher", path, arguments, "Legacy Native");
        Assert.Equal(original, shortcut);
    }

    [Fact]
    public void SetupCreatesIdempotentScriptWithoutOverwritingAnotherFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steam-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var image = Path.Combine(directory, "launcher.AppImage");
            var script = Path.ChangeExtension(image, ".sh");
            SteamGameHandoff.PrepareShortcutBootstrap(image);
            var original = File.ReadAllBytes(script);
            Assert.Equal(SteamGameHandoff.GetShortcutBootstrap(image), File.ReadAllText(script));
            Assert.False(original.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            SteamGameHandoff.PrepareShortcutBootstrap(image);
            Assert.Equal(original, File.ReadAllBytes(script));
            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            File.WriteAllText(script, "unrelated user script");
            Assert.Throws<InvalidDataException>(() => SteamGameHandoff.PrepareShortcutBootstrap(image));
            Assert.Equal("unrelated user script", File.ReadAllText(script));
        }
        finally { Directory.Delete(directory, true); }
    }
}
