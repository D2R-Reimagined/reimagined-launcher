using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LadderBundleReadiness(
    bool IsReady,
    bool CanRepair,
    string Status,
    IReadOnlyList<string> Problems,
    bool IsInstalled = false);

/// <summary>
/// Percentage is null for steps that cannot report one. Callers should fall
/// back to an indeterminate indicator rather than showing zero.
/// </summary>
public sealed record LadderBundleProgress(string Message, double? Percentage = null);

internal sealed record InstalledLadderBundleState(
    Guid BundleId,
    int Revision,
    string ArtifactSha256,
    string ManifestSha256,
    string ManifestBase64,
    string ManifestSignature,
    IReadOnlyList<InstalledLadderBundleFile> Files,
    DateTimeOffset InstalledAtUtc);

internal sealed record InstalledLadderBundleFile(string TargetPath, string Sha256, long SizeBytes);

internal sealed record DownloadedLadderBundle(
    LadderBundleManifest Manifest,
    byte[] ManifestBytes,
    IReadOnlyDictionary<string, byte[]> Files);

internal sealed record LadderBundleManifest(
    int SchemaVersion,
    Guid BundleId,
    Guid LadderId,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    string SourceCommit,
    LadderBundleCompatibility Compatibility,
    IReadOnlyList<LadderBundleManifestFile> Files);

public sealed class LadderBundleService(
    ReimaginedApiHttpClient apiClient,
    D2RLoaderInstallerService loaderInstaller,
    ModReleaseInstallerService modInstaller)
{
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxBundleBytes = 64L * 1024 * 1024;
    private const string StateFileName = "ladder-bundle-state.json";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Test seam for signing with a throwaway key. Nothing in the application
    /// sets this, so a shipped launcher only ever trusts the keys it carries.
    /// </summary>
    internal static string? TrustedKeyOverridePem { get; set; }

    public async Task<LadderBundleReadiness> GetReadinessAsync(
        string? installDirectory,
        LadderBundleResponse? bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            return new LadderBundleReadiness(false, false, "No signed ladder bundle is active.", ["The ladder is using its legacy extension policy."]);
        }

        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new LadderBundleReadiness(false, false, "Select a valid D2R installation.", ["The install directory is unavailable."]);
        }

        var problems = await GetCompatibilityProblemsAsync(normalized, bundle, cancellationToken);
        if (!TryLoadTrustedKey(bundle.SigningKeyId, out var readinessKey, out var keyProblem))
        {
            problems.Add(keyProblem!);
        }
        else
        {
            readinessKey.Dispose();
        }

        var state = await ReadStateAsync(normalized, cancellationToken);
        if (state is null)
        {
            problems.Add("The active ladder bundle has not been installed.");
        }
        else if (state.BundleId != bundle.Id
                 || state.Revision != bundle.Revision
                 || !string.Equals(state.ArtifactSha256, bundle.ArtifactSha256, StringComparison.OrdinalIgnoreCase)
                 || !string.Equals(state.ManifestSha256, bundle.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Installed bundle r{state.Revision} does not match active bundle r{bundle.Revision}.");
        }
        else
        {
            var manifestProblem = VerifyInstalledManifest(state, bundle);
            if (manifestProblem is not null)
            {
                problems.Add(manifestProblem);
            }

            var stateMatchesDescriptor = state.Files.Count == bundle.Files.Count
                                         && bundle.Files.All(expected => state.Files.Any(installed =>
                                             string.Equals(installed.TargetPath, expected.TargetPath, StringComparison.OrdinalIgnoreCase)
                                             && string.Equals(installed.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                                             && installed.SizeBytes == expected.SizeBytes));
            if (!stateMatchesDescriptor)
            {
                problems.Add("The installed ladder bundle state does not match the signed API descriptor.");
            }

            foreach (var file in bundle.Files)
            {
                var path = ResolveTargetPath(normalized, file.TargetPath);
                if (!File.Exists(path))
                {
                    // An optional file the player unchecked was moved to
                    // ladder-disabled by the policy step of the previous launch.
                    // Treating that as damage would redownload and reinstall the
                    // bundle on every single launch. Required files must be here.
                    if (file.IsRequired)
                    {
                        problems.Add($"{Path.GetFileName(path)} is missing.");
                    }

                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length != file.SizeBytes
                    || !string.Equals(await Sha256Async(path, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{Path.GetFileName(path)} was modified after installation.");
                }
            }
        }

        if (problems.Count == 0)
        {
            return new LadderBundleReadiness(
                true,
                true,
                $"Signed ladder bundle r{bundle.Revision} is installed and verified ({bundle.Files.Count} files).",
                [],
                IsInstalled: true);
        }

        // D2RLoader, D2RCore and the mod are all things the launcher can fetch,
        // so they are repair work, not dead ends. What is left blocks because
        // nothing here can fix it: the player's game build is Blizzard's, the
        // launcher cannot replace itself mid-check, and a missing signing key
        // means this launcher build shipped wrong.
        var canRepair = problems.All(problem => !problem.StartsWith("Launcher ", StringComparison.Ordinal)
                                               && !problem.StartsWith("D2R game", StringComparison.Ordinal)
                                               && !problem.StartsWith("No trusted", StringComparison.Ordinal));
        return new LadderBundleReadiness(false, canRepair, string.Join(" ", problems), problems, state is not null);
    }

    public async Task InstallOrRepairAsync(
        string? installDirectory,
        LadderBundleResponse bundle,
        IProgress<LadderBundleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory)
            ?? throw new InvalidOperationException("Select a valid D2R installation before installing a ladder bundle.");
        var compatibilityProblems = await GetCompatibilityProblemsAsync(normalized, bundle, cancellationToken);
        if (compatibilityProblems.Count > 0)
        {
            await InstallPrerequisitesAsync(normalized, bundle, compatibilityProblems, progress, cancellationToken);
            compatibilityProblems = await GetCompatibilityProblemsAsync(normalized, bundle, cancellationToken);
        }
        if (compatibilityProblems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", compatibilityProblems));
        }

        var downloadMessage = $"Downloading signed ladder package r{bundle.Revision}...";
        progress?.Report(new LadderBundleProgress(downloadMessage, 0));
        var archiveBytes = await apiClient.DownloadLadderBundleAsync(
            bundle,
            new Progress<double>(percent => progress?.Report(new LadderBundleProgress(downloadMessage, percent))),
            cancellationToken);
        if (archiveBytes.LongLength != bundle.ArtifactSizeBytes
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(archiveBytes)),
                bundle.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded ladder bundle failed its archive SHA-256 check.");
        }

        progress?.Report(new LadderBundleProgress("Verifying ladder package signature and contents..."));
        var downloaded = VerifyArchive(bundle, archiveBytes);
        progress?.Report(new LadderBundleProgress("Installing ladder package..."));
        await InstallVerifiedAsync(normalized, bundle, downloaded, cancellationToken);

        var readiness = await GetReadinessAsync(normalized, bundle, cancellationToken);
        if (!readiness.IsReady)
        {
            throw new InvalidDataException("The installed ladder bundle did not pass final verification: " + readiness.Status);
        }

        LaunchDiagnostics.Log($"Installed signed ladder bundle {bundle.Id} revision {bundle.Revision}.");
    }

    public static bool CanSupplyApproval(
        LadderBundleResponse? bundle,
        string fileName,
        string sha256,
        D2RLoaderExtensionKind kind)
    {
        return bundle?.Files.Any(file =>
            file.Kind == kind
            && string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.Sha256, sha256.Trim(), StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static string LauncherVersion =>
        typeof(LadderBundleService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static DownloadedLadderBundle VerifyArchive(LadderBundleResponse bundle, byte[] archiveBytes)
    {
        if (archiveBytes.LongLength > MaxBundleBytes)
        {
            throw new InvalidDataException("The ladder bundle exceeds the 64 MiB limit.");
        }

        using var bundleStream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(bundleStream, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The ladder bundle does not contain manifest.json.");
        var signatureEntry = archive.GetEntry("manifest.sig")
            ?? throw new InvalidDataException("The ladder bundle does not contain manifest.sig.");
        if (manifestEntry.Length is <= 0 or > MaxManifestBytes || signatureEntry.Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("The ladder bundle manifest or signature has an invalid size.");
        }

        var manifestBytes = ReadEntry(manifestEntry, MaxManifestBytes);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        if (!string.Equals(manifestHash, bundle.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The ladder bundle manifest SHA-256 does not match the API descriptor.");
        }

        var signatureText = Encoding.ASCII.GetString(ReadEntry(signatureEntry, 4096)).Trim();
        if (!string.Equals(signatureText, bundle.ManifestSignature, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The ladder bundle signature does not match the API descriptor.");
        }

        if (!TryLoadTrustedKey(bundle.SigningKeyId, out var publicKey, out var keyProblem))
        {
            throw new InvalidDataException(keyProblem);
        }

        using (publicKey)
        {
            if (!publicKey.VerifyData(manifestBytes, Convert.FromBase64String(signatureText), HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException("The ladder bundle manifest signature is invalid.");
            }
        }

        var manifest = JsonSerializer.Deserialize<LadderBundleManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("The ladder bundle manifest could not be parsed.");
        ValidateManifestDescriptor(bundle, manifest);

        var expectedEntries = manifest.Files.Select(file => file.ArchivePath).ToHashSet(StringComparer.Ordinal);
        expectedEntries.Add("manifest.json");
        expectedEntries.Add("manifest.sig");
        var actualEntries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .ToArray();
        if (actualEntries.Length != expectedEntries.Count
            || actualEntries.Any(entry => !expectedEntries.Contains(entry)))
        {
            throw new InvalidDataException("The ladder bundle contains files not declared by its signed manifest.");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.ArchivePath);
            ValidateRelativePath(file.TargetPath);
            var entry = archive.GetEntry(file.ArchivePath)
                ?? throw new InvalidDataException($"The ladder bundle is missing {file.ArchivePath}.");
            if (entry.Length != file.SizeBytes || entry.Length is <= 0 or > 16L * 1024 * 1024)
            {
                throw new InvalidDataException($"{file.FileName} has an invalid size.");
            }

            var content = ReadEntry(entry, 16L * 1024 * 1024);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(content)),
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{file.FileName} failed its signed SHA-256 check.");
            }

            files.Add(file.TargetPath, content);
        }

        return new DownloadedLadderBundle(manifest, manifestBytes, files);
    }

    private static async Task InstallVerifiedAsync(
        string installDirectory,
        LadderBundleResponse bundle,
        DownloadedLadderBundle downloaded,
        CancellationToken cancellationToken)
    {
        var managementRoot = Path.Combine(installDirectory, ".reimagined-launcher", "ladder-bundles");
        var transactionId = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stagingRoot = Path.Combine(managementRoot, "staging-" + transactionId);
        var backupRoot = Path.Combine(managementRoot, "backups", transactionId);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);

        var previous = await ReadStateAsync(installDirectory, cancellationToken);
        var targetPaths = (previous?.Files.Select(file => file.TargetPath) ?? [])
            .Concat(downloaded.Manifest.Files.Select(file => file.TargetPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var backedUp = new List<string>();
        var installed = new List<string>();
        try
        {
            foreach (var file in downloaded.Manifest.Files)
            {
                var staged = ResolveUnderRoot(stagingRoot, file.TargetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, downloaded.Files[file.TargetPath], cancellationToken);
            }

            foreach (var relativePath in targetPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveTargetPath(installDirectory, relativePath);
                if (!File.Exists(target))
                {
                    continue;
                }

                var backup = ResolveUnderRoot(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup, overwrite: true);
                backedUp.Add(relativePath);
            }

            foreach (var file in downloaded.Manifest.Files)
            {
                var staged = ResolveUnderRoot(stagingRoot, file.TargetPath);
                var target = ResolveTargetPath(installDirectory, file.TargetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staged, target, overwrite: false);
                installed.Add(file.TargetPath);
            }

            var state = new InstalledLadderBundleState(
                bundle.Id,
                bundle.Revision,
                bundle.ArtifactSha256,
                bundle.ManifestSha256,
                Convert.ToBase64String(downloaded.ManifestBytes),
                bundle.ManifestSignature,
                downloaded.Manifest.Files.Select(file => new InstalledLadderBundleFile(
                    file.TargetPath,
                    file.Sha256,
                    file.SizeBytes)).ToArray(),
                DateTimeOffset.UtcNow);
            await WriteStateAsync(installDirectory, state, cancellationToken);
        }
        catch
        {
            foreach (var relativePath in installed)
            {
                var target = ResolveTargetPath(installDirectory, relativePath);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            foreach (var relativePath in backedUp.AsEnumerable().Reverse())
            {
                var backup = ResolveUnderRoot(backupRoot, relativePath);
                var target = ResolveTargetPath(installDirectory, relativePath);
                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target, overwrite: true);
                }
            }

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"Could not remove ladder bundle staging directory: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Brings the two dependencies the launcher can actually fetch up to what
    /// the signed policy asks for, so a player who picks a ladder is not handed
    /// a list of downloads to go and find. The game version is deliberately not
    /// remediated - nobody but Blizzard ships that - and the caller re-checks
    /// compatibility afterwards rather than trusting these to have worked.
    /// </summary>
    private async Task InstallPrerequisitesAsync(
        string installDirectory,
        LadderBundleResponse bundle,
        IReadOnlyList<string> problems,
        IProgress<LadderBundleProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (problems.Any(problem => problem.StartsWith("D2RLoader", StringComparison.Ordinal)
                                    || problem.StartsWith("D2RCore", StringComparison.Ordinal)))
        {
            progress?.Report(new LadderBundleProgress(
                $"Installing D2RLoader {bundle.Compatibility.RequiredD2RLoaderVersion}..."));
            await loaderInstaller.InstallAsync(
                installDirectory,
                new Progress<D2RLoaderInstallProgress>(update =>
                    progress?.Report(new LadderBundleProgress(update.Message, update.Percentage))),
                cancellationToken,
                bundle.Compatibility.RequiredD2RLoaderVersion);
        }

        if (problems.Any(problem => problem.StartsWith("Reimagined mod", StringComparison.Ordinal)))
        {
            progress?.Report(new LadderBundleProgress(
                $"Installing Reimagined {bundle.Compatibility.RequiredModVersion}..."));
            await modInstaller.InstallAsync(
                installDirectory,
                bundle.Compatibility.RequiredModVersion,
                new Progress<ModReleaseInstallProgress>(update =>
                    progress?.Report(new LadderBundleProgress(update.Message, update.Percentage))),
                cancellationToken);
        }
    }

    private static async Task<List<string>> GetCompatibilityProblemsAsync(
        string installDirectory,
        LadderBundleResponse bundle,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (TryParseVersionCore(bundle.Compatibility.MinimumLauncherVersion, out var requiredLauncher)
            && TryParseVersionCore(LauncherVersion, out var currentLauncher)
            && currentLauncher < requiredLauncher)
        {
            problems.Add($"Launcher {bundle.Compatibility.MinimumLauncherVersion} or newer is required; {LauncherVersion} is installed.");
        }

        var loaderPath = Path.Combine(installDirectory, "D2RLoader.exe");
        if (!File.Exists(loaderPath))
        {
            problems.Add("D2RLoader is not installed.");
        }
        else
        {
            var loaderVersion = FileVersionInfo.GetVersionInfo(loaderPath).FileVersion;
            if (TryParseVersionCore(bundle.Compatibility.RequiredD2RLoaderVersion, out var requiredLoader)
                && requiredLoader > new Version(0, 0, 0)
                && (!TryParseVersionCore(loaderVersion, out var currentLoader) || currentLoader < requiredLoader))
            {
                problems.Add($"D2RLoader {bundle.Compatibility.RequiredD2RLoaderVersion} or newer is required; {loaderVersion ?? "unknown"} is installed.");
            }
            if (!string.IsNullOrWhiteSpace(bundle.Compatibility.RequiredD2RLoaderSha256)
                && !string.Equals(
                    await Sha256Async(loaderPath, cancellationToken),
                    bundle.Compatibility.RequiredD2RLoaderSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("D2RLoader does not match the required signed policy hash.");
            }
        }

        var corePath = Path.Combine(installDirectory, "D2RCore.dll");
        if (!string.IsNullOrWhiteSpace(bundle.Compatibility.RequiredD2RCoreSha256)
            && (!File.Exists(corePath)
                || !string.Equals(
                    await Sha256Async(corePath, cancellationToken),
                    bundle.Compatibility.RequiredD2RCoreSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("D2RCore does not match the required signed policy hash.");
        }

        if (!string.IsNullOrWhiteSpace(bundle.Compatibility.RequiredModVersion))
        {
            var installedModVersion = ReadInstalledModVersion(installDirectory);
            if (!string.Equals(
                    installedModVersion,
                    bundle.Compatibility.RequiredModVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"Reimagined mod {bundle.Compatibility.RequiredModVersion} is required; "
                    + $"{installedModVersion ?? "no installed mod"} was detected.");
            }
        }

        var gamePath = Path.Combine(installDirectory, "D2R.exe");
        if (!string.IsNullOrWhiteSpace(bundle.Compatibility.SupportedGameVersion)
            && bundle.Compatibility.SupportedGameVersion != "*"
            && File.Exists(gamePath))
        {
            var gameVersion = FileVersionInfo.GetVersionInfo(gamePath).FileVersion ?? string.Empty;
            if (!gameVersion.StartsWith(bundle.Compatibility.SupportedGameVersion, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"D2R game version {bundle.Compatibility.SupportedGameVersion} is required; {gameVersion} is installed.");
            }
        }

        return problems;
    }

    private static void ValidateManifestDescriptor(LadderBundleResponse bundle, LadderBundleManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || manifest.BundleId != bundle.Id
            || manifest.LadderId != bundle.LadderId
            || manifest.Revision != bundle.Revision
            || !string.Equals(manifest.SourceCommit, bundle.SourceCommit, StringComparison.Ordinal)
            || !CompatibilityMatches(manifest.Compatibility, bundle.Compatibility)
            || manifest.Files.Count != bundle.Files.Count)
        {
            throw new InvalidDataException("The signed manifest does not match the API bundle descriptor.");
        }

        foreach (var expected in bundle.Files)
        {
            var actual = manifest.Files.SingleOrDefault(file => file.PluginReleaseId == expected.PluginReleaseId);
            if (actual is null
                || actual.Kind != expected.Kind
                || actual.IsRequired != expected.IsRequired
                || !string.Equals(actual.PluginId, expected.PluginId, StringComparison.Ordinal)
                || !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(actual.Version, expected.Version, StringComparison.Ordinal)
                || !string.Equals(actual.ArchivePath, expected.ArchivePath, StringComparison.Ordinal)
                || !string.Equals(actual.TargetPath, expected.TargetPath, StringComparison.Ordinal)
                || !string.Equals(actual.FileName, expected.FileName, StringComparison.Ordinal)
                || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                || actual.SizeBytes != expected.SizeBytes)
            {
                throw new InvalidDataException($"The signed manifest entry for {expected.PluginId} does not match the API descriptor.");
            }
        }
    }

    private static bool CompatibilityMatches(
        LadderBundleCompatibility actual,
        LadderBundleCompatibility expected)
    {
        return string.Equals(actual.MinimumLauncherVersion, expected.MinimumLauncherVersion, StringComparison.Ordinal)
               && string.Equals(actual.RequiredD2RLoaderVersion, expected.RequiredD2RLoaderVersion, StringComparison.Ordinal)
               && string.Equals(actual.RequiredD2RLoaderSha256, expected.RequiredD2RLoaderSha256, StringComparison.OrdinalIgnoreCase)
               && string.Equals(actual.RequiredD2RCoreSha256, expected.RequiredD2RCoreSha256, StringComparison.OrdinalIgnoreCase)
               && string.Equals(actual.RequiredModVersion, expected.RequiredModVersion, StringComparison.Ordinal)
               && string.Equals(actual.SupportedGameVersion, expected.SupportedGameVersion, StringComparison.Ordinal);
    }

    private static string? VerifyInstalledManifest(
        InstalledLadderBundleState state,
        LadderBundleResponse bundle)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(state.ManifestBase64)
                || !string.Equals(state.ManifestSignature, bundle.ManifestSignature, StringComparison.Ordinal))
            {
                return "The installed signed manifest state is missing or was modified.";
            }

            var manifestBytes = Convert.FromBase64String(state.ManifestBase64);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(manifestBytes)),
                    bundle.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "The installed signed manifest no longer matches its SHA-256.";
            }

            if (!TryLoadTrustedKey(bundle.SigningKeyId, out var publicKey, out var keyProblem))
            {
                return keyProblem;
            }

            using (publicKey)
            {
                if (!publicKey.VerifyData(
                        manifestBytes,
                        Convert.FromBase64String(state.ManifestSignature),
                        HashAlgorithmName.SHA256))
                {
                    return "The installed ladder manifest signature is invalid.";
                }
            }

            var manifest = JsonSerializer.Deserialize<LadderBundleManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("The installed ladder manifest could not be parsed.");
            ValidateManifestDescriptor(bundle, manifest);
            return null;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException or InvalidDataException)
        {
            return "The installed signed manifest could not be verified: " + exception.Message;
        }
    }

    private static bool TryLoadTrustedKey(string keyId, out ECDsa publicKey, out string? problem)
    {
        publicKey = ECDsa.Create();
        problem = null;
        try
        {
            var pem = TrustedKeyOverridePem;
#if DEBUG
            // Development only. A shipped launcher must never let the machine it
            // runs on choose which key it trusts - that is a one-variable bypass
            // of the whole signing chain for anyone testing their own bundles.
            if (string.IsNullOrWhiteSpace(pem))
            {
                pem = Environment.GetEnvironmentVariable("D2R_REIMAGINED_BUNDLE_PUBLIC_KEY_PEM")
                    ?.Replace("\\n", "\n", StringComparison.Ordinal);
            }
            if (string.IsNullOrWhiteSpace(pem))
            {
                var configuredPath = Environment.GetEnvironmentVariable("D2R_REIMAGINED_BUNDLE_PUBLIC_KEY_PATH");
                if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                {
                    pem = File.ReadAllText(configuredPath);
                }
            }
#endif
            if (string.IsNullOrWhiteSpace(pem))
            {
                var safeKeyId = string.Concat(keyId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
                if (!string.Equals(safeKeyId, keyId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The bundle signing key ID is invalid.");
                }

                var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LadderBundleSigningKeys", safeKeyId + ".pem");
                if (File.Exists(assetPath))
                {
                    pem = File.ReadAllText(assetPath);
                }
            }
            if (string.IsNullOrWhiteSpace(pem))
            {
                problem = $"No trusted public key is installed for ladder bundle signing key '{keyId}'.";
                publicKey.Dispose();
                publicKey = null!;
                return false;
            }

            publicKey.ImportFromPem(pem);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            problem = $"No trusted public key could be loaded for '{keyId}': {exception.Message}";
            publicKey.Dispose();
            publicKey = null!;
            return false;
        }
    }

    private static async Task<InstalledLadderBundleState?> ReadStateAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var path = GetStatePath(installDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<InstalledLadderBundleState>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LaunchDiagnostics.Log($"Could not read ladder bundle state: {exception.Message}");
            return null;
        }
    }

    private static async Task WriteStateAsync(
        string installDirectory,
        InstalledLadderBundleState state,
        CancellationToken cancellationToken)
    {
        var path = GetStatePath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string GetStatePath(string installDirectory)
        => Path.Combine(installDirectory, "mods", "Reimagined", "d2rloader", StateFileName);

    private static string ResolveTargetPath(string installDirectory, string targetPath)
    {
        ValidateRelativePath(targetPath);
        var normalized = targetPath.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(installDirectory, normalized));
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A ladder bundle target escapes the D2R installation.");
        }

        var allowedRoot = Path.GetFullPath(Path.Combine(installDirectory, "mods", "Reimagined", "d2rloader"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Ladder bundle files may only target the Reimagined D2RLoader directory.");
        }

        return path;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A ladder bundle path escapes its transaction directory.");
        }

        return path;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || path.Contains("../", StringComparison.Ordinal)
            || path.Contains("..\\", StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("The ladder bundle contains an unsafe path.");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("A ladder bundle entry exceeds its allowed size.");
            }
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// D2RLoader stamps a full semantic version onto its binaries
    /// ("1.2.0-beta+preview.3"), which <see cref="Version"/> cannot parse at
    /// all. Compatibility here is about the numeric release, so only that part
    /// takes part in the comparison - a prerelease of the required version
    /// counts as that version. Pin the exact build with the SHA-256 policy
    /// fields when a prerelease must not qualify.
    /// </summary>
    internal static bool TryParseVersionCore(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Split('+', 2)[0].Split('-', 2)[0].Trim();
        return Version.TryParse(core, out version!);
    }

    /// <summary>
    /// A packed install keeps modinfo.json inside Reimagined.mpq; an unpacked
    /// one keeps it beside the folder. Both are real layouts in the wild, so
    /// looking in only one place reports a correct install as the wrong version.
    /// </summary>
    private static string? ReadInstalledModVersion(string installDirectory)
    {
        var modRoot = Path.Combine(installDirectory, "mods", "Reimagined");
        return ReadJsonString(Path.Combine(modRoot, "modinfo.json"), "version")
               ?? ReadJsonString(Path.Combine(modRoot, "Reimagined.mpq", "modinfo.json"), "version");
    }

    private static string? ReadJsonString(string path, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
