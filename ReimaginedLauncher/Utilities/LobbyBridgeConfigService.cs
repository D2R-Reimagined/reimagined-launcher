using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Keeps mods\ReimaginedLadder\d2rloader\config\lobby-bridge.toml aligned with
/// the selected ladder's runtime loader so switching between D2RLoader and
/// Reimagined Loader ladders never leaves a stale lobby_mode behind.
/// </summary>
public static class LobbyBridgeConfigService
{
    public const string PluginFileName = "d2rl-lobby-bridge.dll";
    public const string ConfigFileName = "lobby-bridge.toml";

    public static async Task ConfigureForLadderAsync(
        string? installDirectory,
        LadderLoaderType loaderType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) return;
        var modRoot = ModInstallationPaths.LadderModRoot(installDirectory);
        var pluginPath = Path.Combine(modRoot, "d2rloader", "plugins", PluginFileName);
        var configPath = Path.Combine(modRoot, "d2rloader", "config", ConfigFileName);
        if (!File.Exists(pluginPath) && !File.Exists(configPath)) return;

        var mode = loaderType == LadderLoaderType.ReimaginedLoader ? "battlenet" : "d2rloader";
        var original = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : null;
        var updated = LocalD2rdsRigService.WithLobbyMode(original, mode);
        if (!string.Equals(original, updated, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, updated, new UTF8Encoding(false), cancellationToken);
        }
        LaunchDiagnostics.Log($"lobby-bridge configured for {loaderType} (lobby_mode = \"{mode}\").");
    }
}
