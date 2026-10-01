using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record TradePriceCheckLaunchSettings(string ApiBaseUrl, string? AccessToken, string? LadderId = null);

/// <summary>
/// Points the trade-price-check plugin (Ctrl+D market lookup) at the API.
/// </summary>
/// <remarks>
/// GET /trades is anonymous, so unlike trade-notifications this does not refuse
/// a launch with nobody signed in: the plugin still searches, just without an
/// Authorization header. The plugin has no enabled switch - it is on whenever
/// its DLL is installed - so disabling here only clears the token and ladder.
/// </remarks>
public static class TradePriceCheckConfigService
{
    public const string PluginId = "trade-price-check";
    public const string PluginFileName = "d2rl-trade-price-check.dll";

    private const string ManagedHeader =
        "# trade-price-check - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites api_base_url, access_token and ladder_id\n"
        + "# every launch. Anything else you set here is preserved, and any setting\n"
        + "# left out uses the plugin's built-in default - including hotkey_vk,\n"
        + "# hotkey_ctrl and max_results.\n"
        + "\n";

    private static readonly D2RLoaderPluginPackage NormalPackage = new(PluginId, PluginFileName, ManagedHeader);
    private static readonly D2RLoaderPluginPackage LadderPackage = new(PluginId, PluginFileName, ManagedHeader, modName: ModInstallationPaths.LadderModName);

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

    public static async Task<bool> EnableAsync(
        string? installDirectory,
        TradePriceCheckLaunchSettings settings,
        LaunchExperience experience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
        {
            LaunchDiagnostics.Log("trade-price-check: refusing to configure without an API address.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken?.Trim() ?? string.Empty),
            // Only a ladder launch narrows the search to one ladder; an online
            // launch searches every active listing.
            ["ladder_id"] = D2RLoaderPluginPackage.Quote(experience == LaunchExperience.Ladder ? settings.LadderId ?? string.Empty : string.Empty)
        };

        return await PackageFor(experience).WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }

    public static async Task<bool> DisableAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_token"] = "\"\"",
            ["ladder_id"] = "\"\""
        };

        var normalDisabled = await NormalPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        var ladderDisabled = await LadderPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        return normalDisabled && ladderDisabled;
    }
}
