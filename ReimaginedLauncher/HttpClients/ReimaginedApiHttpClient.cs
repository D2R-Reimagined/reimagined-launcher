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

    /// <summary>Deadline for the small JSON calls, which used to inherit it from the client.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The package is served from object storage in a different region than
    /// the API, so one connection is limited by window size over a long round
    /// trip rather than by the player's bandwidth. Several ranged requests at
    /// once is what actually fills the pipe.
    /// </summary>
    private const int DownloadParallelism = 8;
    private const long DownloadChunkBytes = 8L * 1024 * 1024;
    private const int DownloadChunkAttempts = 4;
    private static readonly TimeSpan DownloadChunkTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;

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
        CancellationToken cancellationToken = default)
    {
        // The first request resolves where the bytes actually live. The API
        // redirects to object storage when it can, and the effective URL is
        // what the ranged requests below are aimed at.
        using var probe = await _httpClient.GetAsync(
            bundle.DownloadPath.TrimStart('/'),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        probe.EnsureSuccessStatusCode();

        var expectedLength = probe.Content.Headers.ContentLength ?? bundle.ArtifactSizeBytes;
        if (expectedLength <= 0 || expectedLength > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException("The ladder bundle has an invalid download size.");
        }

        var effectiveUrl = probe.RequestMessage?.RequestUri;
        var supportsRanges = effectiveUrl is not null
                             && probe.Headers.AcceptRanges.Contains("bytes")
                             && expectedLength > DownloadChunkBytes;
        var stopwatch = Stopwatch.StartNew();
        var payload = new byte[expectedLength];
        if (supportsRanges)
        {
            probe.Dispose();
            await DownloadInParallelAsync(effectiveUrl!, payload, progress, stopwatch, cancellationToken);
            return payload;
        }

        await using var input = await probe.Content.ReadAsStreamAsync(cancellationToken);
        var lastReportAt = TimeSpan.Zero;
        long lastReportBytes = 0;
        double smoothedBytesPerSecond = 0;
        long total = 0;
        while (total < expectedLength)
        {
            var read = await input.ReadAsync(payload.AsMemory((int)total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
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

        if (total != expectedLength)
        {
            throw new InvalidOperationException(
                $"The ladder bundle download ended after {total} of {expectedLength} bytes.");
        }

        ReportDownloadProgress(
            progress,
            total,
            expectedLength,
            stopwatch.Elapsed,
            lastReportAt,
            lastReportBytes,
            smoothedBytesPerSecond);

        return payload;
    }

    /// <summary>
    /// Fills <paramref name="payload"/> with several ranged requests in flight
    /// at once, each writing only into its own slice. A chunk that fails
    /// resumes from the bytes it already has, so a dropped connection costs
    /// the remainder of one chunk rather than the whole download.
    /// </summary>
    private async Task DownloadInParallelAsync(
        Uri source,
        byte[] payload,
        IProgress<LadderBundleDownloadProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var total = payload.LongLength;
        var chunks = new List<(long Start, long EndInclusive)>();
        for (var start = 0L; start < total; start += DownloadChunkBytes)
        {
            chunks.Add((start, Math.Min(total, start + DownloadChunkBytes) - 1));
        }

        long received = 0;
        var download = Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DownloadParallelism,
                CancellationToken = cancellationToken
            },
            async (chunk, token) =>
            {
                var offset = chunk.Start;
                var attempt = 0;
                while (offset <= chunk.EndInclusive)
                {
                    attempt++;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(DownloadChunkTimeout);
                        using var request = new HttpRequestMessage(HttpMethod.Get, source);
                        request.Headers.Range = new RangeHeaderValue(offset, chunk.EndInclusive);
                        using var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token);
                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                        while (offset <= chunk.EndInclusive)
                        {
                            var read = await stream.ReadAsync(
                                payload.AsMemory((int)offset, (int)(chunk.EndInclusive - offset + 1)),
                                timeout.Token);
                            if (read == 0)
                            {
                                throw new System.IO.EndOfStreamException(
                                    $"The ladder bundle download ended at byte {offset} of {total}.");
                            }

                            offset += read;
                            Interlocked.Add(ref received, read);
                        }
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException
                        && attempt < DownloadChunkAttempts
                        && !token.IsCancellationRequested)
                    {
                        LaunchDiagnostics.Log(
                            $"Ladder bundle chunk {chunk.Start}-{chunk.EndInclusive} stopped at {offset}: {exception.Message}. Retrying.");
                        await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                    }
                }
            });

        var lastReportAt = TimeSpan.Zero;
        long lastReportBytes = 0;
        double smoothedBytesPerSecond = 0;
        while (!download.IsCompleted)
        {
            await Task.WhenAny(download, Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken));
            var soFar = Interlocked.Read(ref received);
            var elapsed = stopwatch.Elapsed;
            smoothedBytesPerSecond = ReportDownloadProgress(
                progress,
                soFar,
                total,
                elapsed,
                lastReportAt,
                lastReportBytes,
                smoothedBytesPerSecond);
            lastReportAt = elapsed;
            lastReportBytes = soFar;
        }

        await download;
        ReportDownloadProgress(
            progress,
            total,
            total,
            stopwatch.Elapsed,
            lastReportAt,
            lastReportBytes,
            smoothedBytesPerSecond);
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
        using var response = await _httpClient.GetAsync(
            $"ladders/{ladderId}/optional-extensions/{extension.Id}/download",
            HttpCompletionOption.ResponseHeadersRead, token);
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
