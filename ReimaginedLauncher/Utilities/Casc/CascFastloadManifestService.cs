using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Atomic, per-install reader/writer for <see cref="CascFastloadManifest"/>. The manifest is stored
/// under <c>%AppData%\ReimaginedLauncher\Casc\</c>, keyed by profile type + install-path hash so
/// users cannot corrupt it by editing the game directory; corrupt files are quarantined in-place.
/// </summary>
public sealed class CascFastloadManifestService
{
    /// <summary>Subdirectory under <see cref="SettingsManager.AppDirectoryPath"/> that holds per-install manifests.</summary>
    public const string ManifestSubdirectory = "Casc";

    private readonly string _manifestPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CascFastloadManifestService(InstallationType type, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Install directory must be provided.", nameof(installDirectory));
        }

        _manifestPath = BuildManifestPath(type, installDirectory);
    }

    /// <summary>
    /// Resolves the manifest file location for a given profile type + install directory pair.
    /// File name shape: <c>{type}-{8 hex chars of SHA1(install dir)}.json</c>; collisions across
    /// the handful of installs a single user has are vanishingly unlikely at 8 hex chars.
    /// </summary>
    public static string BuildManifestPath(InstallationType type, string installDirectory)
    {
        var normalized = (installDirectory ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        var key = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        var typeToken = type.ToString().ToLowerInvariant();
        return Path.Combine(
            SettingsManager.AppDirectoryPath,
            ManifestSubdirectory,
            $"{typeToken}-{key}.json");
    }

    /// <summary>Absolute path to the manifest file (may not exist yet).</summary>
    public string ManifestPath => _manifestPath;

    /// <summary>True when a manifest file exists on disk for this install.</summary>
    public bool Exists => File.Exists(_manifestPath);

    /// <summary>
    /// Loads the manifest, returning a fresh empty one when the file does
    /// not exist yet. Quarantines and replaces a corrupt manifest rather
    /// than throwing, so a single bad write cannot brick fastload.
    /// </summary>
    public async Task<CascFastloadManifest> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists <paramref name="manifest"/> atomically. Bumps
    /// <see cref="CascFastloadManifest.LastUpdatedUtc"/> to "now" so the
    /// stored timestamp always reflects the on-disk state, never the
    /// in-memory edits the caller may have made earlier.
    /// </summary>
    public async Task SaveAsync(CascFastloadManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            manifest.LastUpdatedUtc = DateTime.UtcNow;
            SaveUnsafe(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Convenience read-modify-write helper. The supplied delegate may
    /// freely mutate the manifest; the new state is written back
    /// atomically. Returns the manifest as written.
    /// </summary>
    public async Task<CascFastloadManifest> UpdateAsync(
        Action<CascFastloadManifest> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = LoadUnsafe();
            mutator(manifest);
            manifest.LastUpdatedUtc = DateTime.UtcNow;
            SaveUnsafe(manifest);
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the entry for <paramref name="path"/> or <c>null</c>; case-insensitive comparison.</summary>
    public static CascFastloadEntry? FindEntry(CascFastloadManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var entry = manifest.Files[i];
            if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Inserts or replaces by <see cref="CascFastloadEntry.Path"/>; returns <c>true</c> when added, <c>false</c> when replaced.</summary>
    public static bool AddOrUpdate(CascFastloadManifest manifest, CascFastloadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entry);

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            if (!string.Equals(manifest.Files[i].Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            manifest.Files[i] = entry;
            return false;
        }

        manifest.Files.Add(entry);
        return true;
    }

    /// <summary>Removes an entry by path. Returns <c>true</c> when one was removed.</summary>
    public static bool Remove(CascFastloadManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        for (var i = manifest.Files.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(manifest.Files[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            manifest.Files.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>True when manifest's build matches <paramref name="product"/>; delta extract fast-path that only re-verifies on-disk presence.</summary>
    public static bool BuildMatches(CascFastloadManifest manifest, CascStorageProduct product)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(product);

        return manifest.BuildNumber == product.BuildNumber
               && string.Equals(manifest.BuildName, product.CodeName, StringComparison.OrdinalIgnoreCase);
    }

    private CascFastloadManifest LoadUnsafe()
    {
        if (!File.Exists(_manifestPath))
        {
            return new CascFastloadManifest();
        }

        try
        {
            var json = File.ReadAllText(_manifestPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CascFastloadManifest();
            }

            var manifest = JsonSerializer.Deserialize<CascFastloadManifest>(
                json, SerializerOptions.PropertyNameCaseInsensitive);

            if (manifest is null)
            {
                return new CascFastloadManifest();
            }

            if (manifest.Schema > CascFastloadManifest.CurrentSchema)
            {
                // Newer schema than this launcher understands: treat as
                // unreadable rather than silently mis-interpreting it.
                LaunchDiagnostics.LogException(
                    $"CASC fastload manifest schema {manifest.Schema} is newer than supported "
                    + $"({CascFastloadManifest.CurrentSchema}); quarantining and starting fresh.",
                    new InvalidDataException("Unsupported manifest schema."));
                TryQuarantineCorruptManifest();
                return new CascFastloadManifest();
            }

            manifest.Files ??= new List<CascFastloadEntry>();
            return manifest;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to read CASC fastload manifest", ex);
            TryQuarantineCorruptManifest();
            return new CascFastloadManifest();
        }
    }

    private void SaveUnsafe(CascFastloadManifest manifest)
    {
        var directory = Path.GetDirectoryName(_manifestPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(manifest, SerializerOptions.Indented);

        // Mirror PluginAssetBackupService: temp + Move(overwrite) so a
        // crash mid-write cannot leave a truncated manifest behind.
        var tempPath = _manifestPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _manifestPath, overwrite: true);
    }

    private void TryQuarantineCorruptManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath))
            {
                return;
            }

            var quarantine = _manifestPath + $".bad-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            File.Move(_manifestPath, quarantine, overwrite: true);
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException(
                "Failed to quarantine corrupt CASC fastload manifest", ex);
        }
    }
}
