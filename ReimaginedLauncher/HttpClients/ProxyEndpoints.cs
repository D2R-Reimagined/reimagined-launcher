namespace ReimaginedLauncher.HttpClients;

/// <summary>
/// Base URL and endpoints for the Reimagined Launcher Cloudflare Worker proxy
/// that wraps the GitHub Discussions GraphQL API.
/// </summary>
internal static class ProxyEndpoints
{
    public const string BaseUrl = "https://reimagined-proxy.leminkainen118.workers.dev";
    public const string Announcements = $"{BaseUrl}/announcements";
    public const string Plugins = $"{BaseUrl}/plugins";
}
