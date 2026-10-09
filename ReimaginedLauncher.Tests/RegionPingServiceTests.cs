using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class RegionPingServiceTests
{
    [Fact]
    public async Task ARegionTakesItsBestTargetsMedian()
    {
        var replies = new Dictionary<string, Queue<long?>>
        {
            ["10.0.0.1"] = new([40, 90, 50]),
            ["10.0.0.2"] = new([30, null, 34]),
            ["10.0.0.3"] = new([null, null, null])
        };
        var service = new RegionPingService((target, _, _) => Task.FromResult(replies[target].Dequeue()));
        LobbyRegionResponse[] regions =
        [
            new("na-east", "NA East", true, ["10.0.0.1", "10.0.0.2"]),
            new("eu-west", "EU West", true, ["10.0.0.3"])
        ];

        var pings = await service.MeasureAsync(regions);

        Assert.Equal(32d, pings["na-east"]);
        Assert.True(pings.ContainsKey("eu-west"));
        Assert.Null(pings["eu-west"]);
        Assert.Equal(pings, service.GetLastKnown(regions));
    }

    [Fact]
    public async Task ResultsAreReusedUntilTheCacheExpiresOrARetestIsForced()
    {
        var clock = new ManualTimeProvider();
        var echoes = 0;
        var service = new RegionPingService((_, _, _) =>
        {
            Interlocked.Increment(ref echoes);
            return Task.FromResult<long?>(20);
        }, clock);
        LobbyRegionResponse[] regions = [new("na-east", "NA East", true, ["10.0.0.1"])];

        await service.MeasureAsync(regions);
        await service.MeasureAsync(regions);
        Assert.Equal(RegionPingService.EchoesPerTarget, echoes);

        await service.MeasureAsync(regions, force: true);
        Assert.Equal(RegionPingService.EchoesPerTarget * 2, echoes);

        clock.Advance(RegionPingService.CacheLifetime + TimeSpan.FromSeconds(1));
        await service.MeasureAsync(regions);
        Assert.Equal(RegionPingService.EchoesPerTarget * 3, echoes);
    }

    [Fact]
    public void AnUnmeasuredRegionIsMissingFromTheLastKnownResults()
    {
        var service = new RegionPingService((_, _, _) => Task.FromResult<long?>(1));

        Assert.Empty(service.GetLastKnown([new("na-east", "NA East", true, ["10.0.0.1"])]));
    }

    [Fact]
    public void TheMedianNeedsAtLeastOneAnsweredEcho()
    {
        Assert.Null(RegionPingService.Median([]));
        Assert.Equal(7d, RegionPingService.Median([7]));
        Assert.Equal(15d, RegionPingService.Median([20, 10]));
        Assert.Equal(12d, RegionPingService.Median([90, 12, 11]));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
