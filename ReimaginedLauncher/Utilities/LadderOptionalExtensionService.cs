using ReimaginedLauncher.HttpClients.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public static partial class LadderOptionalExtensionService
{
    private const long MaxBytes = 16L * 1024 * 1024;

    public static bool CanDownload(LadderAllowedExtensionResponse extension) =>
        !extension.IsRequired && !string.IsNullOrWhiteSpace(extension.DownloadPath)
        && extension.SizeBytes is > 0 and <= MaxBytes && SafeName().IsMatch(extension.FileName) && !ReservedName().IsMatch(extension.FileName)
        && HashPattern().IsMatch(extension.Sha256) && HasMatchingKind(extension);

    private static bool HasMatchingKind(LadderAllowedExtensionResponse extension) => extension.Kind switch
    {
        D2RLoaderExtensionKind.Plugin => extension.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
        D2RLoaderExtensionKind.Patch => extension.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static string TargetPath(LadderAllowedExtensionResponse extension)
    {
        if (!SafeName().IsMatch(extension.FileName) || ReservedName().IsMatch(extension.FileName) || !HasMatchingKind(extension))
            throw new InvalidDataException("The optional extension has an invalid filename or kind.");
        return $"mods/Reimagined/d2rloader/{(extension.Kind == D2RLoaderExtensionKind.Plugin ? "plugins" : "patches")}/{extension.FileName}";
    }

    internal static bool IsExtensionPath(string path) => ExtensionPathPattern().IsMatch(path.Replace('\\', '/'));

    internal static string PluginId(LadderAllowedExtensionResponse extension)
    {
        var name = Path.GetFileNameWithoutExtension(extension.FileName);
        return name.StartsWith("d2rl-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
    }

    public static void VerifyDownload(LadderAllowedExtensionResponse extension, byte[] bytes)
    {
        if (!CanDownload(extension) || bytes.LongLength != extension.SizeBytes
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), extension.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{extension.FileName} failed its approved size or SHA-256 check.");
    }

    internal static async Task<List<string>> GetProblemsAsync(string root, LadderBundleResponse bundle,
        IReadOnlyList<LadderAllowedExtensionResponse> extensions, IReadOnlySet<Guid> selectedIds, CancellationToken token)
    {
        var desired = GetDesired(bundle, extensions, selectedIds);
        var problems = new List<string>();
        foreach (var (relative, extension) in desired)
        {
            if (!await MatchesAsync(CheckedPath(root, relative), extension, token))
                problems.Add($"{extension.FileName} needs to be downloaded or updated.");
        }
        var required = bundle.Files.Select(file => file.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in EnumerateExtensions(root))
        {
            if (!required.Contains(relative) && !desired.ContainsKey(relative))
                problems.Add($"{Path.GetFileName(relative)} needs to be removed from the active mod folder.");
        }
        return problems;
    }

    public static async Task SynchronizeAsync(string root, LadderBundleResponse bundle,
        IReadOnlyList<LadderAllowedExtensionResponse> extensions, IReadOnlySet<Guid> selectedIds,
        Func<LadderAllowedExtensionResponse, CancellationToken, Task<byte[]>> download,
        IProgress<LadderBundleProgress>? progress = null, CancellationToken token = default)
    {
        var desired = GetDesired(bundle, extensions, selectedIds);
        var required = bundle.Files.Select(file => file.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backupRoot = $".reimagined-launcher/ladder-bundles/optional-backups/{Guid.NewGuid():N}";
        foreach (var (relative, extension) in desired)
        {
            token.ThrowIfCancellationRequested();
            var path = CheckedPath(root, relative);
            if (await MatchesAsync(path, extension, token)) continue;
            if (!CanDownload(extension))
                throw new InvalidOperationException($"{extension.FileName} has no downloadable artifact. Ask the ladder administrator to upload it.");
            var bytes = await download(extension, token);
            VerifyDownload(extension, bytes);
            var staging = CheckedPath(root, $"{backupRoot}/staging/{extension.Id:N}");
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            await File.WriteAllBytesAsync(staging, bytes, token);
            path = CheckedPath(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var backup = Backup(root, relative, backupRoot);
            try { File.Move(staging, path); }
            catch
            {
                if (backup is not null && !File.Exists(path)) File.Move(backup, path);
                throw;
            }
        }
        foreach (var relative in EnumerateExtensions(root).ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (!required.Contains(relative) && !desired.ContainsKey(relative))
            {
                progress?.Report(new LadderBundleProgress($"Removing {Path.GetFileName(relative)} from the active mod folder..."));
                Backup(root, relative, backupRoot);
            }
        }
        var problems = await GetProblemsAsync(root, bundle, extensions, selectedIds, token);
        if (problems.Count > 0) throw new InvalidDataException(string.Join(" ", problems));
    }

    private static Dictionary<string, LadderAllowedExtensionResponse> GetDesired(LadderBundleResponse bundle,
        IReadOnlyList<LadderAllowedExtensionResponse> extensions, IReadOnlySet<Guid> selectedIds)
    {
        var required = bundle.Files.Select(file => file.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, LadderAllowedExtensionResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions.Where(item => !item.IsRequired && selectedIds.Contains(item.Id)))
        {
            var target = TargetPath(extension);
            if (required.Contains(target)) throw new InvalidDataException("An optional extension cannot override a signed bundle file.");
            if (!HashPattern().IsMatch(extension.Sha256)) throw new InvalidDataException("An optional extension has an invalid SHA-256.");
            if (!result.TryAdd(target, extension)) throw new InvalidDataException("Duplicate optional extension target.");
        }
        return result;
    }

    private static async Task<bool> MatchesAsync(string path, LadderAllowedExtensionResponse extension, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        if (extension.SizeBytes is { } size && new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), extension.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateExtensions(string root)
    {
        foreach (var kind in new[] { "plugins", "patches" })
        {
            var relative = $"mods/Reimagined/d2rloader/{kind}";
            var folder = CheckedPath(root, relative);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var target = $"{relative}/{Path.GetFileName(file)}";
                if (IsExtensionPath(target))
                {
                    CheckedPath(root, target);
                    yield return target;
                }
            }
        }
    }

    private static string? Backup(string root, string relative, string backupRoot)
    {
        var source = CheckedPath(root, relative);
        if (!File.Exists(source)) return null;
        var backup = CheckedPath(root, $"{backupRoot}/{relative}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(source, backup);
        return backup;
    }

    internal static string CheckedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Optional extension path escapes the installation.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Optional extension paths cannot contain symbolic links or junctions.");
        }
        return path;
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,95}\.(dll|json)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
    [GeneratedRegex(@"\A(con|prn|aux|nul|com[1-9]|lpt[1-9])\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"\Amods/Reimagined/d2rloader/(plugins/[^/]+\.dll|patches/[^/]+\.json)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPathPattern();
}
