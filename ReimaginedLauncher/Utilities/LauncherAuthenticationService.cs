using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

/// <summary>The token one game session's plugins use, and when it stops working.</summary>
/// <param name="IsUserTokenFallback">
/// True when the API had no game-token endpoint and this is the launcher's own
/// account token. It must never be sent to the game-token revoke endpoint.
/// </param>
public sealed record GameSessionToken(string AccessToken, DateTimeOffset ExpiresAtUtc, bool IsUserTokenFallback);

public sealed class LauncherAuthenticationService(ReimaginedApiHttpClient apiClient)
{
    private const string WebsiteBaseAddressEnvironmentVariable = "D2R_REIMAGINED_WEBSITE_BASE_URL";
#if DEBUG
    private static readonly Uri DefaultWebsiteBaseAddress = new("http://localhost:9500/");
#else
    private static readonly Uri DefaultWebsiteBaseAddress = new("https://www.d2r-reimagined.com/");
#endif
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private AppSettings? _settings;
    private LauncherTokenResponse? _session;
    private GameSessionToken? _activeGameToken;

    public event EventHandler? SessionChanged;

    internal Func<AppSettings, Task> SaveSettingsAsync { get; set; } = SettingsManager.SaveAsync;
    internal Action<Uri> BrowserOpener { get; set; } = OpenBrowser;

    public ReimaginedUserResponse? CurrentUser => _session?.User;
    public bool IsSignedIn => CurrentUser is not null;

    public void BindSettings(AppSettings settings)
    {
        _settings = settings;
        apiClient.AccessTokenProvider = GetAccessTokenAsync;
    }

    public async Task InitializeAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        BindSettings(settings);
        if (string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken))
        {
            return;
        }

        _ = await GetAccessTokenAsync(cancellationToken);
    }

    public async Task<ReimaginedUserResponse> SignInAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _settings
            ?? throw new InvalidOperationException("Launcher settings have not finished loading.");

        if (_session is { } currentSession
            && currentSession.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return currentSession.User;
        }

        if (!string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken))
        {
            try
            {
                _ = await GetAccessTokenAsync(cancellationToken);
                if (_session is { } restoredSession)
                {
                    return restoredSession.User;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LaunchDiagnostics.Log($"Could not restore cached Reimagined session before sign-in ({exception.Message}); continuing with browser sign-in.");
            }
        }

        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/oauth/callback";
            BrowserOpener(CreateAuthorizationUri(redirectUri, challenge, state));

            using var callbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callbackTimeout.CancelAfter(SignInTimeout);

            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(callbackTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Website sign-in timed out. Please try again.");
                }

                using (client)
                await using (var stream = client.GetStream())
                {
                    CallbackReadOutcome outcome;
                    try
                    {
                        outcome = await ReadCallbackAsync(stream, state, callbackTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("Website sign-in timed out. Please try again.");
                    }
                    catch (Exception)
                    {
                        await TryWriteBrowserResponseAsync(
                            stream,
                            "Sign-in failed",
                            "The launcher could not validate this sign-in response.");
                        throw;
                    }

                    if (outcome.Kind == CallbackOutcomeKind.IgnoreConnection)
                    {
                        continue;
                    }

                    if (outcome.Kind == CallbackOutcomeKind.NotFound)
                    {
                        await TryWriteNotFoundResponseAsync(stream);
                        continue;
                    }

                    var callback = outcome.Result!;
                    if (!string.IsNullOrEmpty(callback.Error))
                    {
                        await TryWriteBrowserResponseAsync(
                            stream,
                            "Sign-in cancelled",
                            "You can close this tab and return to the launcher.");
                        throw new InvalidOperationException("Website sign-in was cancelled.");
                    }

                    LauncherTokenResponse? session;
                    try
                    {
                        session = await apiClient.ExchangeLauncherCodeAsync(
                            callback.Code!,
                            verifier,
                            redirectUri,
                            cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        await TryWriteBrowserResponseAsync(
                            stream,
                            "Sign-in failed",
                            "The launcher could not reach the D2R Reimagined API to finish signing in. Return to the launcher and try again.");
                        if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("Timed out while exchanging the launcher sign-in code with the API. Please try again.", exception);
                        }

                        throw;
                    }

                    if (session is null)
                    {
                        await TryWriteBrowserResponseAsync(
                            stream,
                            "Sign-in expired",
                            "Return to the launcher and start sign-in again.");
                        throw new InvalidOperationException("The launcher authorization code was invalid or expired.");
                    }

                    await _sessionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await StoreSessionAsync(session);
                    }
                    finally
                    {
                        _sessionLock.Release();
                    }

                    await TryWriteBrowserResponseAsync(
                        stream,
                        "Launcher connected",
                        $"Signed in as {WebUtility.HtmlEncode(session.User.DisplayName)}. You can close this tab.");
                    return session.User;
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_session is { } current
            && current.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return current.AccessToken;
        }

        var settings = _settings;
        if (settings is null || string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken))
        {
            return null;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is { } currentAfterWait
                && currentAfterWait.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return currentAfterWait.AccessToken;
            }

            settings = _settings;
            if (settings is null || string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken))
            {
                return null;
            }

            var refreshed = await apiClient.RefreshLauncherSessionAsync(
                settings.D2RReimaginedRefreshToken,
                cancellationToken);
            if (refreshed is null)
            {
                var hadSession = _session is not null;
                _session = null;
                settings.D2RReimaginedRefreshToken = null;
                await SaveSettingsAsync(settings);
                if (hadSession)
                {
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                }

                return null;
            }

            await StoreSessionAsync(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Gets the one token a game launch hands to its plugins: a narrow,
    /// short-lived game token minted by the API, never the launcher's own
    /// account token - unless the API predates game tokens (404), in which case
    /// this falls back to the account token and its expiry and logs a warning,
    /// so the launcher and API can deploy in either order. Null when nobody is
    /// signed in or minting failed for any other reason; callers treat that
    /// exactly like being signed out.
    /// </summary>
    public async Task<GameSessionToken?> CreateGameTokenAsync(CancellationToken cancellationToken = default)
    {
        string? userToken;
        try
        {
            userToken = await GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LaunchDiagnostics.Log($"game token: could not validate the Reimagined API session ({exception.Message}).");
            return null;
        }

        if (string.IsNullOrWhiteSpace(userToken) || _session is not { } session)
        {
            return null;
        }

        var token = await ResolveGameTokenAsync(
            ct => apiClient.CreateGameTokenAsync(userToken, ct),
            userToken,
            session.ExpiresAtUtc,
            cancellationToken);
        if (token is { IsUserTokenFallback: false })
        {
            _activeGameToken = token;
        }

        return token;
    }

    /// <summary>
    /// The 404 fallback and failure handling for <see cref="CreateGameTokenAsync"/>,
    /// separated so it can be tested without a signed-in session.
    /// </summary>
    internal static async Task<GameSessionToken?> ResolveGameTokenAsync(
        Func<CancellationToken, Task<GameTokenResponse?>> mint,
        string userToken,
        DateTime userTokenExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var minted = await mint(cancellationToken);
            if (minted is null)
            {
                LaunchDiagnostics.Log(
                    "WARNING game token: the API has no auth/launcher/game-token endpoint (404). "
                    + "Falling back to the launcher's own account token for this game session.");
                return new GameSessionToken(userToken, AsUtc(userTokenExpiresAtUtc), IsUserTokenFallback: true);
            }

            return new GameSessionToken(minted.AccessToken, AsUtc(minted.ExpiresAtUtc), IsUserTokenFallback: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LaunchDiagnostics.Log($"game token: could not mint a game token ({exception.Message}); plugins get no account for this launch.");
            return null;
        }
    }

    /// <summary>
    /// Revokes a minted game token. A fallback account token is never sent to
    /// the revoke endpoint - it is the launcher's own session. Failures are
    /// logged, never thrown: the token expires on its own.
    /// </summary>
    public async Task RevokeGameTokenAsync(GameSessionToken? token, CancellationToken cancellationToken = default)
    {
        if (token is null || token.IsUserTokenFallback)
        {
            return;
        }

        Interlocked.CompareExchange(ref _activeGameToken, null, token);
        try
        {
            await apiClient.RevokeGameTokenAsync(token.AccessToken, cancellationToken);
            LaunchDiagnostics.Log("game token: revoked.");
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.Log($"game token: could not revoke ({exception.Message}); it will expire at {token.ExpiresAtUtc:u}.");
        }
    }

    private static DateTimeOffset AsUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
        };
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // The game token outlives nothing it should: signing out ends the
        // session the game was given too.
        await RevokeGameTokenAsync(Volatile.Read(ref _activeGameToken), cancellationToken);

        var settings = _settings;
        if (settings is null)
        {
            _session = null;
            SessionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var refreshToken = settings.D2RReimaginedRefreshToken;
            try
            {
                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    await apiClient.RevokeLauncherSessionAsync(refreshToken, cancellationToken);
                }
            }
            finally
            {
                _session = null;
                settings.D2RReimaginedRefreshToken = null;
                await SaveSettingsAsync(settings);
                SessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StoreSessionAsync(LauncherTokenResponse session)
    {
        _session = session;
        if (_settings is not null)
        {
            _settings.D2RReimaginedRefreshToken = session.RefreshToken;
            await SaveSettingsAsync(_settings);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Uri CreateAuthorizationUri(
        string redirectUri,
        string codeChallenge,
        string state)
    {
        var builder = new UriBuilder(new Uri(ResolveWebsiteBaseAddress(), "launcher/authorize"))
        {
            Query = $"redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
                + $"&state={Uri.EscapeDataString(state)}"
        };
        return builder.Uri;
    }

    internal static Uri ResolveWebsiteBaseAddress()
    {
        var configuredAddress = Environment.GetEnvironmentVariable(WebsiteBaseAddressEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            return DefaultWebsiteBaseAddress;
        }

        configuredAddress = configuredAddress.Trim().TrimEnd('/') + "/";
        if (Uri.TryCreate(configuredAddress, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            return address;
        }

        LaunchDiagnostics.Log(
            $"Ignoring invalid {WebsiteBaseAddressEnvironmentVariable} value and using {DefaultWebsiteBaseAddress}.");
        return DefaultWebsiteBaseAddress;
    }

    private static void OpenBrowser(Uri uri)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(uri.AbsoluteUri);
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo("open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(uri.AbsoluteUri);
        }
        else
        {
            startInfo = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
        }

        Process.Start(startInfo)?.Dispose();
    }

    private static async Task<CallbackReadOutcome> ReadCallbackAsync(
        NetworkStream stream,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        string? requestLine;
        try
        {
            requestLine = await reader.ReadLineAsync(cancellationToken);
        }
        catch (IOException)
        {
            return new CallbackReadOutcome(CallbackOutcomeKind.IgnoreConnection, null);
        }

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return new CallbackReadOutcome(CallbackOutcomeKind.IgnoreConnection, null);
        }

        if (requestLine.Length > 8192)
        {
            throw new InvalidOperationException("The launcher callback request was invalid.");
        }

        for (var headerCount = 0; headerCount < 100; headerCount++)
        {
            var header = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(header))
            {
                break;
            }
        }

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "GET")
        {
            throw new InvalidOperationException("The launcher callback request was invalid.");
        }

        var requestUri = new Uri(new Uri("http://127.0.0.1"), parts[1]);
        if (requestUri.AbsolutePath != "/oauth/callback")
        {
            return new CallbackReadOutcome(CallbackOutcomeKind.NotFound, null);
        }

        var query = ParseQuery(requestUri.Query);
        if (!query.TryGetValue("state", out var state) || !FixedTimeEquals(state, expectedState))
        {
            throw new InvalidOperationException("The launcher callback state did not match.");
        }

        query.TryGetValue("code", out var code);
        query.TryGetValue("error", out var error);
        if (string.IsNullOrWhiteSpace(code) == string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException("The launcher callback did not include a valid result.");
        }

        return new CallbackReadOutcome(CallbackOutcomeKind.Callback, new CallbackResult(code, error));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[WebUtility.UrlDecode(pair[0])] = pair.Length == 2
                ? WebUtility.UrlDecode(pair[1])
                : string.Empty;
        }

        return values;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static async Task TryWriteBrowserResponseAsync(
        NetworkStream stream,
        string title,
        string message)
    {
        try
        {
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var body = "<!doctype html><html><head><meta charset=\"utf-8\"><title>"
                + title
                + "</title><style>body{margin:0;background:#070707;color:#e9dfcf;font:16px system-ui;display:grid;place-items:center;min-height:100vh}main{max-width:36rem;padding:2rem;border:1px solid #7b2326;background:#140b0c;border-radius:.75rem}h1{color:#d7a84c}</style></head><body><main><h1>"
                + title
                + "</h1><p>"
                + message
                + "</p></main></body></html>";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                + $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(headers, writeTimeout.Token);
            await stream.WriteAsync(bodyBytes, writeTimeout.Token);
            await stream.FlushAsync(writeTimeout.Token);
            ShutdownSend(stream);
        }
        catch
        {
            // Best-effort browser response before closing the loopback socket.
        }
    }

    private static async Task TryWriteNotFoundResponseAsync(NetworkStream stream)
    {
        try
        {
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(headers, writeTimeout.Token);
            await stream.FlushAsync(writeTimeout.Token);
            ShutdownSend(stream);
        }
        catch
        {
            // Best-effort 404 response for non-callback browser probes (e.g. /favicon.ico).
        }
    }

    private static void ShutdownSend(NetworkStream stream)
    {
        try
        {
            stream.Socket.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // Ignore socket shutdown errors if the peer already closed.
        }
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private enum CallbackOutcomeKind
    {
        IgnoreConnection,
        NotFound,
        Callback
    }

    private sealed record CallbackReadOutcome(CallbackOutcomeKind Kind, CallbackResult? Result);
    private sealed record CallbackResult(string? Code, string? Error);
}
