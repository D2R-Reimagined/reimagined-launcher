using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ServerSavesLaunchSettings(
    string ApiBaseUrl,
    string AccessToken,
    Guid? LadderId,
    string LadderLaunchTicket,
    string StatusSessionId = "",
    IReadOnlyList<string>? LobbyRegionIds = null,
    string? LobbyServerId = null);

/// <summary>
/// Configures the Server Saves plugin supplied by the signed ladder package.
/// </summary>
public static class ServerSavesConfigService
{
    public const string PluginId = "server-saves";
    public const string PluginFileName = "d2rl-server-saves.dll";

    private const string ManagedHeader =
        "# server-saves - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url, ladder_id,\n"
        + "# lobby_region_ids and lobby_server_id every launch, and always leaves\n"
        + "# access_token, ladder_launch_ticket and status_session_id empty: those come\n"
        + "# from <install>/reimagined-secrets/session.toml, outside mods/, so they are\n"
        + "# never shared with other players.\n"
        + "#\n"
        + "# Anything else you set here is preserved, and any\n"
        + "# setting left out uses the plugin's built-in default.\n"
        + "\n";

    private static readonly D2RLoaderPluginPackage Package = new(PluginId, PluginFileName, ManagedHeader, modName: ModInstallationPaths.LadderModName);
    private static readonly D2RLoaderPluginPackage NormalPackage = new(PluginId, PluginFileName, ManagedHeader);

    public static bool IsPluginInstalled(string? installDirectory)
    {
        return Package.IsInstalled(installDirectory);
    }

    /// <summary>
    /// Points the plugin at the API with a usable token. Returns false when the
    /// config could not be written, which the caller must treat as a reason not
    /// to launch: a ladder session with the plugin disabled would silently use
    /// local characters.
    /// </summary>
    public static async Task<bool> EnableAsync(
        string? installDirectory,
        ServerSavesLaunchSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            LaunchDiagnostics.Log("server-saves: refusing to enable without an API address and access token.");
            return false;
        }
        if (settings.LadderId is not null && string.IsNullOrWhiteSpace(settings.LadderLaunchTicket))
        {
            LaunchDiagnostics.Log("server-saves: refusing to enable for a ladder without a signed launch ticket.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "true",
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.BlankSecret,
            ["ladder_id"] = D2RLoaderPluginPackage.Quote(settings.LadderId is { } ladderId ? ladderId.ToString() : string.Empty),
            ["ladder_launch_ticket"] = D2RLoaderPluginPackage.BlankSecret,
            ["status_session_id"] = D2RLoaderPluginPackage.BlankSecret,
            ["lobby_region_ids"] = D2RLoaderPluginPackage.Quote(string.Join(",", settings.LobbyRegionIds ?? [])),
            ["lobby_server_id"] = D2RLoaderPluginPackage.Quote(settings.LobbyServerId ?? string.Empty)
        };

        return await Package.WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }

    /// <summary>
    /// Turns the plugin off and clears the stored token. Every non-ladder launch
    /// must do this, otherwise a token left from an earlier ladder session would
    /// keep hiding the player's own characters.
    /// </summary>
    public static async Task<bool> DisableAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "false",
            ["access_token"] = D2RLoaderPluginPackage.BlankSecret,
            ["ladder_id"] = "\"\"",
            ["ladder_launch_ticket"] = D2RLoaderPluginPackage.BlankSecret,
            ["status_session_id"] = D2RLoaderPluginPackage.BlankSecret,
            ["lobby_region_ids"] = "\"\"",
            ["lobby_server_id"] = "\"\""
        };

        var normalDisabled = await NormalPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        var ladderDisabled = await Package.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        return normalDisabled && ladderDisabled;
    }

    /// <summary>Kept for the tests that cover the TOML rewriting directly.</summary>
    internal static string UpsertScalar(string toml, string key, string value)
    {
        return D2RLoaderPluginPackage.UpsertScalar(toml, key, value);
    }
}
