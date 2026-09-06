using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using ReimaginedLauncher.HttpClients;

namespace ReimaginedLauncher.Tests;

[Collection("Ladder schedule HTTP")]
public sealed class LadderLaunchScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 18, 0, 0, TimeSpan.Zero);
    private static LadderResponse Ladder(DateTimeOffset start, DateTimeOffset? end = null) =>
        new(Guid.NewGuid(), "Scheduled ladder", start, end ?? start.AddDays(30), [], null);

    [Fact]
    public void DiscoveryOpensExactlyOneHourBeforeStartAndExcludesEndedLadders()
    {
        Assert.True(LadderLaunchSchedule.IsAvailable(Ladder(Now.AddHours(1)), Now));
        Assert.False(LadderLaunchSchedule.IsAvailable(Ladder(Now.AddHours(1).AddTicks(1)), Now));
        Assert.False(LadderLaunchSchedule.IsAvailable(Ladder(Now.AddDays(-1), Now), Now));
        Assert.True(LadderLaunchSchedule.IsAvailable(Ladder(Now.AddMinutes(-1)), Now));
    }

    [Fact]
    public void CountdownExpirationCannotAuthorizePlayWithoutServerConfirmation()
    {
        var ladder = Ladder(Now.AddSeconds(-1));
        Assert.False(new LadderLaunchSchedule([ladder], [], Now).IsLive(ladder));
        Assert.True(new LadderLaunchSchedule([ladder], [ladder], Now).IsLive(ladder));
    }

    [Fact]
    public void ConfirmationForAnotherLadderOrChangedScheduleCannotAuthorizePlay()
    {
        var ladder = Ladder(Now.AddMinutes(-1));
        Assert.False(new LadderLaunchSchedule([ladder], [Ladder(ladder.StartDateUtc)], Now).IsLive(ladder));
        Assert.False(new LadderLaunchSchedule([ladder], [ladder with { StartDateUtc = Now.AddHours(1) }], Now).IsLive(ladder));
    }

    [Fact]
    public void FutureOrEndedLadderRemainsBlockedEvenIfInActiveResponse()
    {
        var future = Ladder(Now.AddMinutes(1));
        var ended = Ladder(Now.AddDays(-1), Now);
        Assert.False(new LadderLaunchSchedule([future], [future], Now).IsLive(future));
        Assert.False(new LadderLaunchSchedule([ended], [ended], Now).IsLive(ended));
    }

    [Fact]
    public void CountdownRoundsUpAndWaitsForVerificationAtZero()
    {
        Assert.Equal("Starts in 01:00:00", LadderLaunchSchedule.Countdown(Now.AddHours(1), Now));
        Assert.Equal("Starts in 00:00:01", LadderLaunchSchedule.Countdown(Now.AddMilliseconds(1), Now));
        Assert.Equal("Checking live status...", LadderLaunchSchedule.Countdown(Now, Now));
        Assert.Equal("Checking live status...", LadderLaunchSchedule.Countdown(Now.AddSeconds(-1), Now));
    }

    [Fact]
    public async Task DiscoveryUsesServerTimeAndRequiresSeparateLiveConfirmation()
    {
        var ladder = Ladder(Now.AddMinutes(30));
        using var handler = new ScheduleHandler(ladder);
        using var http = new HttpClient(handler);
        var client = new ReimaginedApiHttpClient(http);
        var schedule = await client.GetLadderLaunchScheduleAsync();
        Assert.Single(schedule.Available);
        Assert.InRange((schedule.Now - Now).TotalSeconds, 0, 5);
        Assert.False(schedule.IsLive(ladder));
        Assert.Equal(new[] { "/ladders", "/ladders/active" }, handler.Paths);
    }

    [Fact]
    public async Task FailedLiveCheckDoesNotReturnAnAuthorizedSchedule()
    {
        using var handler = new ScheduleHandler(Ladder(Now.AddMinutes(-1)), true);
        using var http = new HttpClient(handler);
        var client = new ReimaginedApiHttpClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLadderLaunchScheduleAsync());
    }

    private sealed class ScheduleHandler(LadderResponse ladder, bool failLive = false) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.True(request.Headers.CacheControl?.NoCache);
            var response = new HttpResponseMessage(failLive && path == "/ladders/active"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(path == "/ladders" ? new[] { ladder } : Array.Empty<LadderResponse>())
            };
            response.Headers.Date = Now;
            return Task.FromResult(response);
        }
    }
}

[CollectionDefinition("Ladder schedule HTTP", DisableParallelization = true)]
public sealed class LadderScheduleHttpCollection;
