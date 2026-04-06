using System;
using System.Collections.Generic;

namespace ReimaginedLauncher.HttpClients.Models;

public class GitHubAnnouncement
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool IsUnread { get; set; }
    public bool HasExpandableContent { get; set; }
    public bool IsExpanded { get; set; }
    public IReadOnlyList<GitHubAnnouncementBlock> Blocks { get; set; } = [];
    public IReadOnlyList<GitHubAnnouncementBlock> PreviewBlocks { get; set; } = [];

    public string MetaText => $"#{Number} | {Author} | {PublishedAt.LocalDateTime:MMM d, yyyy}";
    public string ExpandActionText => IsExpanded ? "Show less" : "Show more";
    public bool ShowPreview => HasExpandableContent && !IsExpanded;
    public bool ShowExpandedBody => HasExpandableContent && IsExpanded;
    public bool ShowFullBody => !HasExpandableContent;
}
