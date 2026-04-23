namespace ReimaginedLauncher.HttpClients.Models;

public sealed class UserPluginEntry
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ModVersion { get; init; } = string.Empty;
    public string ZipUrl { get; init; } = string.Empty;
    public string DiscussionUrl { get; init; } = string.Empty;
}
