using System;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed class UserPluginEntry
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ModVersion { get; init; } = string.Empty;
    public string? PluginVersion { get; init; }
    public string ZipUrl { get; init; } = string.Empty;
    public string DiscussionUrl { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }

    // Local install state, populated by the view from the launcher's stored plugin
    // registrations (matched by DiscussionUrl); not part of the GitHub discussion payload.
    public bool IsInstalled { get; set; }
    public string? InstalledVersion { get; set; }
    public bool IsOutOfDate { get; set; }
    public bool HasInstalledVersion => !string.IsNullOrWhiteSpace(InstalledVersion);
    public bool IsUpToDate => IsInstalled && !IsOutOfDate;
}
