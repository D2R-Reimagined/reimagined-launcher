using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public class GitHubAnnouncementsHttpClient
{
    private const string RepositoryPath = "/D2R-Reimagined/reimagined-launcher";
    private const string BaseUrl = "https://github.com";
    private const string AnnouncementsUrl = $"{BaseUrl}{RepositoryPath}/discussions/categories/announcements";
    private static readonly Regex DiscussionRegex = new(
        "<a[^>]+href=\"(?<href>/D2R-Reimagined/reimagined-launcher/discussions/(?<number>\\d+))\"[^>]*class=\"[^\"]*markdown-title[^\"]*\"[^>]*>(?<title>.*?)</a>\\s*</h3>\\s*<div class=\"text-small color-fg-muted mt-1\">\\s*<a[^>]+href=\"/(?<authorPath>[^\"]+)\"[^>]*>(?<author>.*?)</a>\\s*announced\\s*<relative-time datetime=\"(?<published>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex DescriptionRegex = new(
        "<meta property=\"og:description\" content=\"(?<description>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BodyRegex = new(
        "<td\\s+class=\"[^\"]*comment-body markdown-body js-comment-body[^\"]*\"[^>]*>(?<body>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BlockRegex = new(
        "<(?<tag>h[1-6]|p|li)[^>]*>(?<content>.*?)</(?<endtag>h[1-6]|p|li)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly HttpClient _httpClient;

    public GitHubAnnouncementsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
    }

    public async Task<IReadOnlyList<GitHubAnnouncement>> GetAnnouncementsAsync()
    {
        try
        {
            var html = await _httpClient.GetStringAsync(AnnouncementsUrl);
            var announcements = new List<GitHubAnnouncement>();
            var seenDiscussionNumbers = new HashSet<int>();

            foreach (Match match in DiscussionRegex.Matches(html))
            {
                if (!int.TryParse(match.Groups["number"].Value, out var discussionNumber) ||
                    !seenDiscussionNumbers.Add(discussionNumber))
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(match.Groups["published"].Value, out var publishedAt))
                {
                    publishedAt = DateTimeOffset.MinValue;
                }

                announcements.Add(new GitHubAnnouncement
                {
                    Number = discussionNumber,
                    Title = DecodeText(match.Groups["title"].Value),
                    Author = DecodeText(match.Groups["author"].Value),
                    PublishedAt = publishedAt,
                    Url = $"{BaseUrl}{match.Groups["href"].Value}"
                });
            }

            announcements = announcements
                .OrderByDescending(announcement => announcement.Number)
                .ToList();

            foreach (var announcement in announcements)
            {
                var blocks = await GetAnnouncementBlocksAsync(announcement.Url, announcement.Title);
                announcement.Blocks = blocks;
                announcement.BodyText = string.Join(Environment.NewLine, blocks.Select(block => block.Text));
                announcement.Summary = announcement.BodyText;
                announcement.PreviewBlocks = BuildPreviewBlocks(blocks, out var hasExpandableContent);
                announcement.PreviewText = string.Join(Environment.NewLine, announcement.PreviewBlocks.Select(block => block.Text));
                announcement.HasExpandableContent = hasExpandableContent;
            }

            return announcements;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<GitHubAnnouncementBlock>> GetAnnouncementBlocksAsync(string url, string title)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var match = BodyRegex.Match(html);
            if (!match.Success)
            {
                return
                [
                    new GitHubAnnouncementBlock
                    {
                        Kind = "paragraph",
                        Text = await GetAnnouncementSummaryAsync(url, title)
                    }
                ];
            }

            var blocks = ParseBlocks(match.Groups["body"].Value);
            if (blocks.Count > 0 && string.Equals(blocks[0].Text, title, StringComparison.OrdinalIgnoreCase))
            {
                blocks.RemoveAt(0);
            }

            if (blocks.Count == 0)
            {
                return
                [
                    new GitHubAnnouncementBlock
                    {
                        Kind = "paragraph",
                        Text = await GetAnnouncementSummaryAsync(url, title)
                    }
                ];
            }

            return blocks;
        }
        catch
        {
            return
            [
                new GitHubAnnouncementBlock
                {
                    Kind = "paragraph",
                    Text = await GetAnnouncementSummaryAsync(url, title)
                }
            ];
        }
    }

    private async Task<string> GetAnnouncementSummaryAsync(string url, string title)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var match = DescriptionRegex.Match(html);
            if (!match.Success)
            {
                return "Open the discussion on GitHub to read the full announcement.";
            }

            var summary = DecodeText(match.Groups["description"].Value);
            if (string.IsNullOrWhiteSpace(summary) ||
                string.Equals(summary, title, StringComparison.OrdinalIgnoreCase))
            {
                return "Open the discussion on GitHub to read the full announcement.";
            }

            return summary;
        }
        catch
        {
            return "Open the discussion on GitHub to read the full announcement.";
        }
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
}
