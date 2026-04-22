using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public class GitHubDiscussionPluginsHttpClient
{
    private const string BaseUrl = "https://github.com";
    private const string PluginsDiscussionsUrl =
        $"{BaseUrl}/D2R-Reimagined/reimagined-launcher/discussions/categories/plugins";

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

    private static readonly Regex ModVersionFieldRegex = new(
        @"(?:Mod|ModVer|ModVersion):\s*(?<value>\d+\.\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;

    public GitHubDiscussionPluginsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
    }

    public async Task<IReadOnlyList<UserPluginEntry>> GetUserPluginsAsync()
    {
        try
        {
            var html = await _httpClient.GetStringAsync(PluginsDiscussionsUrl);
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

            return plugins;
        }
        catch
        {
            return [];
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

        var response = await _httpClient.GetAsync(zipUrl);
        response.EnsureSuccessStatusCode();

        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream);

        return tempPath;
    }

    private async Task<UserPluginEntry?> ParseDiscussionAsync(string discussionUrl)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(discussionUrl);

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
