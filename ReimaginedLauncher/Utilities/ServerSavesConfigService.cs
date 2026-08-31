using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ServerSavesLaunchSettings(
    string ApiBaseUrl,
    string AccessToken,
    Guid? LadderId,
    string LadderLaunchTicket);

/// <summary>
/// Installs the server-saves D2RLoader plugin from the launcher's own bundled
/// copy and writes the launch-time settings it needs into its TOML config. The
/// plugin stores a player's characters on the Reimagined API and hides the
/// local ones, so it must only ever be enabled for a signed-in ladder launch.
/// </summary>
/// <remarks>
/// The installing and TOML rewriting live in <see cref="D2RLoaderPluginPackage"/>,
/// shared with the other bundled plugins. What stays here is what is specific to
/// this one: the settings it owns, and the rule that it is never enabled without
/// both an API address and a token.
/// </remarks>
public static class ServerSavesConfigService
{
    public const string PluginId = "server-saves";
    public const string PluginFileName = "d2rl-server-saves.dll";

    private const string ManagedHeader =
        "# server-saves - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url, access_token,\n"
        + "# ladder_id and ladder_launch_ticket every launch. Anything else you set\n"
        + "# here is preserved, and any\n"
        + "# setting left out uses the plugin's built-in default.\n"
        + "\n";

    private static readonly D2RLoaderPluginPackage Package = new(PluginId, PluginFileName, ManagedHeader);

    public static bool IsPluginInstalled(string? installDirectory)
    {
        return Package.IsInstalled(installDirectory);
    }

    internal static bool CanSupplyApprovedPlugin(
        string fileName,
        string sha256,
        string? bundledPluginPath = null)
    {
        return Package.CanSupplyApproved(fileName, sha256, bundledPluginPath);
    }

    /// <summary>
    /// Copies the launcher's bundled plugin into the mod's plugin folder if it
    /// is missing or out of date, so players never have to source the DLL
    /// themselves. Returns false only on a real failure - a copy that was
    /// already current is success, not a no-op to warn about.
    /// </summary>
    public static Task<bool> EnsureInstalledAsync(string? installDirectory, string? bundledPluginPath = null)
    {
        return Package.EnsureInstalledAsync(installDirectory, bundledPluginPath);
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
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken),
            ["ladder_id"] = D2RLoaderPluginPackage.Quote(settings.LadderId is { } ladderId ? ladderId.ToString() : string.Empty),
            ["ladder_launch_ticket"] = D2RLoaderPluginPackage.Quote(settings.LadderLaunchTicket)
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
            ["access_token"] = "\"\"",
            ["ladder_id"] = "\"\"",
            ["ladder_launch_ticket"] = "\"\""
        };

        return await Package.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
    }

    /// <summary>Kept for the tests that cover the TOML rewriting directly.</summary>
    internal static string UpsertScalar(string toml, string key, string value)
    {
        return D2RLoaderPluginPackage.UpsertScalar(toml, key, value);
    }
}
