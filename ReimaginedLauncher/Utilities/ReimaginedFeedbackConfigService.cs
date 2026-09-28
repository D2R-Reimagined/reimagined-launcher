using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ReimaginedFeedbackLaunchSettings(string ApiBaseUrl, string AccessToken);
public static class ReimaginedFeedbackConfigService
{
    public const string PluginId = "reimagined-feedback";
    public const string PluginFileName = "d2rl-reimagined-feedback.dll";

    private const string ManagedHeader =
        "# reimagined-feedback - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites api_base_url and access_token every\n"
        + "# launch; an empty access_token keeps the plugin disabled. Anything else\n"
        + "# you set here is preserved, and any setting left out uses the plugin's\n"
        + "# built-in default - including endpoint_path, timeout_ms, min_interval_ms\n"
        + "# and max_length.\n"
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
        ReimaginedFeedbackLaunchSettings settings,
        LaunchExperience experience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            LaunchDiagnostics.Log("reimagined-feedback: refusing to enable without an API address and access token.");
            return false;
        }

        // The plugin sends the bearer token over plain http only to this
        // machine (a local API), and refuses to load with any other http URL.
        if (!IsAllowedApiBaseUrl(settings.ApiBaseUrl))
        {
            LaunchDiagnostics.Log($"reimagined-feedback: {settings.ApiBaseUrl} is neither https nor a local http API; the plugin would refuse it.");
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_base_url"] = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = D2RLoaderPluginPackage.Quote(settings.AccessToken)
        };

        return await PackageFor(experience).WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
    }
    public static bool IsAllowedApiBaseUrl(string apiBaseUrl)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
               || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
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
