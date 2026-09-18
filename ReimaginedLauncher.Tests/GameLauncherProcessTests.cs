using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class GameLauncherProcessTests
{
    [Fact]
    public void ProcessStartsDirectlyWithTheConfiguredWorkingDirectory()
    {
        var startInfo = GameLauncherService.CreateProcessStartInfo(
            @"C:\Games\Diablo II Resurrected\D2RLoader.exe",
            "-mod Reimagined -txt",
            @"C:\Games\Diablo II Resurrected");

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(@"C:\Games\Diablo II Resurrected", startInfo.WorkingDirectory);
        Assert.Equal("-mod Reimagined -txt", startInfo.Arguments);
    }
}
