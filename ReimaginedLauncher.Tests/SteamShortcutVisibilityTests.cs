using System.Text;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamShortcutVisibilityTests
{
    private static readonly byte[] HiddenHeader = Encoding.UTF8.GetBytes("\u0002IsHidden\0");
    private static readonly LauncherSteamShortcut Helper = new("Game Session", "\"/apps/launcher\"", "--steam-game /state");

    private static int HiddenOffset(byte[] data, string name)
    {
        var nameOffset = data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name + "\0"));
        return nameOffset + data.AsSpan(nameOffset).IndexOf(HiddenHeader);
    }

    [Fact]
    public void MigrationChangesOnlyHelpersVisibilityAndRetainsItsLaunchId()
    {
        var other = new LauncherSteamShortcut("Other", "\"/apps/other\"", "-custom");
        var gui = new LauncherSteamShortcut("Native Launcher", Helper.Executable, "");
        var original = SteamShortcutFile.AppendOwnedShortcuts([], other, Helper, gui);
        var hidden = Helper with { Hidden = true };
        var expected = original.ToArray();
        expected[HiddenOffset(original, Helper.Name) + HiddenHeader.Length] = 1;
        var updated = SteamShortcutFile.AppendOwnedShortcuts(original, hidden, gui);
        Assert.Equal(Helper.AppId, hidden.AppId);
        Assert.Equal(Helper.GameId, hidden.GameId);
        Assert.Equal(expected, updated);
        Assert.Equal(updated, SteamShortcutFile.AppendOwnedShortcuts(updated, hidden, gui));
    }

    [Fact]
    public void FreshHelperIsHiddenWhileNativeLauncherIsVisible()
    {
        var gui = new LauncherSteamShortcut("Native Launcher", Helper.Executable, "");
        var data = SteamShortcutFile.AppendOwnedShortcuts([], Helper with { Hidden = true }, gui);
        Assert.Equal(1u, BitConverter.ToUInt32(data, HiddenOffset(data, Helper.Name) + HiddenHeader.Length));
        Assert.Equal(0u, BitConverter.ToUInt32(data, HiddenOffset(data, gui.Name) + HiddenHeader.Length));
    }

    [Fact]
    public void MissingVisibilityFieldIsInsertedWithoutDuplicateEntry()
    {
        var data = SteamShortcutFile.AppendOwnedShortcuts([], Helper);
        var offset = HiddenOffset(data, Helper.Name);
        var missing = data[..offset].Concat(data[(offset + HiddenHeader.Length + 4)..]).ToArray();
        var hidden = Helper with { Hidden = true };
        var updated = SteamShortcutFile.AppendOwnedShortcuts(missing, hidden);
        Assert.Equal(data.Length, updated.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(updated, HiddenOffset(updated, Helper.Name) + HiddenHeader.Length));
        Assert.Equal(updated, SteamShortcutFile.AppendOwnedShortcuts(updated, hidden));
    }

    [Fact]
    public void UnspecifiedVisibilityPreservesExistingUserChoiceAndExplicitUnhideWorks()
    {
        var hidden = SteamShortcutFile.AppendOwnedShortcuts([], Helper with { Hidden = true });
        Assert.Equal(hidden, SteamShortcutFile.AppendOwnedShortcuts(hidden, Helper));
        Assert.Equal(SteamShortcutFile.AppendOwnedShortcuts([], Helper),
            SteamShortcutFile.AppendOwnedShortcuts(hidden, Helper with { Hidden = false }));
    }
}
