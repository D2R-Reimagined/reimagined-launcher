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
    public async Task DiscoveryUsesServerTimeAndRequiresLiveConfirmation()
    {
        var ladder = Ladder(Now.AddMinutes(30));
        using var handler = new ScheduleHandler(ladder);
        using var http = new HttpClient(handler);
        var client = new ReimaginedApiHttpClient(http);
        var schedule = await client.GetLadderLaunchScheduleAsync();
        Assert.Single(schedule.Available);
        Assert.InRange((schedule.Now - Now).TotalSeconds, 0, 5);
        Assert.False(schedule.IsLive(ladder));
        Assert.Equal(new[] { "/ladders/schedule", $"/ladders/{ladder.Id}/client-policy" }, handler.Paths);
    }

    [Fact]
    public async Task FailedLiveCheckDoesNotReturnAnAuthorizedSchedule()
    {
        using var handler = new ScheduleHandler(Ladder(Now.AddMinutes(-1))) { FailSchedule = true };
        using var http = new HttpClient(handler);
        var client = new ReimaginedApiHttpClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLadderLaunchScheduleAsync());
    }

    [Fact]
    public async Task UnchangedSchedulesReusePolicyButAlwaysRefreshLiveConfirmation()
    {
        var ladder = Ladder(Now.AddMinutes(-1));
        using var handler = new ScheduleHandler(ladder) { Live = true };
        var client = new ReimaginedApiHttpClient(new HttpClient(handler));
        Assert.True((await client.GetLadderLaunchScheduleAsync()).IsLive(ladder));
        handler.Live = false;
        Assert.False((await client.GetLadderLaunchScheduleAsync()).IsLive(ladder));
        Assert.Equal(1, handler.Paths.Count(path => path.EndsWith("/client-policy")));
        handler.Version = "changed-optional-or-bundle";
        await client.GetLadderLaunchScheduleAsync();
        Assert.Equal(2, handler.Paths.Count(path => path.EndsWith("/client-policy")));
        handler.FailSchedule = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLadderLaunchScheduleAsync());
    }

    [Fact]
    public async Task PolicyReplacementRaceAndCancellationNeverReturnAnAuthorizedSchedule()
    {
        using var handler = new ScheduleHandler(Ladder(Now.AddMinutes(-1))) { Live = true, MismatchedVersion = true };
        var client = new ReimaginedApiHttpClient(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLadderLaunchScheduleAsync());
        handler.MismatchedVersion = false;
        Assert.Single((await client.GetLadderLaunchScheduleAsync()).Available);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLadderLaunchScheduleAsync(new CancellationToken(true)));
        handler.Empty = true;
        Assert.Empty((await client.GetLadderLaunchScheduleAsync()).Available);
        handler.Empty = false;
        await client.GetLadderLaunchScheduleAsync();
        Assert.Equal(3, handler.Paths.Count(path => path.EndsWith("/client-policy")));
    }

    [Fact]
    public async Task DistantFutureLaddersDoNotFetchManifests()
    {
        using var handler = new ScheduleHandler(Ladder(Now.AddDays(2)));
        var client = new ReimaginedApiHttpClient(new HttpClient(handler));
        Assert.Empty((await client.GetLadderLaunchScheduleAsync()).Available);
        Assert.Equal(new[] { "/ladders/schedule" }, handler.Paths);
    }

    [Theory]
    [InlineData(false, false, 0, 300)]
    [InlineData(true, true, 0, 30)]
    [InlineData(true, false, 10, 10)]
    [InlineData(true, false, -1, 30)]
    public void PollingCadencePreservesStartBoundaryAndSlowsIdleModes(bool mode, bool failed, int startSeconds, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), LadderLaunchSchedule.RefreshInterval(mode, failed, Now.AddSeconds(startSeconds), Now));

    [Fact]
    public void NoAvailableLadderPollsEveryFiveMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), LadderLaunchSchedule.RefreshInterval(true, false, null, Now));

    private sealed class ScheduleHandler(LadderResponse ladder) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool Live, FailSchedule, MismatchedVersion, Empty;
        public string Version = "version-1";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.True(request.Headers.CacheControl?.NoCache);
            var response = new HttpResponseMessage(FailSchedule && path == "/ladders/schedule"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = path == "/ladders/schedule"
                    ? JsonContent.Create(new LadderScheduleResponse(Now, Empty ? [] :
                        [new(ladder.Id, ladder.Name, ladder.StartDateUtc, ladder.EndDateUtc, Version, Live)]))
                    : JsonContent.Create(ladder)
            };
            response.Headers.Add("X-Ladder-Policy-Version", MismatchedVersion ? "changed" : Version);
            response.Headers.Date = Now;
            return Task.FromResult(response);
        }
    }
}

[CollectionDefinition("Ladder schedule HTTP", DisableParallelization = true)]
public sealed class LadderScheduleHttpCollection;
