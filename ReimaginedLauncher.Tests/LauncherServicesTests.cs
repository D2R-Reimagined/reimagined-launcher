using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LauncherServicesTests
{
    [Fact]
    public async Task ViewsShareTheApiClientThatSignInAuthorizes()
    {
        var services = new ServiceCollection();
        Program.AddLauncherServices(services);
        using var provider = services.BuildServiceProvider();

        var authentication = provider.GetRequiredService<LauncherAuthenticationService>();
        authentication.SaveSettingsAsync = _ => Task.CompletedTask;
        await authentication.InitializeAsync(new AppSettings());

        // LaunchView resolves its own client; it must be the one sign-in wired up.
        var viewClient = provider.GetRequiredService<ReimaginedApiHttpClient>();
        Assert.NotNull(viewClient.AccessTokenProvider);
        Assert.Same(viewClient, provider.GetRequiredService<ReimaginedApiHttpClient>());
    }

    [Fact]
    public async Task ConcurrentInitializeAndGetAccessTokenRefreshSessionOnlyOnce()
    {
        var refreshCount = 0;
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new StubMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/auth/launcher/refresh", request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref refreshCount);
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            return CreateJsonResponse(CreateTokenResponse("access-1", "refresh-2", "DeckardCain"));
        }));

        var apiClient = new ReimaginedApiHttpClient(httpClient);
        var auth = new LauncherAuthenticationService(apiClient)
        {
            SaveSettingsAsync = _ => Task.CompletedTask
        };
        var settings = new AppSettings { D2RReimaginedRefreshToken = "refresh-1" };
        auth.BindSettings(settings);

        var initTask = auth.InitializeAsync(settings);
        var tokenTask = auth.GetAccessTokenAsync();
        releaseRefresh.SetResult();

        await Task.WhenAll(initTask, tokenTask);

        Assert.Equal(1, refreshCount);
        Assert.Equal("access-1", await tokenTask);
        Assert.Equal("DeckardCain", auth.CurrentUser?.DisplayName);
        Assert.Equal("refresh-2", settings.D2RReimaginedRefreshToken);
    }

    [Fact]
    public async Task SignInRestoresCachedRefreshTokenBeforeOpeningBrowser()
    {
        var browserOpened = false;
        using var httpClient = new HttpClient(new StubMessageHandler((request, _) =>
        {
            Assert.Equal("/auth/launcher/refresh", request.RequestUri?.AbsolutePath);
            return Task.FromResult(CreateJsonResponse(CreateTokenResponse("access-cached", "refresh-next", "Tyrael")));
        }));

        var apiClient = new ReimaginedApiHttpClient(httpClient);
        var auth = new LauncherAuthenticationService(apiClient)
        {
            SaveSettingsAsync = _ => Task.CompletedTask,
            BrowserOpener = _ => browserOpened = true
        };
        var settings = new AppSettings { D2RReimaginedRefreshToken = "refresh-cached" };
        auth.BindSettings(settings);

        var user = await auth.SignInAsync();

        Assert.False(browserOpened);
        Assert.Equal("Tyrael", user.DisplayName);
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignInHandlesBrowserPreconnectAndFaviconBeforeOAuthCallback()
    {
        using var httpClient = new HttpClient(new StubMessageHandler((request, _) =>
        {
            Assert.Equal("/auth/launcher/token", request.RequestUri?.AbsolutePath);
            return Task.FromResult(CreateJsonResponse(CreateTokenResponse("access-oauth", "refresh-oauth", "Akara")));
        }));

        var apiClient = new ReimaginedApiHttpClient(httpClient);
        Uri? capturedAuthorizeUri = null;
        var auth = new LauncherAuthenticationService(apiClient)
        {
            SaveSettingsAsync = _ => Task.CompletedTask,
            BrowserOpener = uri =>
            {
                capturedAuthorizeUri = uri;
                _ = Task.Run(() => SimulateBrowserCallbackWithPreconnectAndFaviconAsync(uri));
            }
        };
        auth.BindSettings(new AppSettings());

        var user = await auth.SignInAsync();

        Assert.NotNull(capturedAuthorizeUri);
        Assert.Equal("Akara", user.DisplayName);
        Assert.Equal("Akara", auth.CurrentUser?.DisplayName);
    }

    [Fact]
    public async Task SignInWritesErrorHtmlToCallbackSocketWhenTokenExchangeFails()
    {
        using var httpClient = new HttpClient(new StubMessageHandler((_, _) =>
            throw new HttpRequestException("Simulated connection failure to API")));

        var apiClient = new ReimaginedApiHttpClient(httpClient);
        var callbackResponseTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new LauncherAuthenticationService(apiClient)
        {
            SaveSettingsAsync = _ => Task.CompletedTask,
            BrowserOpener = uri =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var html = await SendOAuthCallbackAsync(uri);
                        callbackResponseTask.TrySetResult(html);
                    }
                    catch (Exception ex)
                    {
                        callbackResponseTask.TrySetException(ex);
                    }
                });
            }
        };
        auth.BindSettings(new AppSettings());

        await Assert.ThrowsAsync<HttpRequestException>(() => auth.SignInAsync());
        var browserResponse = await callbackResponseTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("HTTP/1.1 200 OK", browserResponse);
        Assert.Contains("Sign-in failed", browserResponse);
    }

    private static async Task SimulateBrowserCallbackWithPreconnectAndFaviconAsync(Uri authorizeUri)
    {
        var query = ParseQueryString(authorizeUri.Query);
        var redirectUri = new Uri(query["redirect_uri"]);
        var state = query["state"];

        // 1. Browser speculative preconnect that closes without sending bytes.
        using (var preconnect = new TcpClient())
        {
            await preconnect.ConnectAsync(IPAddress.Loopback, redirectUri.Port);
        }

        // 2. Browser request for /favicon.ico before or alongside callback.
        using (var faviconClient = new TcpClient())
        {
            await faviconClient.ConnectAsync(IPAddress.Loopback, redirectUri.Port);
            await using var stream = faviconClient.GetStream();
            var req = Encoding.ASCII.GetBytes("GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req);
            await stream.FlushAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var faviconResp = await reader.ReadToEndAsync();
            Assert.Contains("404 Not Found", faviconResp);
        }

        // 3. Actual /oauth/callback request.
        var callbackResponse = await SendOAuthCallbackAsync(authorizeUri);
        Assert.Contains("Launcher connected", callbackResponse);
    }

    private static async Task<string> SendOAuthCallbackAsync(Uri authorizeUri)
    {
        var query = ParseQueryString(authorizeUri.Query);
        var redirectUri = new Uri(query["redirect_uri"]);
        var state = query["state"];

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirectUri.Port);
        await using var stream = client.GetStream();
        var requestText = $"GET /oauth/callback?code=test-code&state={Uri.EscapeDataString(state)} HTTP/1.1\r\nHost: 127.0.0.1:{redirectUri.Port}\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(requestText);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            dict[WebUtility.UrlDecode(kv[0])] = kv.Length == 2 ? WebUtility.UrlDecode(kv[1]) : string.Empty;
        }

        return dict;
    }

    private static LauncherTokenResponse CreateTokenResponse(string accessToken, string refreshToken, string displayName)
    {
        return new LauncherTokenResponse(
            accessToken,
            DateTime.UtcNow.AddHours(1),
            refreshToken,
            DateTime.UtcNow.AddDays(30),
            new ReimaginedUserResponse(
                Guid.NewGuid(),
                "user@example.com",
                displayName,
                null,
                null,
                null,
                ["Player"],
                DateTime.UtcNow));
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
