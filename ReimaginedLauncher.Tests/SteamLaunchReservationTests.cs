using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamLaunchReservationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "steam-reservation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PendingHelperRejectsSecondPreparationWithoutChangingFirstSession()
    {
        using var first = SteamGameHandoff.Reserve(_directory);
        var config = Path.Combine(_directory, "loader.toml");
        var request = Path.Combine(_directory, "request.json");
        File.WriteAllText(config, "first mod configuration");
        File.WriteAllText(request, "first pending helper request");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        Assert.True(await GameSessionSecretsService.WriteAsync(_directory, new GameSessionSecrets("first-token", expiry, "first-ticket", "first-session")));
        var session = GameSessionSecretsService.GetPath(_directory)!;
        var before = File.ReadAllBytes(session);
        var prepared = false;

        Assert.Throws<IOException>(() =>
        {
            using var second = SteamGameHandoff.Reserve(_directory);
            prepared = true;
            File.WriteAllText(config, "second configuration");
            GameSessionSecretsService.Delete(_directory);
        });

        Assert.False(prepared);
        Assert.True(SteamGameHandoff.IsReserved(_directory));
        Assert.Equal(before, File.ReadAllBytes(session));
        Assert.Equal("first mod configuration", File.ReadAllText(config));
        Assert.Equal("first pending helper request", File.ReadAllText(request));
    }

    [Fact]
    public void PreparationFailureReleasesReservationAfterCleanup()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var reservation = SteamGameHandoff.Reserve(_directory);
            throw new InvalidOperationException("preparation failed");
        }));
        Assert.False(SteamGameHandoff.IsReserved(_directory));
        using var next = SteamGameHandoff.Reserve(_directory);
        Assert.True(SteamGameHandoff.IsReserved(_directory));
    }

    [Fact]
    public void ReservationRemainsExclusiveUntilCleanupIsComplete()
    {
        var reservation = SteamGameHandoff.Reserve(_directory);
        try
        {
            File.WriteAllText(Path.Combine(_directory, "status.json"), "exited");
            Assert.Throws<IOException>(() => SteamGameHandoff.Reserve(_directory));
            File.Delete(Path.Combine(_directory, "status.json"));
        }
        finally { reservation.Dispose(); }
        Assert.False(SteamGameHandoff.IsReserved(_directory));
        using var next = SteamGameHandoff.Reserve(_directory);
    }

    [Fact]
    public void SurvivingHelperRejectsReservationAndFailureDoesNotLeakLock()
    {
        Directory.CreateDirectory(_directory);
        using (var helper = new FileStream(Path.Combine(_directory, "game.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<InvalidOperationException>(() => SteamGameHandoff.Reserve(_directory));
        }
        using var next = SteamGameHandoff.Reserve(_directory);
    }

    [Fact]
    public async Task SecondLauncherStartupDoesNotCleanPendingSession()
    {
        using var pending = SteamGameHandoff.Reserve(_directory);
        Assert.True(await GameSessionSecretsService.WriteAsync(_directory,
            new GameSessionSecrets("pending-token", DateTimeOffset.UtcNow.AddHours(1), null, null)));
        var session = GameSessionSecretsService.GetPath(_directory)!;
        var original = File.ReadAllBytes(session);
        var cleaned = false;
        await SteamGameHandoff.RunStartupMaintenanceAsync(() =>
        {
            cleaned = true;
            GameSessionSecretsService.Delete(_directory);
            return Task.CompletedTask;
        }, _directory);
        Assert.False(cleaned);
        Assert.Equal(original, File.ReadAllBytes(session));
    }

    [Fact]
    public async Task StartupMaintenanceHoldsReservationAndReleasesOnFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SteamGameHandoff.RunStartupMaintenanceAsync(() =>
        {
            Assert.Throws<IOException>(() => SteamGameHandoff.Reserve(_directory));
            throw new InvalidOperationException("startup failed");
        }, _directory));
        Assert.False(SteamGameHandoff.IsReserved(_directory));
        using var next = SteamGameHandoff.Reserve(_directory);
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
