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

    public ReimaginedUserResponse? CurrentUser => _session?.User;
    public bool IsSignedIn => CurrentUser is not null;

    public async Task InitializeAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(settings.D2RReimaginedRefreshToken))
        {
            return;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var refreshed = await apiClient.RefreshLauncherSessionAsync(
                settings.D2RReimaginedRefreshToken,
                cancellationToken);
            if (refreshed is null)
            {
                settings.D2RReimaginedRefreshToken = null;
                await SettingsManager.SaveAsync(settings);
                return;
            }

            await StoreSessionAsync(refreshed);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<ReimaginedUserResponse> SignInAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _settings
            ?? throw new InvalidOperationException("Launcher settings have not finished loading.");
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var redirectUri = $"http://127.0.0.1:{port}/oauth/callback";
                OpenBrowser(CreateAuthorizationUri(redirectUri, challenge, state));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SignInTimeout);
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();

                CallbackResult callback;
                try
                {
                    callback = await ReadCallbackAsync(stream, state, timeout.Token);
                }
                catch
                {
                    await WriteBrowserResponseAsync(
                        stream,
                        "Sign-in failed",
                        "The launcher could not validate this sign-in response.",
                        timeout.Token);
                    throw;
                }

                if (!string.IsNullOrEmpty(callback.Error))
                {
                    await WriteBrowserResponseAsync(
                        stream,
                        "Sign-in cancelled",
                        "You can close this tab and return to the launcher.",
                        timeout.Token);
                    throw new InvalidOperationException("Website sign-in was cancelled.");
                }

                var session = await apiClient.ExchangeLauncherCodeAsync(
                    callback.Code!,
                    verifier,
                    redirectUri,
                    timeout.Token);
                if (session is null)
                {
                    await WriteBrowserResponseAsync(
                        stream,
                        "Sign-in expired",
                        "Return to the launcher and start sign-in again.",
                        timeout.Token);
                    throw new InvalidOperationException("The launcher authorization code was invalid or expired.");
                }

                await StoreSessionAsync(session);
                await WriteBrowserResponseAsync(
                    stream,
                    "Launcher connected",
                    $"Signed in as {WebUtility.HtmlEncode(session.User.DisplayName)}. You can close this tab.",
                    timeout.Token);
                return session.User;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Website sign-in timed out. Please try again.");
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            _sessionLock.Release();
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
            var refreshed = await apiClient.RefreshLauncherSessionAsync(
                settings.D2RReimaginedRefreshToken,
                cancellationToken);
            if (refreshed is null)
            {
                _session = null;
                settings.D2RReimaginedRefreshToken = null;
                await SettingsManager.SaveAsync(settings);
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

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings;
        if (settings is null)
        {
            _session = null;
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
                await SettingsManager.SaveAsync(settings);
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
        if (_settings is null)
        {
            return;
        }

        _settings.D2RReimaginedRefreshToken = session.RefreshToken;
        await SettingsManager.SaveAsync(_settings);
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

    private static Uri ResolveWebsiteBaseAddress()
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

    private static async Task<CallbackResult> ReadCallbackAsync(
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
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 8192)
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
            throw new InvalidOperationException("The launcher callback path was invalid.");
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

        return new CallbackResult(code, error);
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

    private static async Task WriteBrowserResponseAsync(
        NetworkStream stream,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
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
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record CallbackResult(string? Code, string? Error);
}
