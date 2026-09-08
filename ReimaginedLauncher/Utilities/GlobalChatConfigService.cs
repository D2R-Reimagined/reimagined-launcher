using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record GlobalChatLaunchSettings(string ApiBaseUrl, string AccessToken);

/// <summary>
/// Points an already-installed global-chat plugin at the API, so players can
/// talk to each other in game.
/// </summary>
/// <remarks>
/// This is the two-way counterpart to <see cref="ChatRelayConfigService"/>, and
/// it differs from that service in two ways that matter.
///
/// <para><b>It never installs the plugin.</b> chat-relay ships inside the
/// launcher build and is copied into place on launch. Global chat does not: it
/// arrives in the ladder bundle or as an approved extension, or the player
/// downloads it from the website and drops it into their own mod install. The
/// launcher's whole job here is to notice that the plugin is present and write
/// the token into its config. A missing plugin is an ordinary outcome, not a
/// failure - which is why there is no EnsureInstalledAsync and no bundled asset
/// for this plugin, and why <see cref="EnableAsync"/> requires the plugin to be
/// installed before it will write anything.</para>
///
/// <para><b>It is enabled for a D2RLoader launch, not only a ladder one.</b>
/// chat-relay is ladder-only because it captures what a player types and posts
/// it onward, which is not something to leave running over someone's ordinary
/// session. Global chat captures nothing on its own: a player reaches it by
/// typing <c>/g</c>, or by explicitly turning global mode on. So a player who
/// has installed the plugin themselves gets global chat in D2RLoader mode.</para>
///
/// <para>The mod folder differs per experience - a ladder launch runs
/// <c>ReimaginedLadder</c> and every other one runs <c>Reimagined</c> - so the
/// caller passes the experience and this picks the matching package. Disabling
/// writes to both, because the launch that needs turning off is not necessarily
/// the one that turned it on.</para>
///
/// <para>The API names the sender from the token rather than from the message,
/// so the token written here decides which name other players see. Writing
/// another account's token would publish this player's words under that
/// name.</para>
/// </remarks>
public static class GlobalChatConfigService
{
    public const string PluginId = "global-chat";
    public const string PluginFileName = "d2rl-global-chat.dll";

    private const string ManagedHeader =
        "# global-chat - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url and access_token\n"
        + "# every launch. Anything else you set here is preserved, and any setting\n"
        + "# left out uses the plugin's built-in default - including channel_tag,\n"
        + "# history_lines and the colour bytes.\n"
        + "\n";

    private static readonly D2RLoaderPluginPackage NormalPackage = new(PluginId, PluginFileName, ManagedHeader);
    private static readonly D2RLoaderPluginPackage LadderPackage = new(PluginId, PluginFileName, ManagedHeader, modName: ModInstallationPaths.LadderModName);

    /// <summary>
    /// Whether global chat is offered for this launch at all. A D2RMM
    /// installation has no D2RLoader to load the plugin, and an offline launch
    /// is the one mode that is meant to need no account.
    /// </summary>
    public static bool IsEligible(InstallationType type, LaunchExperience experience)
    {
        return type != InstallationType.D2RMM
               && experience is LaunchExperience.Online or LaunchExperience.Ladder;
    }

    private static D2RLoaderPluginPackage PackageFor(LaunchExperience experience)
    {
        return experience == LaunchExperience.Ladder ? LadderPackage : NormalPackage;
    }

    public static bool IsPluginInstalled(string? installDirectory, LaunchExperience experience)
    {
        return PackageFor(experience).IsInstalled(installDirectory);
    }

    /// <summary>
    /// Points the plugin at the API with a usable token.
    /// </summary>
    /// <remarks>
    /// A caller should not block a launch on this failing. Global chat not
    /// working costs a convenience; it does not put the session's characters or
    /// progress at risk the way an unconfigured server-saves does.
    /// </remarks>
    public static async Task<bool> EnableAsync(
        string? installDirectory,
        GlobalChatLaunchSettings settings,
        LaunchExperience experience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            // The endpoint is authenticated and the token carries the display
            // name. Enabling without one leaves the plugin reconnecting into an
            // HTTP 401 for the whole session.
            LaunchDiagnostics.Log("global-chat: refusing to enable without an API address and access token.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "true",
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken)
        };

        return await PackageFor(experience).WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }

    /// <summary>
    /// Turns the plugin off and clears the stored token.
    /// </summary>
    /// <remarks>
    /// Both mod folders are cleared, not just the one this launch would use. A
    /// player who signs out and launches offline should not leave a working
    /// token sitting in the ladder mod's config for the next launch to pick up.
    /// </remarks>
    public static async Task<bool> DisableAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "false",
            ["access_token"] = "\"\""
        };

        var normalDisabled = await NormalPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        var ladderDisabled = await LadderPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        return normalDisabled && ladderDisabled;
    }
}
