using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LauncherInstanceTests
{
    [Fact]
    public void SecondLaunchActivatesOwnerAndDoesNotReleaseItsLock()
    {
        var identity = Guid.NewGuid().ToString();
        using var owner = new LauncherInstance(identity);
        Assert.True(owner.IsPrimary);
        var activations = 0;
        owner.SetActivationHandler(() => Interlocked.Increment(ref activations));
        NotifyFromOtherThread(identity);
        NotifyFromOtherThread(identity);
        Assert.Equal(2, activations);
    }

    [Fact]
    public void ActivationDuringStartupIsDeliveredWhenWindowIsReady()
    {
        var identity = Guid.NewGuid().ToString();
        using var owner = new LauncherInstance(identity);
        NotifyFromOtherThread(identity);
        var activated = false;
        owner.SetActivationHandler(() => activated = true);
        Assert.True(activated);
    }

    [Fact]
    public void ExitReleasesOwnershipForNextLaunch()
    {
        var identity = Guid.NewGuid().ToString();
        using (var first = new LauncherInstance(identity)) Assert.True(first.IsPrimary);
        using var next = new LauncherInstance(identity);
        Assert.True(next.IsPrimary);
    }

    [Fact]
    public void DifferentPreviewIdentityCanRunIndependently()
    {
        using var release = new LauncherInstance(Guid.NewGuid() + "release");
        using var preview = new LauncherInstance(Guid.NewGuid() + "preview");
        Assert.True(release.IsPrimary);
        Assert.True(preview.IsPrimary);
    }

    private static void NotifyFromOtherThread(string identity)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var second = new LauncherInstance(identity);
                Assert.False(second.IsPrimary);
                Assert.True(second.ActivateExistingAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) throw failure;
    }
}
