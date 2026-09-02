using ReimaginedLauncher.Views.Update;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class UpdateViewTests
{
    [Theory]
    [InlineData(false, false, false, false, true, true, false, true)]
    [InlineData(false, true, false, false, true, true, true, true)]
    [InlineData(true, false, false, false, true, true, true, false)]
    [InlineData(false, false, true, false, true, true, false, false)]
    [InlineData(false, false, false, true, true, true, false, false)]
    [InlineData(false, false, false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false, false, false)]
    public void ReinstallDoesNotRequireANewerVersionButKeepsInstallGuards(
        bool isInstallMissing,
        bool isUpdateAvailable,
        bool isLoading,
        bool isInstallInProgress,
        bool canInstallOrUpdate,
        bool isAuthenticated,
        bool expectedInstallOrUpdate,
        bool expectedReinstall)
    {
        var actions = UpdateView.GetInstallActionAvailability(
            isInstallMissing, isUpdateAvailable, isLoading, isInstallInProgress, canInstallOrUpdate, isAuthenticated);

        Assert.Equal(expectedInstallOrUpdate, actions.CanInstallOrUpdate);
        Assert.Equal(expectedReinstall, actions.CanReinstall);
    }
}
