using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public class GitHubDiscussionPluginsHttpClient
{
    private const string BaseUrl = "https://github.com";
    private const string PluginsDiscussionsUrl =
        $"{BaseUrl}/D2R-Reimagined/reimagined-launcher/discussions/categories/plugins";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static IReadOnlyList<UserPluginEntry>? _cachedPlugins;
    private static DateTimeOffset _cacheTimestamp;

    private static readonly Regex DiscussionLinkRegex = new(
        @"<a[^>]+href=""(?<href>/D2R-Reimagined/reimagined-launcher/discussions/(?<number>\d+))""[^>]*class=""[^""]*markdown-title[^""]*""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex BodyRegex = new(
        @"<td\s+class=""[^""]*comment-body markdown-body js-comment-body[^""]*""[^>]*>(?<body>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ZipLinkRegex = new(
        @"<a[^>]+href=""(?<url>[^""]+\.zip)""[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitleFieldRegex = new(
        @"Title:\s*(?<value>.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DescriptionFieldRegex = new(
        @"(?:Desc|Description):\s*(?<value>.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Accepts SemVer-ish versions: 2-4 numeric segments with an optional
    // pre-release (-beta, -rc.1) or build metadata (+abc) suffix.
    // Examples matched: 1.0, 1.2.3, 1.2.3.4, 1.2.3-beta, 1.2.3-rc.1+build5.
    private static readonly Regex ModVersionFieldRegex = new(
        @"(?:Mod|ModVer|ModVersion):\s*(?<value>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;

    public GitHubDiscussionPluginsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
    }

    public async Task<IReadOnlyList<UserPluginEntry>> GetUserPluginsAsync(bool forceRefresh = false)
    {
        // Serialize concurrent callers so only one fetch hits GitHub at a time,
        // and so a second caller can re-use a just-populated cache.
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
                var html = await GetStringWithRateLimitAsync(PluginsDiscussionsUrl);
                var plugins = new List<UserPluginEntry>();
                var seenNumbers = new HashSet<int>();

                foreach (Match match in DiscussionLinkRegex.Matches(html))
                {
                    if (!int.TryParse(match.Groups["number"].Value, out var number) ||
                        !seenNumbers.Add(number))
                    {
                        continue;
                    }

                    var discussionUrl = $"{BaseUrl}{match.Groups["href"].Value}";
                    var entry = await ParseDiscussionAsync(discussionUrl);
                    if (entry != null)
                    {
                        plugins.Add(entry);
                    }
                }

                _cachedPlugins = plugins;
                _cacheTimestamp = DateTimeOffset.UtcNow;
                return plugins;
            }
            catch
            {
                // On failure, fall back to any previously cached result to avoid
                // repeatedly hammering GitHub on transient errors.
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

    private async Task<UserPluginEntry?> ParseDiscussionAsync(string discussionUrl)
    {
        try
        {
            var html = await GetStringWithRateLimitAsync(discussionUrl);

            var bodyMatch = BodyRegex.Match(html);
            if (!bodyMatch.Success)
            {
                return null;
            }

            var bodyHtml = bodyMatch.Groups["body"].Value;
            var bodyText = ConvertHtmlToText(bodyHtml);

            var titleMatch = TitleFieldRegex.Match(bodyText);
            if (!titleMatch.Success)
            {
                return null;
            }

            var descriptionMatch = DescriptionFieldRegex.Match(bodyText);
            if (!descriptionMatch.Success)
            {
                return null;
            }

            var modVersionMatch = ModVersionFieldRegex.Match(bodyText);
            if (!modVersionMatch.Success)
            {
                return null;
            }

            var zipMatch = ZipLinkRegex.Match(bodyHtml);
            if (!zipMatch.Success)
            {
                return null;
            }

            var zipUrl = WebUtility.HtmlDecode(zipMatch.Groups["url"].Value);
            if (!zipUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = $"{BaseUrl}{zipUrl}";
            }

            return new UserPluginEntry
            {
                Title = titleMatch.Groups["value"].Value.Trim(),
                Description = descriptionMatch.Groups["value"].Value.Trim(),
                ModVersion = modVersionMatch.Groups["value"].Value.Trim(),
                ZipUrl = zipUrl,
                DiscussionUrl = discussionUrl
            };
        }
        catch
        {
            return null;
        }
    }

    // Maximum time we will voluntarily wait for a 429/403 Retry-After before
    // giving up; anything longer is reported as a failure to the caller.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private async Task<string> GetStringWithRateLimitAsync(string url)
    {
        using var response = await GetWithRateLimitAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if ((int)response.StatusCode == 429 ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 response.Headers.RetryAfter != null))
            {
                // GitHub's secondary/abuse limits return 429 or 403 with a
                // Retry-After header. Honour it exactly once, then fail.
                var delay = GetRetryAfterDelay(response);
                var statusCode = (int)response.StatusCode;
                response.Dispose();

                if (attempt == 0 && delay > TimeSpan.Zero && delay <= MaxRetryAfter)
                {
                    await Task.Delay(delay);
                    continue;
                }

                throw new HttpRequestException(
                    $"GitHub rate-limited request to {url} (status {statusCode}, Retry-After {delay.TotalSeconds:F0}s).");
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

    private static string ConvertHtmlToText(string html)
    {
        var text = html;
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty, RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
