using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Installing one of the launcher's own D2RLoader plugins and writing the
/// launch-time settings into its TOML config.
/// </summary>
/// <remarks>
/// Every bundled plugin needs the same handful of things: copy the DLL into the
/// mod's plugin folder, find the loader roots that carry it, and rewrite a few
/// scalars in its config while leaving the player's own edits alone. This holds
/// that shared behaviour so each plugin's own service is only the part that
/// differs - which settings it owns, and when it may be turned on.
///
/// The TOML handling is deliberately not a parser. The plugins read flat scalars
/// and take the first match, so upserting the first assignment of a key matches
/// what they will actually see, and comments and unrelated settings survive.
/// </remarks>
public sealed class D2RLoaderPluginPackage
{
    private const string LoaderFolderName = "d2rloader";
    private const string ModName = "Reimagined";

    private readonly string? _bundledPluginPathOverride;

    public D2RLoaderPluginPackage(
        string pluginId,
        string pluginFileName,
        string managedHeader,
        string? bundledPluginPath = null)
    {
        PluginId = pluginId;
        PluginFileName = pluginFileName;
        ManagedHeader = managedHeader;
        _bundledPluginPathOverride = bundledPluginPath;
    }

    public string PluginId { get; }

    public string PluginFileName { get; }

    /// <summary>Header written above a config this launcher creates from scratch.</summary>
    public string ManagedHeader { get; }

    /// <summary>
    /// The copy shipped inside the launcher build. A plugin's own plugin.json
    /// fixes its scope to the mod, so that is the only place D2RLoader will ever
    /// load it from - unlike the TOML config, installation never touches the
    /// global scope.
    /// </summary>
    public string BundledPluginPath => _bundledPluginPathOverride
        ?? Path.Combine(AppContext.BaseDirectory, "Assets", "D2RLoaderPlugins", PluginId, PluginFileName);

    public bool IsInstalled(string? installDirectory)
    {
        return ResolveInstalledRoots(installDirectory).Count > 0;
    }

    /// <summary>
    /// Whether the launcher's bundled copy is the exact file a ladder approved.
    /// The policy matches on content hash, so a rebuilt DLL is an unapproved DLL
    /// until the ladder's row is updated.
    /// </summary>
    public bool CanSupplyApproved(string fileName, string sha256, string? bundledPluginPath = null)
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
    /// Copies the bundled plugin into the mod's plugin folder if it is missing
    /// or out of date, so players never have to source the DLL themselves.
    /// Returns false only on a real failure - a copy that was already current is
    /// success, not a no-op to warn about.
    /// </summary>
    public Task<bool> EnsureInstalledAsync(string? installDirectory, string? bundledPluginPath = null)
    {
        var source = bundledPluginPath ?? BundledPluginPath;
        if (!File.Exists(source))
        {
            LaunchDiagnostics.Log($"{PluginId}: the bundled plugin is missing from the launcher build ({source}).");
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
            LaunchDiagnostics.Log($"{PluginId}: installed the bundled plugin to {target}.");
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"{PluginId}: could not install the plugin to {target}: {exception.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Rewrites the given scalars in every config this plugin owns.
    /// </summary>
    /// <param name="requireInstalled">
    /// True when the settings only make sense with the plugin present, so a
    /// missing plugin is a failure. False when clearing settings, where a config
    /// that was never written is a no-op rather than a problem.
    /// </param>
    public async Task<bool> WriteAsync(
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
                LaunchDiagnostics.Log($"{PluginId}: could not write {configPath}: {exception.Message}");
            }
        }

        return written == roots.Count;
    }

    /// <summary>
    /// Replaces the first assignment of <paramref name="key"/>, or appends one.
    /// The plugins read flat scalars and take the first match, so this matches
    /// what they will actually see while leaving comments and other settings
    /// alone.
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

    internal static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    internal static string NormalizeBaseUrl(string value)
    {
        return value.TrimEnd('/');
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

    private string GetConfigPath(string loaderRoot)
    {
        return Path.Combine(loaderRoot, "config", $"{PluginId}.toml");
    }

    /// <summary>Loader roots that actually carry the plugin DLL.</summary>
    private List<string> ResolveInstalledRoots(string? installDirectory)
    {
        return EnumerateRoots(installDirectory)
            .Where(root => File.Exists(Path.Combine(root, "plugins", PluginFileName)))
            .ToList();
    }

    /// <summary>Loader roots that already carry a config, installed or not.</summary>
    private List<string> ResolveConfiguredRoots(string? installDirectory)
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
