using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients;

public class GitHubAnnouncementsHttpClient
{
    private const string AnnouncementsUrl = ProxyEndpoints.Announcements;

    private static readonly Regex BlockRegex = new(
        "<(?<tag>h[1-6]|p|li)[^>]*>(?<content>.*?)</(?<endtag>h[1-6]|p|li)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Serialize concurrent callers so the static fallback cache is mutated
    // and read under a single lock (mirrors GitHubDiscussionPluginsHttpClient).
    private static readonly SemaphoreSlim FetchLock = new(1, 1);
    private static IReadOnlyList<GitHubAnnouncement>? _lastKnownGood;

    private readonly HttpClient _httpClient;

    public GitHubAnnouncementsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
    }

    public async Task<IReadOnlyList<GitHubAnnouncement>> GetAnnouncementsAsync()
    {
        await FetchLock.WaitAsync();
        try
        {
            using var response = await ProxyHttpHelper.GetWithRateLimitAsync(_httpClient, AnnouncementsUrl);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<ProxyAnnouncement[]>() ?? [];

            var announcements = payload
                .Select(ToAnnouncement)
                .OrderByDescending(announcement => announcement.Number)
                .ToList();

            _lastKnownGood = announcements;
            return announcements;
        }
        catch (Exception ex)
        {
            // Fall back to the last good copy so a single transient outage
            // doesn't blank the announcements panel.
            LaunchDiagnostics.LogException("Failed to fetch announcements from proxy", ex);
            return _lastKnownGood ?? [];
        }
        finally
        {
            FetchLock.Release();
        }
    }

    private static GitHubAnnouncement ToAnnouncement(ProxyAnnouncement source)
    {
        var title = DecodeText(source.Title ?? string.Empty);
        var blocks = ParseBlocks(source.BodyHtml ?? string.Empty);
        if (blocks.Count > 0 && string.Equals(blocks[0].Text, title, StringComparison.OrdinalIgnoreCase))
        {
            blocks.RemoveAt(0);
        }

        var effectiveBlocks = blocks.Count > 0
            ? (IReadOnlyList<GitHubAnnouncementBlock>)blocks
            :
            [
                new GitHubAnnouncementBlock
                {
                    Kind = "paragraph",
                    Text = !string.IsNullOrWhiteSpace(source.BodyText)
                        ? source.BodyText!.Trim()
                        : "Open the discussion on GitHub to read the full announcement."
                }
            ];

        var bodyText = string.Join(Environment.NewLine, effectiveBlocks.Select(block => block.Text));
        var previewBlocks = BuildPreviewBlocks(effectiveBlocks, out var hasExpandableContent);
        var previewText = string.Join(Environment.NewLine, previewBlocks.Select(block => block.Text));

        return new GitHubAnnouncement
        {
            Number = source.Number,
            Title = title,
            Author = DecodeText(source.Author ?? string.Empty),
            PublishedAt = source.PublishedAt ?? DateTimeOffset.MinValue,
            Url = source.Url ?? string.Empty,
            Blocks = effectiveBlocks,
            BodyText = bodyText,
            Summary = bodyText,
            PreviewBlocks = previewBlocks,
            PreviewText = previewText,
            HasExpandableContent = hasExpandableContent
        };
    }

    private static List<GitHubAnnouncementBlock> ParseBlocks(string bodyHtml)
    {
        var blocks = new List<GitHubAnnouncementBlock>();

        foreach (Match match in BlockRegex.Matches(bodyHtml))
        {
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var endTag = match.Groups["endtag"].Value.ToLowerInvariant();
            if (tag != endTag)
            {
                continue;
            }

            var text = ConvertInlineHtmlToText(match.Groups["content"].Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            blocks.Add(new GitHubAnnouncementBlock
            {
                Kind = tag == "p" ? "paragraph" : tag,
                Text = text
            });
        }

        return blocks;
    }

    private static IReadOnlyList<GitHubAnnouncementBlock> BuildPreviewBlocks(
        IReadOnlyList<GitHubAnnouncementBlock> blocks,
        out bool hasExpandableContent)
    {
        if (blocks.Count > 2)
        {
            hasExpandableContent = true;
            return blocks.Take(2).ToArray();
        }

        hasExpandableContent = false;
        return blocks;
    }

    private static string ConvertInlineHtmlToText(string html)
    {
        var text = html;
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<a[^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<text>.*?)</a>", match =>
        {
            var linkText = DecodeText(match.Groups["text"].Value);
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            return string.IsNullOrWhiteSpace(linkText) ? href : $"{linkText} ({href})";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", string.Empty, RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        text = Regex.Replace(text, "[ \t]+\n", "\n");
        text = Regex.Replace(text, "\n[ \t]+", "\n");
        return text.Trim();
    }

    private static string DecodeText(string value)
    {
        var decoded = WebUtility.HtmlDecode(StripTags(value));
        return string.Join(
            ' ',
            decoded.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripTags(string value)
    {
        return Regex.Replace(value, "<.*?>", string.Empty);
    }

    private sealed class ProxyAnnouncement
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("bodyHtml")]
        public string? BodyHtml { get; set; }

        [JsonPropertyName("bodyText")]
        public string? BodyText { get; set; }
    }
}
