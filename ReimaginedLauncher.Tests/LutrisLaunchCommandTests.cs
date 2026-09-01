using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LutrisLaunchCommandTests
{
    [Fact]
    public void LutrisLaunchCommandMatchesTheLutrisShortcutForm()
    {
        var profile = new InstallationProfile
        {
            Type = InstallationType.Lutris,
            LutrisGameId = 145,
            LutrisGameSlug = "diablo-ii-resurrected"
        };

        var command = GameLauncherService.BuildLutrisLaunchCommand(profile);

        Assert.StartsWith("env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/145", command);
    }

    [Fact]
    public void LutrisLaunchCommandNamesTheArgumentLutrisHasToSupply()
    {
        var profile = new InstallationProfile { Type = InstallationType.Lutris, LutrisGameId = 145 };

        var command = GameLauncherService.BuildLutrisLaunchCommand(profile);

        Assert.Contains("-mod Reimagined -txt", command);
    }

    [Fact]
    public void LutrisLaunchCommandAsksForAGameWhenNoneIsSelected()
    {
        var profile = new InstallationProfile { Type = InstallationType.Lutris };

        var command = GameLauncherService.BuildLutrisLaunchCommand(profile);

        Assert.DoesNotContain("rungameid", command);
        Assert.Contains("no game selected", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LutrisProfilesDoNotGetLauncherBuiltParameters()
    {
        // The launch parameters the launcher builds cannot reach the game through
        // the Lutris URI handler, so the command must not imply that they do.
        var profile = new InstallationProfile
        {
            Type = InstallationType.Lutris,
            LutrisGameId = 145,
            NoSound = true,
            EnableRespec = true,
            PlayersCount = 8
        };

        var command = GameLauncherService.BuildLutrisLaunchCommand(profile);

        Assert.DoesNotContain("-nosound", command);
        Assert.DoesNotContain("-enablerespec", command);
        Assert.DoesNotContain("-players", command);
    }
}
