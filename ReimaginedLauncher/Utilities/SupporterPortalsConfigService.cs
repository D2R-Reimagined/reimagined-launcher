using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record SupporterPortalsLaunchSettings(string ApiBaseUrl, string AccessToken);
public static class SupporterPortalsConfigService
{
    public const string PluginId = "supporter-portals";
    public const string PluginFileName = "d2rl-supporter-portals.dll";

    private const string ManagedHeader =
        "# supporter-portals - launcher-managed API address and access token.\n\n";

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
        SupporterPortalsLaunchSettings settings,
        LaunchExperience experience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            LaunchDiagnostics.Log("supporter-portals: refusing to enable without an API address and access token.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken)
        };

        return await PackageFor(experience).WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }
    public static async Task<bool> DisableAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_token"] = "\"\""
        };

        var normalDisabled = await NormalPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        var ladderDisabled = await LadderPackage.WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
        return normalDisabled && ladderDisabled;
    }
}
