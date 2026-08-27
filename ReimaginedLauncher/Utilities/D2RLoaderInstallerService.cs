using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record D2RLoaderInstallProgress(string Message, double? Percentage = null);

public sealed class D2RLoaderInstallerService
{
    private const string DownloadUrl = "https://d2rloader.net/downloads/latest";
    private const string LoaderExecutableName = "D2RLoader.exe";
    private readonly HttpClient _httpClient;

    public D2RLoaderInstallerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("D2R-Reimagined-Launcher");
    }

    public async Task InstallAsync(
        string? installDirectory,
        IProgress<D2RLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (!InstallDirectoryValidator.IsValidInstallDirectory(normalized))
        {
            throw new InvalidOperationException(
                "Select the Diablo II: Resurrected folder that contains D2R.exe before installing D2RLoader.");
        }

        if (D2RLoaderService.IsInstalled(normalized))
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

            progress?.Report(new D2RLoaderInstallProgress("D2RLoader installed.", 100));
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
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
}
