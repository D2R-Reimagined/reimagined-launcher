using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ChatRelayLaunchSettings(string ApiBaseUrl, string AccessToken);

/// <summary>
/// Installs the chat-relay D2RLoader plugin from the launcher's own bundled copy
/// and points it at the API for a ladder launch.
/// </summary>
/// <remarks>
/// The plugin reads what the player types in game and sends it to the API, which
/// posts it to the Discord global chat. Two consequences shape this service:
///
/// It is only ever enabled for a signed-in ladder launch, and every other launch
/// clears the token and turns it off. Capturing chat is not something to leave
/// running over someone's ordinary single-player session because a ladder
/// session happened earlier.
///
/// The API names the sender from the token, not from the message, so the token
/// written here decides whose name appears in Discord. Writing another account's
/// token would publish this player's words under that name.
/// </remarks>
public static class ChatRelayConfigService
{
    public const string PluginId = "chat-relay";
    public const string PluginFileName = "d2rl-chat-relay.dll";

    private const string ManagedHeader =
        "# chat-relay - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url and access_token\n"
        + "# every launch. Anything else you set here is preserved, and any setting\n"
        + "# left out uses the plugin's built-in default - including relay_whispers,\n"
        + "# which stays off unless you deliberately turn it on.\n"
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
    /// is missing or out of date, so the DLL players run is the one the launcher
    /// shipped rather than something they sourced themselves.
    /// </summary>
    public static Task<bool> EnsureInstalledAsync(string? installDirectory, string? bundledPluginPath = null)
    {
        return Package.EnsureInstalledAsync(installDirectory, bundledPluginPath);
    }

    /// <summary>
    /// Points the plugin at the API with a usable token.
    /// </summary>
    /// <remarks>
    /// A caller should not block a launch on this failing. Chat not reaching
    /// Discord is a missing convenience; it does not put the session's
    /// characters or progress at risk the way an unconfigured server-saves does.
    /// </remarks>
    public static async Task<bool> EnableAsync(
        string? installDirectory,
        ChatRelayLaunchSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            // Enabling capture without somewhere to send it would read the
            // player's chat for nothing.
            LaunchDiagnostics.Log("chat-relay: refusing to enable without an API address and access token.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "true",
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken)
        };

        return await Package.WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }

    /// <summary>
    /// Turns the plugin off and clears the stored token, so nothing a player
    /// types outside a ladder session is captured or sent anywhere.
    /// </summary>
    public static async Task<bool> DisableAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "false",
            ["access_token"] = "\"\""
        };

        return await Package.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
    }
}
