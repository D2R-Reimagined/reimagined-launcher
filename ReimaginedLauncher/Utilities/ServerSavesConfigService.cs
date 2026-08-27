using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ServerSavesLaunchSettings(string ApiBaseUrl, string AccessToken, Guid? LadderId);

/// <summary>
/// Installs the server-saves D2RLoader plugin from the launcher's own bundled
/// copy and writes the launch-time settings it needs into its TOML config. The
/// plugin stores a player's characters on the Reimagined API and hides the
/// local ones, so it must only ever be enabled for a signed-in ladder launch.
/// </summary>
public static class ServerSavesConfigService
{
    public const string PluginId = "server-saves";
    public const string PluginFileName = "d2rl-server-saves.dll";

    private const string LoaderFolderName = "d2rloader";
    private const string ModName = "Reimagined";

    // The plugin's own plugin.json fixes its scope to this mod, so this is the
    // only place D2RLoader will ever load it from - unlike the TOML config,
    // installation never touches the global scope.
    private static readonly string BundledPluginPath = Path.Combine(
        AppContext.BaseDirectory, "Assets", "D2RLoaderPlugins", "server-saves", PluginFileName);

    private const string ManagedHeader =
        "# server-saves - launcher-managed settings.\n"
        + "#\n"
        + "# The Reimagined launcher rewrites enabled, api_base_url, access_token and\n"
        + "# ladder_id every launch. Anything else you set here is preserved, and any\n"
        + "# setting left out uses the plugin's built-in default.\n"
        + "\n";

    public static bool IsPluginInstalled(string? installDirectory)
    {
        return ResolveInstalledRoots(installDirectory).Count > 0;
    }

    internal static bool CanSupplyApprovedPlugin(
        string fileName,
        string sha256,
        string? bundledPluginPath = null)
    {
        if (!string.Equals(fileName, PluginFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = bundledPluginPath ?? BundledPluginPath;
        try
        {
            return File.Exists(source)
                   && string.Equals(
                       Convert.ToHexString(Sha256Of(source)),
                       sha256.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the launcher's bundled plugin into the mod's plugin folder if it
    /// is missing or out of date, so players never have to source the DLL
    /// themselves. Returns false only on a real failure - a copy that was
    /// already current is success, not a no-op to warn about.
    /// </summary>
    public static Task<bool> EnsureInstalledAsync(string? installDirectory, string? bundledPluginPath = null)
    {
        var source = bundledPluginPath ?? BundledPluginPath;
        if (!File.Exists(source))
        {
            LaunchDiagnostics.Log($"server-saves: the bundled plugin is missing from the launcher build ({source}).");
            return Task.FromResult(false);
        }

        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Task.FromResult(false);
        }

        var targetDirectory = Path.Combine(normalized, "mods", ModName, LoaderFolderName, "plugins");
        var target = Path.Combine(targetDirectory, PluginFileName);

        try
        {
            if (File.Exists(target) && FilesAreIdentical(source, target))
            {
                return Task.FromResult(true);
            }

            Directory.CreateDirectory(targetDirectory);
            File.Copy(source, target, overwrite: true);
            LaunchDiagnostics.Log($"server-saves: installed the bundled plugin to {target}.");
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"server-saves: could not install the plugin to {target}: {exception.Message}");
            return Task.FromResult(false);
        }
    }

    private static bool FilesAreIdentical(string first, string second)
    {
        return Sha256Of(first).SequenceEqual(Sha256Of(second));
    }

    private static byte[] Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
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

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = "true",
            ["api_base_url"] = Quote(NormalizeBaseUrl(settings.ApiBaseUrl)),
            ["access_token"] = Quote(settings.AccessToken),
            ["ladder_id"] = Quote(settings.LadderId is { } ladderId ? ladderId.ToString() : string.Empty)
        };

        return await WriteAsync(installDirectory, values, requireInstalled: true, cancellationToken);
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
            ["ladder_id"] = "\"\""
        };

        return await WriteAsync(installDirectory, values, requireInstalled: false, cancellationToken);
    }

    private static async Task<bool> WriteAsync(
        string? installDirectory,
        IReadOnlyDictionary<string, string> values,
        bool requireInstalled,
        CancellationToken cancellationToken)
    {
        var roots = requireInstalled
            ? ResolveInstalledRoots(installDirectory)
            : ResolveConfiguredRoots(installDirectory);
        if (roots.Count == 0)
        {
            // Disabling something that was never configured is a no-op, not a
            // failure. Enabling a plugin that is not installed is a failure.
            return !requireInstalled;
        }

        var written = 0;
        foreach (var root in roots)
        {
            var configPath = GetConfigPath(root);
            try
            {
                var existing = File.Exists(configPath)
                    ? await File.ReadAllTextAsync(configPath, cancellationToken)
                    : ManagedHeader;

                var updated = values.Aggregate(existing, (toml, entry) => UpsertScalar(toml, entry.Key, entry.Value));
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                await File.WriteAllTextAsync(configPath, updated, new UTF8Encoding(false), cancellationToken);
                written++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"server-saves: could not write {configPath}: {exception.Message}");
            }
        }

        return written == roots.Count;
    }

    /// <summary>
    /// Replaces the first assignment of <paramref name="key"/>, or appends one.
    /// The plugin reads flat scalars and takes the first match, so this matches
    /// what it will actually see while leaving comments and other settings alone.
    /// </summary>
    internal static string UpsertScalar(string toml, string key, string value)
    {
        var lineEnding = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = toml.Split('\n').ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            if (!IsAssignmentOf(lines[index], key))
            {
                continue;
            }

            var trailingReturn = lines[index].EndsWith('\r') ? "\r" : string.Empty;
            lines[index] = $"{key} = {value}{trailingReturn}";
            return string.Join("\n", lines);
        }

        var rebuilt = string.Join("\n", lines);
        if (rebuilt.Length > 0 && !rebuilt.EndsWith('\n'))
        {
            rebuilt += lineEnding;
        }

        return $"{rebuilt}{key} = {value}{lineEnding}";
    }

    private static bool IsAssignmentOf(string line, string key)
    {
        var span = line.AsSpan().TrimStart();
        if (!span.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        span = span[key.Length..].TrimStart();
        return span.Length > 0 && span[0] == '=';
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string NormalizeBaseUrl(string value)
    {
        return value.TrimEnd('/');
    }

    private static string GetConfigPath(string loaderRoot)
    {
        return Path.Combine(loaderRoot, "config", $"{PluginId}.toml");
    }

    /// <summary>Loader roots that actually carry the plugin DLL.</summary>
    private static List<string> ResolveInstalledRoots(string? installDirectory)
    {
        return EnumerateRoots(installDirectory)
            .Where(root => File.Exists(Path.Combine(root, "plugins", PluginFileName)))
            .ToList();
    }

    /// <summary>Loader roots that already carry a config, installed or not.</summary>
    private static List<string> ResolveConfiguredRoots(string? installDirectory)
    {
        return EnumerateRoots(installDirectory)
            .Where(root => File.Exists(GetConfigPath(root)))
            .ToList();
    }

    private static IEnumerable<string> EnumerateRoots(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return Path.Combine(normalized, "mods", ModName, LoaderFolderName);
        yield return Path.Combine(normalized, LoaderFolderName);
    }
}
