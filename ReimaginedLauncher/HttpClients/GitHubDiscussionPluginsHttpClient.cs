using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients;

public class GitHubDiscussionPluginsHttpClient
{
    private const string PluginsUrl = ProxyEndpoints.Plugins;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static IReadOnlyList<UserPluginEntry>? _cachedPlugins;
    private static DateTimeOffset _cacheTimestamp;

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
            catch (Exception ex)
            {
                // On failure, fall back to any previously cached result so we
                // don't hammer the proxy on transient errors and so users keep
                // seeing the last known good list.
                LaunchDiagnostics.LogException("Failed to fetch user plugins from proxy", ex);
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

        var response = await ProxyHttpHelper.GetWithRateLimitAsync(_httpClient, zipUrl);
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
        using var response = await ProxyHttpHelper.GetWithRateLimitAsync(_httpClient, PluginsUrl);
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
                DiscussionUrl = entry.DiscussionUrl!,
                PublishedAt = entry.PublishedAt,
                UpdatedAt = entry.UpdatedAt,
                LastActivityAt = entry.LastActivityAt
            });
        }

        return plugins;
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

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("lastActivityAt")]
        public DateTimeOffset? LastActivityAt { get; set; }
    }
}
