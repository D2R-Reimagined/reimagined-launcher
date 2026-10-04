using System.Net;
using System.Text;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class GameTokenTests
{
    private static readonly DateTime UserExpiry = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MintingPostsWithTheUserTokenAndReadsTheGameToken()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            "{\"accessToken\":\"game-abc\",\"expiresAtUtc\":\"2026-10-04T14:00:00Z\"}");
        var client = new ReimaginedApiHttpClient(new HttpClient(handler));

        var minted = await client.CreateGameTokenAsync("user-token");

        Assert.NotNull(minted);
        Assert.Equal("game-abc", minted.AccessToken);
        Assert.Equal(new DateTime(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc), minted.ExpiresAtUtc.ToUniversalTime());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("/auth/launcher/game-token", handler.Uri!.AbsolutePath);
        Assert.Equal("Bearer user-token", handler.Authorization);
    }

    [Fact]
    public async Task AMissingEndpointIsReportedAsNullNotAnError()
    {
        var client = new ReimaginedApiHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.NotFound, "")));

        Assert.Null(await client.CreateGameTokenAsync("user-token"));
    }

    [Fact]
    public async Task OtherMintFailuresThrow()
    {
        var client = new ReimaginedApiHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.InternalServerError, "")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateGameTokenAsync("user-token"));
    }

    [Fact]
    public async Task RevokingAuthenticatesWithTheGameTokenItself()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, "");
        var client = new ReimaginedApiHttpClient(new HttpClient(handler));

        await client.RevokeGameTokenAsync("game-abc");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("/auth/launcher/game-token/revoke", handler.Uri!.AbsolutePath);
        Assert.Equal("Bearer game-abc", handler.Authorization);
    }

    [Fact]
    public async Task AMintedTokenIsUsedAsIs()
    {
        var token = await LauncherAuthenticationService.ResolveGameTokenAsync(
            _ => Task.FromResult<GameTokenResponse?>(
                new GameTokenResponse("game-abc", new DateTime(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc))),
            "user-token",
            UserExpiry,
            CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("game-abc", token.AccessToken);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 14, 0, 0, TimeSpan.Zero), token.ExpiresAtUtc);
        Assert.False(token.IsUserTokenFallback);
    }

    [Fact]
    public async Task AnApiWithoutGameTokensFallsBackToTheUserTokenAndItsExpiry()
    {
        var token = await LauncherAuthenticationService.ResolveGameTokenAsync(
            _ => Task.FromResult<GameTokenResponse?>(null),
            "user-token",
            UserExpiry,
            CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("user-token", token.AccessToken);
        Assert.Equal(new DateTimeOffset(UserExpiry), token.ExpiresAtUtc);
        Assert.True(token.IsUserTokenFallback);
    }

    [Fact]
    public async Task AnyOtherMintFailureMeansNoToken()
    {
        var token = await LauncherAuthenticationService.ResolveGameTokenAsync(
            _ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError),
            "user-token",
            UserExpiry,
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task TheFallbackEndToEndThroughTheHttpClient()
    {
        var client = new ReimaginedApiHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.NotFound, "")));

        var token = await LauncherAuthenticationService.ResolveGameTokenAsync(
            ct => client.CreateGameTokenAsync("user-token", ct),
            "user-token",
            UserExpiry,
            CancellationToken.None);

        Assert.Equal("user-token", token?.AccessToken);
        Assert.True(token?.IsUserTokenFallback);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
