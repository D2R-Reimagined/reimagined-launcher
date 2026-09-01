using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients;

public sealed record LadderBundleDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    double Percentage,
    double BytesPerSecond,
    TimeSpan? EstimatedTimeRemaining);

public sealed class ReimaginedApiHttpClient
{
    private const string ApiBaseAddressEnvironmentVariable = "D2R_REIMAGINED_API_BASE_URL";
#if DEBUG
    private static readonly Uri DefaultApiBaseAddress = new("http://localhost:5000/");
#else
    private static readonly Uri DefaultApiBaseAddress = new("https://api.d2r-reimagined.com/");
#endif
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _httpClient;

    public ReimaginedApiHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = ResolveApiBaseAddress();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
        LaunchDiagnostics.Log($"Reimagined API base address: {_httpClient.BaseAddress}");
    }

    /// <summary>The resolved API origin, for components that talk to it outside this client.</summary>
    public Uri BaseAddress => _httpClient.BaseAddress!;

    public async Task<IReadOnlyList<LadderResponse>> GetActiveLaddersAsync(
        CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<List<LadderResponse>>(
            "ladders/active",
            JsonOptions,
            cancellationToken) ?? [];
    }

    public async Task<byte[]> DownloadLadderBundleAsync(
        LadderBundleResponse bundle,
        IProgress<LadderBundleDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            bundle.DownloadPath.TrimStart('/'),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength ?? bundle.ArtifactSizeBytes;
        if (expectedLength <= 0 || expectedLength > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException("The ladder bundle has an invalid download size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new System.IO.MemoryStream((int)expectedLength);
        var buffer = new byte[81920];
        var stopwatch = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        long lastReportBytes = 0;
        double smoothedBytesPerSecond = 0;
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > 512L * 1024 * 1024)
            {
                throw new InvalidOperationException("The ladder bundle exceeded the 512 MiB download limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReportAt >= TimeSpan.FromMilliseconds(250))
            {
                smoothedBytesPerSecond = ReportDownloadProgress(
                    progress,
                    total,
                    expectedLength,
                    elapsed,
                    lastReportAt,
                    lastReportBytes,
                    smoothedBytesPerSecond);
                lastReportAt = elapsed;
                lastReportBytes = total;
            }
        }

        ReportDownloadProgress(
            progress,
            total,
            expectedLength,
            stopwatch.Elapsed,
            lastReportAt,
            lastReportBytes,
            smoothedBytesPerSecond);

        return output.ToArray();
    }

    private static double ReportDownloadProgress(
        IProgress<LadderBundleDownloadProgress>? progress,
        long bytesReceived,
        long totalBytes,
        TimeSpan elapsed,
        TimeSpan previousReportAt,
        long previousReportBytes,
        double previousBytesPerSecond)
    {
        var sampleSeconds = (elapsed - previousReportAt).TotalSeconds;
        var sampleBytesPerSecond = sampleSeconds > 0 && bytesReceived > previousReportBytes
            ? (bytesReceived - previousReportBytes) / sampleSeconds
            : 0;
        var bytesPerSecond = sampleBytesPerSecond <= 0
            ? previousBytesPerSecond
            : previousBytesPerSecond > 0
                ? (previousBytesPerSecond * 0.7) + (sampleBytesPerSecond * 0.3)
                : sampleBytesPerSecond;
        var percentage = Math.Clamp(bytesReceived * 100d / totalBytes, 0, 100);
        TimeSpan? remaining = bytesPerSecond > 0 && bytesReceived < totalBytes
            ? TimeSpan.FromSeconds((totalBytes - bytesReceived) / bytesPerSecond)
            : null;

        progress?.Report(new LadderBundleDownloadProgress(
            bytesReceived,
            totalBytes,
            percentage,
            bytesPerSecond,
            remaining));
        return bytesPerSecond;
    }

    public async Task<LadderLaunchTicketResponse> CreateLadderLaunchTicketAsync(
        Guid ladderId,
        LadderBundleResponse bundle,
        string launcherVersion,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"ladders/{ladderId}/launch-ticket")
        {
            Content = JsonContent.Create(new
            {
                bundleId = bundle.Id,
                bundleRevision = bundle.Revision,
                launcherVersion,
                artifactSha256 = bundle.ArtifactSha256
            }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LadderLaunchTicketResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("The API returned an empty ladder launch ticket response.");
    }

    public async Task<LauncherTokenResponse?> ExchangeLauncherCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/token",
            new { code, codeVerifier, redirectUri },
            JsonOptions,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LauncherTokenResponse>(
            JsonOptions,
            cancellationToken);
    }

    public async Task<LauncherTokenResponse?> RefreshLauncherSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/refresh",
            new { refreshToken },
            JsonOptions,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LauncherTokenResponse>(
            JsonOptions,
            cancellationToken);
    }

    public async Task RevokeLauncherSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/revoke",
            new { refreshToken },
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static Uri ResolveApiBaseAddress()
    {
        var configuredAddress = Environment.GetEnvironmentVariable(ApiBaseAddressEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            return DefaultApiBaseAddress;
        }

        configuredAddress = configuredAddress.Trim().TrimEnd('/') + "/";
        if (Uri.TryCreate(configuredAddress, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            return address;
        }

        LaunchDiagnostics.Log(
            $"Ignoring invalid {ApiBaseAddressEnvironmentVariable} value and using {DefaultApiBaseAddress}.");
        return DefaultApiBaseAddress;
    }
}
