using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ModReleaseInstallProgress(string Message, double? Percentage = null);

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset> Assets);

internal sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

/// <summary>
/// Installs an exact Reimagined mod version straight from the project's GitHub
/// releases. Nexus Mods stays the route for normal play; this exists so a ladder
/// that requires a specific version can put the player on it without sending
/// them to a browser, a Nexus login, or a manual download.
/// </summary>
public sealed class ModReleaseInstallerService
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/D2R-Reimagined/d2r-reimagined-mod/releases";
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public ModReleaseInstallerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task InstallAsync(
        string? installDirectory,
        string requiredVersion,
        IProgress<ModReleaseInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (!InstallDirectoryValidator.IsValidInstallDirectory(normalized))
        {
            throw new InvalidOperationException(
                "Select the Diablo II: Resurrected folder that contains D2R.exe before installing the mod.");
        }
        if (string.IsNullOrWhiteSpace(requiredVersion))
        {
            throw new InvalidOperationException("The ladder did not specify which Reimagined version it requires.");
        }

        progress?.Report(new ModReleaseInstallProgress($"Looking up Reimagined {requiredVersion} on GitHub..."));
        var asset = await ResolveAssetAsync(requiredVersion.Trim(), cancellationToken);

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            "mod-installs",
            Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempDirectory, "Reimagined.zip");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await DownloadAsync(asset, archivePath, progress, cancellationToken);

            progress?.Report(new ModReleaseInstallProgress($"Installing Reimagined {requiredVersion}..."));
            await Task.Run(() => InstallArchive(archivePath, normalized!, cancellationToken), cancellationToken);

            var installed = ReadInstalledVersion(normalized!);
            if (!string.Equals(installed, requiredVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The GitHub release for {requiredVersion} installed version '{installed ?? "unknown"}'.");
            }

            progress?.Report(new ModReleaseInstallProgress($"Reimagined {requiredVersion} installed.", 100));
            LaunchDiagnostics.Log($"Installed Reimagined mod {requiredVersion} from GitHub release {asset.Name}.");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// Releases are tagged inconsistently across the project's history ("v3.0.10",
    /// "3.0.10"), and asset names carry their own decoration, so the version is
    /// matched against both the tag and the asset name rather than assuming one
    /// naming scheme.
    /// </summary>
    private async Task<GitHubReleaseAsset> ResolveAssetAsync(
        string requiredVersion,
        CancellationToken cancellationToken)
    {
        var releases = await _httpClient.GetFromJsonAsync<GitHubRelease[]>(
            ReleasesApiUrl + "?per_page=100",
            cancellationToken) ?? [];
        var release = releases.FirstOrDefault(item =>
            string.Equals(item.TagName, requiredVersion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.TagName, "v" + requiredVersion, StringComparison.OrdinalIgnoreCase));
        if (release is null)
        {
            throw new InvalidOperationException(
                $"No GitHub release publishes Reimagined {requiredVersion}. Install it manually, or ask an "
                + "administrator to point the ladder at a released version.");
        }

        var candidates = release.Assets
            .Where(item => item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var asset = candidates.Length == 1
            ? candidates[0]
            : candidates.FirstOrDefault(item => item.Name.Contains(requiredVersion, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            throw new InvalidOperationException(
                $"The GitHub release for Reimagined {requiredVersion} has no installable zip asset.");
        }
        if (asset.Size is <= 0 or > MaxArchiveBytes)
        {
            throw new InvalidDataException($"The Reimagined {requiredVersion} archive has an unexpected size.");
        }

        return asset;
    }

    private async Task DownloadAsync(
        GitHubReleaseAsset asset,
        string archivePath,
        IProgress<ModReleaseInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModReleaseInstallProgress("Downloading Reimagined from GitHub...", 0));
        using var response = await _httpClient.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(
                         archivePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                downloaded += read;
                if (downloaded > MaxArchiveBytes)
                {
                    throw new InvalidDataException("The Reimagined archive exceeded its allowed download size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(new ModReleaseInstallProgress(
                    "Downloading Reimagined from GitHub...",
                    downloaded * 100d / asset.Size));
            }
        }

        var length = new FileInfo(archivePath).Length;
        if (length != asset.Size)
        {
            throw new InvalidDataException("The downloaded Reimagined archive is not the size GitHub advertised.");
        }

        // GitHub publishes a digest for release assets. It is not a signature -
        // it comes from the same place as the bytes - but it turns a truncated
        // or corrupted 140 MB download into a clear error instead of a broken
        // install that only shows up as a failed ladder verification later.
        if (!string.IsNullOrWhiteSpace(asset.Digest)
            && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(archivePath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            var expected = asset.Digest["sha256:".Length..];
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded Reimagined archive failed its SHA-256 check.");
            }
        }
    }

    /// <summary>
    /// The release archive is rooted at the game folder and carries
    /// mods/Reimagined. The existing folder is replaced rather than merged so a
    /// downgrade cannot leave newer files behind, and the previous copy is kept
    /// next to it until the new one is in place.
    /// </summary>
    private static void InstallArchive(
        string archivePath,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(Path.GetDirectoryName(archivePath)!, "staging");
        D2RLoaderInstallerService.ExtractArchive(archivePath, stagingDirectory, cancellationToken);

        var sourceModDirectory = Directory
            .GetDirectories(stagingDirectory, "Reimagined", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                "mods",
                StringComparison.OrdinalIgnoreCase));
        if (sourceModDirectory is null)
        {
            throw new InvalidDataException("The Reimagined archive does not contain a mods/Reimagined folder.");
        }

        var targetModDirectory = Path.Combine(installDirectory, "mods", "Reimagined");
        var backupDirectory = Path.Combine(installDirectory, "mods", "Reimagined.backup");
        if (Directory.Exists(targetModDirectory))
        {
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }

            Directory.Move(targetModDirectory, backupDirectory);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetModDirectory)!);
            CopyDirectory(sourceModDirectory, targetModDirectory, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(targetModDirectory))
            {
                Directory.Delete(targetModDirectory, recursive: true);
            }
            if (Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetModDirectory);
            }

            throw;
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    internal static string? ReadInstalledVersion(string installDirectory)
    {
        var modRoot = Path.Combine(installDirectory, "mods", "Reimagined");
        return ReadJsonString(Path.Combine(modRoot, "modinfo.json"), "version")
               ?? ReadJsonString(Path.Combine(modRoot, "Reimagined.mpq", "modinfo.json"), "version");
    }

    private static string? ReadJsonString(string path, string propertyName)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"Could not remove mod install temp directory: {exception.Message}");
        }
    }
}
