using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public class GitHubDiscussionPluginsHttpClient
{
    private const string PluginsUrl = ProxyEndpoints.Plugins;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static IReadOnlyList<UserPluginEntry>? _cachedPlugins;
    private static DateTimeOffset _cacheTimestamp;

    // Maximum time we will voluntarily wait for a 429/503 Retry-After before
    // giving up; anything longer is reported as a failure to the caller.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public GitHubDiscussionPluginsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
    }

    public async Task<IReadOnlyList<UserPluginEntry>> GetUserPluginsAsync(bool forceRefresh = false)
    {
        // Serialize concurrent callers so only one fetch hits the proxy at a
        // time, and so a second caller can re-use a just-populated cache.
        await CacheLock.WaitAsync();
        try
        {
            if (!forceRefresh &&
                _cachedPlugins != null &&
                DateTimeOffset.UtcNow - _cacheTimestamp < CacheDuration)
            {
                return _cachedPlugins;
            }

            try
            {
                var plugins = await FetchPluginsAsync();
                _cachedPlugins = plugins;
                _cacheTimestamp = DateTimeOffset.UtcNow;
                return plugins;
            }
            catch
            {
                // On failure, fall back to any previously cached result so we
                // don't hammer the proxy on transient errors and so users keep
                // seeing the last known good list.
                return _cachedPlugins ?? [];
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }

    public async Task<string> DownloadZipToTempAsync(string zipUrl)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            "user-plugin-downloads",
            $"{Guid.NewGuid():N}.zip");

        var directory = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var response = await GetWithRateLimitAsync(zipUrl);
        try
        {
            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
        }
        finally
        {
            response.Dispose();
        }

        return tempPath;
    }

    private async Task<IReadOnlyList<UserPluginEntry>> FetchPluginsAsync()
    {
        using var response = await GetWithRateLimitAsync(PluginsUrl);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ProxyPlugin[]>() ?? [];
        var plugins = new List<UserPluginEntry>(payload.Length);
        foreach (var entry in payload)
        {
            if (string.IsNullOrWhiteSpace(entry.Title) ||
                string.IsNullOrWhiteSpace(entry.Description) ||
                string.IsNullOrWhiteSpace(entry.ModVersion) ||
                string.IsNullOrWhiteSpace(entry.ZipUrl) ||
                string.IsNullOrWhiteSpace(entry.DiscussionUrl))
            {
                continue;
            }

            plugins.Add(new UserPluginEntry
            {
                Title = entry.Title!.Trim(),
                Description = entry.Description!.Trim(),
                ModVersion = entry.ModVersion!.Trim(),
                ZipUrl = entry.ZipUrl!,
                DiscussionUrl = entry.DiscussionUrl!
            });
        }

        return plugins;
    }

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if ((int)response.StatusCode == 429 ||
                (response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                 response.Headers.RetryAfter != null))
            {
                // Honour Retry-After once, then fail.
                var delay = GetRetryAfterDelay(response);
                var statusCode = (int)response.StatusCode;
                response.Dispose();

                if (attempt == 0 && delay > TimeSpan.Zero && delay <= MaxRetryAfter)
                {
                    await Task.Delay(delay);
                    continue;
                }

                throw new HttpRequestException(
                    $"Proxy rate-limited request to {url} (status {statusCode}, Retry-After {delay.TotalSeconds:F0}s).");
            }

            return response;
        }

        // Unreachable: the loop always returns or throws.
        throw new HttpRequestException($"Failed to fetch {url} after retry.");
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return TimeSpan.FromSeconds(5);
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(5);
    }

    private sealed class ProxyPlugin
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("modVersion")]
        public string? ModVersion { get; set; }

        [JsonPropertyName("zipUrl")]
        public string? ZipUrl { get; set; }

        [JsonPropertyName("discussionUrl")]
        public string? DiscussionUrl { get; set; }
    }
}
