using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Atomic, per-install reader/writer for <see cref="CascFastloadManifest"/>. The manifest lives
/// inside Reimagined.mpq/data/ so it sits next to the bytes it tracks; corrupt files are quarantined.
/// </summary>
public sealed class CascFastloadManifestService
{
    public const string ManifestRelativePath = "mods\\Reimagined\\Reimagined.mpq\\data\\.reimagined-fastload.json";

    private readonly string _manifestPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CascFastloadManifestService(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Install directory must be provided.", nameof(installDirectory));
        }

        _manifestPath = Path.Combine(installDirectory, ManifestRelativePath);
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

    /// <summary>
    /// Returns the existing entry for <paramref name="path"/> or
    /// <c>null</c>. Path comparison is ordinal-ignore-case (CASC paths are
    /// case-insensitive on the wire and on Windows; Linux preserves
    /// original casing but we still match insensitively to forgive
    /// hand-edited manifests).
    /// </summary>
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

    /// <summary>
    /// Inserts or replaces an entry by <see cref="CascFastloadEntry.Path"/>.
    /// Returns <c>true</c> if a new entry was added, <c>false</c> if an
    /// existing one was replaced.
    /// </summary>
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

    /// <summary>
    /// True when the build recorded in <paramref name="manifest"/> matches
    /// <paramref name="product"/>. Used as the cheap fast-path in the
    /// delta extract: equal builds means CKeys are guaranteed identical
    /// and we only need to verify the on-disk files still exist.
    /// </summary>
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
