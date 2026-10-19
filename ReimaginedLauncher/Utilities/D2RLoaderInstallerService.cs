using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record D2RLoaderInstallProgress(string Message, double? Percentage = null);
public sealed record D2RLoaderUpdateCheckResult(
    string InstalledVersion,
    string LatestVersion,
    bool IsUpdateAvailable);

public sealed class D2RLoaderInstallerService
{
    private const string DownloadUrl = "https://d2rloader.net/downloads/latest";
    private const string VersionUrl = "https://d2rloader.net/api/v1/version";
    private const string LoaderExecutableName = "D2RLoader.exe";
    private readonly HttpClient _httpClient;

    public D2RLoaderInstallerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("D2R-Reimagined-Launcher");
    }

    /// <summary>
    /// Downloads and installs D2RLoader. Pass <paramref name="minimumVersion"/>
    /// to also upgrade an install that is already present but older than a
    /// ladder requires - without it an existing install is always left alone.
    /// Pass <paramref name="forceDownload"/> after a published-version check
    /// to replace an older installed loader with the latest download.
    /// Only the latest published build can be fetched, so an install that is
    /// still too old afterwards is reported rather than retried.
    /// </summary>
    public async Task InstallAsync(
        string? installDirectory,
        IProgress<D2RLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? minimumVersion = null,
        bool forceDownload = false)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (!InstallDirectoryValidator.IsValidInstallDirectory(normalized))
        {
            throw new InvalidOperationException(
                "Select the Diablo II: Resurrected folder that contains D2R.exe before installing D2RLoader.");
        }

        if (!forceDownload
            && D2RLoaderService.IsInstalled(normalized)
            && SatisfiesMinimum(normalized!, minimumVersion))
        {
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            "d2rloader-installs",
            Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempDirectory, "D2RLoader.zip");
        var stagingDirectory = Path.Combine(tempDirectory, "staging");

        Directory.CreateDirectory(tempDirectory);

        try
        {
            progress?.Report(new D2RLoaderInstallProgress("Connecting to d2rloader.net..."));
            using var response = await _httpClient.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
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
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
                    double? percentage = contentLength is > 0
                        ? downloaded * 100d / contentLength.Value
                        : null;
                    progress?.Report(new D2RLoaderInstallProgress("Downloading D2RLoader...", percentage));
                }
            }

            progress?.Report(new D2RLoaderInstallProgress("Extracting D2RLoader..."));
            await Task.Run(
                () => ExtractArchive(archivePath, stagingDirectory, cancellationToken),
                cancellationToken);

            progress?.Report(new D2RLoaderInstallProgress("Installing D2RLoader..."));
            await Task.Run(
                () => InstallExtractedFiles(stagingDirectory, normalized!, cancellationToken),
                cancellationToken);

            if (!D2RLoaderService.IsInstalled(normalized))
            {
                throw new InvalidDataException("The downloaded archive did not install D2RLoader.exe.");
            }
            if (!SatisfiesMinimum(normalized!, minimumVersion))
            {
                throw new InvalidDataException(
                    $"The latest published D2RLoader is older than the {minimumVersion} this ladder requires.");
            }

            progress?.Report(new D2RLoaderInstallProgress("D2RLoader installed.", 100));
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<D2RLoaderUpdateCheckResult> CheckForUpdateAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var inventory = D2RLoaderService.Discover(installDirectory);
        if (!inventory.IsInstalled)
        {
            throw new InvalidOperationException("D2RLoader is not installed.");
        }

        if (string.IsNullOrWhiteSpace(inventory.Version))
        {
            throw new InvalidDataException("The installed D2RLoader version could not be read.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _httpClient.GetAsync(VersionUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        var published = await JsonSerializer.DeserializeAsync<D2RLoaderVersionResponse>(
            content,
            cancellationToken: timeout.Token);
        if (string.IsNullOrWhiteSpace(published?.Version))
        {
            throw new InvalidDataException("The D2RLoader version service returned an invalid response.");
        }

        if (!TryCompareSemanticVersions(published.Version, inventory.Version, out var comparison))
        {
            throw new InvalidDataException(
                $"D2RLoader version information could not be compared (installed: {inventory.Version}, published: {published.Version}).");
        }

        return new D2RLoaderUpdateCheckResult(
            inventory.Version,
            published.Version,
            comparison > 0);
    }

    internal static bool IsUpdateAvailable(string? installedVersion, string? latestVersion) =>
        TryCompareSemanticVersions(latestVersion, installedVersion, out var comparison) && comparison > 0;

    private static bool TryCompareSemanticVersions(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParseSemanticVersion(left, out var leftCore, out var leftPrerelease)
            || !TryParseSemanticVersion(right, out var rightCore, out var rightPrerelease))
        {
            return false;
        }

        comparison = leftCore.CompareTo(rightCore);
        if (comparison != 0)
        {
            return true;
        }

        comparison = ComparePrerelease(leftPrerelease, rightPrerelease);
        return true;
    }

    private static bool TryParseSemanticVersion(
        string? value,
        out Version core,
        out string[]? prerelease)
    {
        core = new Version(0, 0, 0, 0);
        prerelease = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var withoutBuild = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        if (!Version.TryParse(parts[0], out var parsed)
            || parsed.Major < 0
            || parsed.Minor < 0)
        {
            return false;
        }

        core = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        if (parts.Length == 2)
        {
            if (string.IsNullOrWhiteSpace(parts[1]))
            {
                return false;
            }

            prerelease = parts[1].Split('.');
        }

        return true;
    }

    private static int ComparePrerelease(string[]? left, string[]? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }
        if (right is null)
        {
            return -1;
        }

        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftIsNumeric = long.TryParse(left[index], out var leftNumber);
            var rightIsNumeric = long.TryParse(right[index], out var rightNumber);
            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.Compare(left[index], right[index], StringComparison.Ordinal);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool SatisfiesMinimum(string installDirectory, string? minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumVersion)
            || !LadderBundleService.TryParseVersionCore(minimumVersion, out var minimum))
        {
            return true;
        }

        var loaderPath = Path.Combine(installDirectory, LoaderExecutableName);
        if (!File.Exists(loaderPath))
        {
            return false;
        }

        var installed = FileVersionInfo.GetVersionInfo(loaderPath).FileVersion;
        return LadderBundleService.TryParseVersionCore(installed, out var current) && current >= minimum;
    }

    internal static void ExtractArchive(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory)
                              + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryPath));
            if (!destinationPath.StartsWith(destinationRoot, comparison))
            {
                throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    internal static void InstallExtractedFiles(
        string stagingDirectory,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        var loaderCandidates = Directory.GetFiles(
            stagingDirectory,
            LoaderExecutableName,
            SearchOption.AllDirectories);
        if (loaderCandidates.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one {LoaderExecutableName} in the downloaded archive, but found {loaderCandidates.Length}.");
        }

        var sourceRoot = Path.GetDirectoryName(loaderCandidates[0])!;
        var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => string.Equals(Path.GetFileName(path), LoaderExecutableName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var sourcePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(installDirectory, relativePath);
            if (File.Exists(destinationPath) && IsUserManagedLoaderPath(relativePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static bool IsUserManagedLoaderPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("d2rloader/config/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("d2rloader/plugins/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("d2rloader/patches/", StringComparison.OrdinalIgnoreCase);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record D2RLoaderVersionResponse(
        [property: JsonPropertyName("version")] string? Version);
}
