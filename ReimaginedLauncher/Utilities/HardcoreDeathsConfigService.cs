using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record HardcoreDeathsLaunchSettings(string ApiBaseUrl, string AccessToken);

/// <summary>
/// Points an already-installed hardcore-deaths plugin at the API, so a player
/// hears every hardcore death on the ladder and their own is announced to
/// everyone else.
/// </summary>
/// <remarks>
/// <para>The twin of <see cref="GlobalChatConfigService"/>: never installs the
/// plugin, and offered for the same two experiences.</para>
///
/// <para>The API contract is asymmetric. Listening is anonymous, so a plugin
/// with no token is not silent - it hears every death and is refused when it
/// reports its own. <see cref="EnableAsync"/> therefore still requires a token
/// rather than writing the address alone.</para>
///
/// <para>The mod folder differs per experience: a ladder launch runs
/// <c>ReimaginedLadder</c>, every other one <c>Reimagined</c>. Disabling writes
/// to both, because the launch that needs turning off is not necessarily the one
/// that turned it on.</para>
///
/// <para><c>death_ui_target</c> is deliberately not written here. It is a
/// discovered value belonging to the player or the ladder bundle, and the
/// launcher has no way to know it.</para>
/// </remarks>
public static class HardcoreDeathsConfigService
{
    public const string PluginId = "hardcore-deaths";
    public const string PluginFileName = "d2rl-hardcore-deaths.dll";

    private const string ManagedHeader =
        "# hardcore-deaths - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url and access_token\n"
        + "# every launch. Anything else you set here is preserved, and any setting\n"
        + "# left out uses the plugin's built-in default - including death_ui_target,\n"
        + "# killer_prefix, channel_tag and the colour bytes.\n"
        + "\n";

    private static readonly D2RLoaderPluginPackage NormalPackage = new(PluginId, PluginFileName, ManagedHeader);
    private static readonly D2RLoaderPluginPackage LadderPackage = new(PluginId, PluginFileName, ManagedHeader, modName: ModInstallationPaths.LadderModName);

    /// <summary>
    /// Whether death announcements are offered for this launch at all. A D2RMM
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
    /// A caller should not block a launch on this failing. Losing death
    /// announcements costs a convenience; it does not put the session's
    /// characters or progress at risk the way an unconfigured server-saves does.
    /// </remarks>
    public static async Task<bool> EnableAsync(
        string? installDirectory,
        HardcoreDeathsLaunchSettings settings,
        LaunchExperience experience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            // Reporting is authenticated even though listening is not.
            LaunchDiagnostics.Log("hardcore-deaths: refusing to enable without an API address and access token.");
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
