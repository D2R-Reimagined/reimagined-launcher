using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LutrisProfileTests
{
    private static bool LutrisAvailable => InstallationTypes.IsAvailable(InstallationType.Lutris);

    [Fact]
    public void LutrisIsOnlyAvailableOnLinux()
    {
        Assert.Equal(OperatingSystem.IsLinux(), LutrisAvailable);
        Assert.True(InstallationTypes.IsAvailable(InstallationType.BattleNet));
        Assert.True(InstallationTypes.IsAvailable(InstallationType.Steam));
        Assert.True(InstallationTypes.IsAvailable(InstallationType.D2RMM));
    }

    [Fact]
    public void EnsureLutrisProfileAppendsWithoutMovingExistingProfiles()
    {
        // A settings file written before the Lutris type existed. Indexes 0-2
        // must keep pointing at the same installations after the upgrade.
        var settings = new AppSettings
        {
            Profiles =
            [
                new InstallationProfile { Type = InstallationType.BattleNet, InstallDirectory = "/bnet" },
                new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = "/steam" },
                new InstallationProfile { Type = InstallationType.D2RMM, InstallDirectory = "/d2rmm" }
            ],
            SelectedProfileIndex = 1
        };

        settings.EnsureLutrisProfile();

        Assert.Equal("/bnet", settings.Profiles[0].InstallDirectory);
        Assert.Equal("/steam", settings.Profiles[1].InstallDirectory);
        Assert.Equal("/d2rmm", settings.Profiles[2].InstallDirectory);
        Assert.Equal(InstallationType.Steam, settings.CurrentProfile.Type);

        if (LutrisAvailable)
        {
            Assert.Equal(4, settings.Profiles.Count);
            Assert.Equal(InstallationType.Lutris, settings.Profiles[3].Type);
        }
        else
        {
            Assert.Equal(3, settings.Profiles.Count);
        }
    }

    [Fact]
    public void EnsureLutrisProfileKeepsTheDefaultProfilesInEnumOrderOnAFreshFile()
    {
        // The installation type dropdown maps item index to both the enum value
        // and the profile index, so a new file must not start with Lutris first.
        var settings = new AppSettings();

        settings.EnsureLutrisProfile();

        Assert.Equal(InstallationType.BattleNet, settings.Profiles[0].Type);
        Assert.Equal(InstallationType.Steam, settings.Profiles[1].Type);
        Assert.Equal(InstallationType.D2RMM, settings.Profiles[2].Type);

        if (LutrisAvailable)
        {
            Assert.Equal(InstallationType.Lutris, settings.Profiles[3].Type);
        }
    }

    [Fact]
    public void EnsureLutrisProfileIsIdempotent()
    {
        var settings = new AppSettings();

        settings.EnsureLutrisProfile();
        var count = settings.Profiles.Count;
        settings.EnsureLutrisProfile();

        Assert.Equal(count, settings.Profiles.Count);
    }

    [Fact]
    public void ReadingCurrentProfileDoesNotCreateALutrisProfile()
    {
        var settings = new AppSettings();

        _ = settings.CurrentProfile;

        Assert.Equal(3, settings.Profiles.Count);
        Assert.DoesNotContain(settings.Profiles, profile => profile.Type == InstallationType.Lutris);
    }
}
