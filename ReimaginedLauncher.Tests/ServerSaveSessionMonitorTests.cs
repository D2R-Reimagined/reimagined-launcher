using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ServerSaveSessionMonitorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private static ServerSaveStatus Status(bool running = true) => new(
        "session", Guid.NewGuid(), "ready", "Saved successfully.", "", "", Now, running,
        [new ServerSaveConfirmation("Hero.d2s", "Hero", 14, new string('a', 64), Now)]);

    [Fact]
    public async Task Wrapper_exit_keeps_monitoring_live_saves_until_plugin_shutdown()
    {
        var elapsed = 0;
        var reads = 0;
        await ServerSaveSessionMonitor.WaitForInactiveAsync(
            () =>
            {
                reads++;
                // A transient read failure must not end a healthy session.
                return Task.FromResult<ServerSaveStatus?>(reads == 2 ? null
                    : Status(running: reads < 5) with { UpdatedAtUtc = Now.AddSeconds(elapsed) });
            },
            () => Now.AddSeconds(elapsed),
            () => { elapsed += 20; return Task.CompletedTask; });

        Assert.Equal(5, reads);
        Assert.Equal(80, elapsed);
    }

    [Fact]
    public async Task Missing_heartbeat_eventually_ends_tracking()
    {
        var elapsed = 0;
        await ServerSaveSessionMonitor.WaitForInactiveAsync(
            () => Task.FromResult<ServerSaveStatus?>(Status()),
            () => Now.AddSeconds(elapsed),
            () => { elapsed += 2; return Task.CompletedTask; });

        Assert.Equal(46, elapsed);
    }

    [Fact]
    public void Lost_process_tracking_does_not_claim_the_game_closed()
    {
        var display = ServerSaveSessionMonitor.DescribeAfterWatch(Status(), Now.AddMinutes(1), false);
        Assert.DoesNotContain("closed", display.Message);
        Assert.Contains("lost track", display.Message);
        Assert.Contains("v14", display.Confirmed);
    }

    [Fact]
    public void Confirmed_exit_still_warns_about_missing_final_acknowledgement()
    {
        var display = ServerSaveSessionMonitor.DescribeAfterWatch(Status(), Now, true);
        Assert.Contains("closed without confirming", display.Message);
        Assert.True(display.Warning);
    }
}
