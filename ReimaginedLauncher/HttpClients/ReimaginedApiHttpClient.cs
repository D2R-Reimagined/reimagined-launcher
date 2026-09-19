using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>Deadline for the small JSON calls, which used to inherit it from the client.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);
    private readonly Dictionary<Guid, (string Version, LadderResponse Policy)> _schedulePolicies = [];

    public ReimaginedApiHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = ResolveApiBaseAddress();
        // A client-wide timeout also caps how long a response body may take to
        // read, which a package download cannot live with. Every short call
        // below sets its own deadline instead.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
        LaunchDiagnostics.Log($"Reimagined API base address: {_httpClient.BaseAddress}");
    }

    /// <summary>The resolved API origin, for components that talk to it outside this client.</summary>
    public Uri BaseAddress => _httpClient.BaseAddress!;
    public Func<CancellationToken, Task<string?>>? AccessTokenProvider { get; set; }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken token)
    {
        var accessToken = AccessTokenProvider is null ? null : await AccessTokenProvider(token);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public async Task<LadderLaunchSchedule> GetLadderLaunchScheduleAsync(
        CancellationToken cancellationToken = default)
    {
        await _scheduleGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, "ladders/schedule");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            await AuthorizeAsync(request, timeout.Token);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var schedule = await response.Content.ReadFromJsonAsync<LadderScheduleResponse>(JsonOptions, timeout.Token)
                ?? throw new InvalidDataException("The ladder schedule response was empty.");
            var receivedAt = Stopwatch.GetTimestamp();
            var available = schedule.Ladders.Where(entry => entry.EndDateUtc > schedule.ServerTimeUtc
                && entry.StartDateUtc <= schedule.ServerTimeUtc.AddHours(1)).ToArray();
            foreach (var id in _schedulePolicies.Keys.Where(id => !available.Any(entry => entry.Id == id)).ToArray())
                _schedulePolicies.Remove(id);
            var ladders = new List<LadderResponse>();
            var live = new List<LadderResponse>();
            foreach (var entry in available)
            {
                if (!_schedulePolicies.TryGetValue(entry.Id, out var cached) || cached.Version != entry.PolicyVersion)
                {
                    using var policyRequest = new HttpRequestMessage(HttpMethod.Get, $"ladders/{entry.Id}/client-policy");
                    policyRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                    await AuthorizeAsync(policyRequest, timeout.Token);
                    using var policyResponse = await _httpClient.SendAsync(policyRequest, timeout.Token);
                    policyResponse.EnsureSuccessStatusCode();
                    if (!policyResponse.Headers.TryGetValues("X-Ladder-Policy-Version", out var versions)
                        || versions.SingleOrDefault() != entry.PolicyVersion)
                        throw new InvalidDataException("The ladder policy changed during refresh. Refresh the ladder and try again.");
                    var policy = await policyResponse.Content.ReadFromJsonAsync<LadderResponse>(JsonOptions, timeout.Token)
                        ?? throw new InvalidDataException("The ladder policy response was empty.");
                    if (policy.Id != entry.Id || policy.StartDateUtc != entry.StartDateUtc || policy.EndDateUtc != entry.EndDateUtc)
                        throw new InvalidDataException("The ladder policy does not match its schedule.");
                    cached = (entry.PolicyVersion, policy);
                    _schedulePolicies[entry.Id] = cached;
                }
                ladders.Add(cached.Policy);
                if (entry.IsLive) live.Add(cached.Policy);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new LadderLaunchSchedule(ladders, live, schedule.ServerTimeUtc + Stopwatch.GetElapsedTime(receivedAt));
        }
        finally { _scheduleGate.Release(); }
    }

    public async Task<IReadOnlyList<LadderResponse>> GetActiveLaddersAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CreateRequestTimeout(cancellationToken);
        return await _httpClient.GetFromJsonAsync<List<LadderResponse>>(
            "ladders/active",
            JsonOptions,
            timeout.Token) ?? [];
    }

    /// <summary>
    /// Per-call deadline for the short JSON requests. The client itself no
    /// longer sets one, because a client-wide timeout also applies while a
    /// package download is reading its response body.
    /// </summary>
    private static CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }

    public async Task<byte[]> DownloadLadderBundleAsync(
        LadderBundleResponse bundle,
        IProgress<LadderBundleDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? cachePath = null)
    {
        var temporary = cachePath is null;
        cachePath ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reimagined-downloads", Guid.NewGuid().ToString("N"), "bundle.zip");
        try
        {
            var accessToken = AccessTokenProvider is null ? null : await AccessTokenProvider(cancellationToken);
            return await new LadderBundleDownloader(_httpClient, accessToken).DownloadAsync(bundle.DownloadPath,
                bundle.ArtifactSizeBytes, bundle.ArtifactSha256, cachePath, progress, cancellationToken);
        }
        finally
        {
            if (temporary)
            {
                var directory = System.IO.Path.GetDirectoryName(cachePath)!;
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    public async Task<byte[]> DownloadOptionalExtensionAsync(
        Guid ladderId,
        LadderAllowedExtensionResponse extension,
        IProgress<LadderBundleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!LadderOptionalExtensionService.CanDownload(extension))
            throw new InvalidOperationException("This optional file is not available for download.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"ladders/{ladderId}/optional-extensions/{extension.Id}/download");
        await AuthorizeAsync(request, token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != extension.SizeBytes)
            throw new System.IO.InvalidDataException("The optional file changed on the server. Refresh the ladder and try again.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new System.IO.MemoryStream();
        var buffer = new byte[81920];
        var watch = Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > extension.SizeBytes)
                throw new System.IO.InvalidDataException("The optional file exceeded its approved size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            var percent = output.Length * 100d / extension.SizeBytes!.Value;
            progress?.Report(new LadderBundleProgress($"Downloading {extension.FileName}...", percent,
                $"{output.Length / 1048576d:F1} / {extension.SizeBytes.Value / 1048576d:F1} MiB | {percent:F0}% | {output.Length / Math.Max(watch.Elapsed.TotalSeconds, .001) / 1048576d:F1} MiB/s"));
        }
        var bytes = output.ToArray();
        LadderOptionalExtensionService.VerifyDownload(extension, bytes);
        return bytes;
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
        using var timeout = CreateRequestTimeout(cancellationToken);
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LadderLaunchTicketResponse>(JsonOptions, timeout.Token)
               ?? throw new InvalidOperationException("The API returned an empty ladder launch ticket response.");
    }

    public async Task<LauncherTokenResponse?> ExchangeLauncherCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CreateRequestTimeout(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/token",
            new { code, codeVerifier, redirectUri },
            JsonOptions,
            timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LauncherTokenResponse>(
            JsonOptions,
            timeout.Token);
    }

    public async Task<LauncherTokenResponse?> RefreshLauncherSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CreateRequestTimeout(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/refresh",
            new { refreshToken },
            JsonOptions,
            timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LauncherTokenResponse>(
            JsonOptions,
            timeout.Token);
    }

    public async Task RevokeLauncherSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CreateRequestTimeout(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/launcher/revoke",
            new { refreshToken },
            JsonOptions,
            timeout.Token);
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
