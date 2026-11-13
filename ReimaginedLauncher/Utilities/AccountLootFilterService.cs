using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients;

namespace ReimaginedLauncher.Utilities;

public sealed record InstalledLootFilter(string Id, string Name);
public sealed record SyncedLootFilterEntry(string Id, string Name, int Revision, string FileName);
public sealed record LootFilterSyncResult(
    IReadOnlyList<SyncedLootFilterEntry> Synced,
    int UpdatedCount,
    int DeletedCount,
    string? BackupWarning);

internal sealed record PreparedLootFilterSync(
    IReadOnlyList<SyncedLootFilterEntry> TargetEntries,
    IReadOnlyDictionary<string, byte[]> DownloadedPayloadsById,
    IReadOnlySet<string>? PreservedPreviousIds = null,
    string? Warning = null);

internal sealed class AccountLootFilterService(ReimaginedApiHttpClient api, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> requests = [];
    private readonly Dictionary<Guid, DateTimeOffset> blocked = [];
    private readonly Dictionary<Guid, IReadOnlyList<AccountLootFilter>> cache = [];
    private Guid? currentAccount;

    internal void UseAccount(Guid? user)
    {
        if (currentAccount != user) cache.Clear();
        currentAccount = user;
    }

    internal bool TryGetCachedList(Guid user, out IReadOnlyList<AccountLootFilter> filters)
        => cache.TryGetValue(user, out filters!);

    internal TimeSpan Wait(Guid user)
    {
        var now = time.GetUtcNow();
        if (!requests.TryGetValue(user, out var queue)) requests[user] = queue = new();
        while (queue.TryPeek(out var first) && now - first >= TimeSpan.FromMinutes(1)) queue.Dequeue();
        var until = blocked.GetValueOrDefault(user);
        if (queue.Count >= 5) until = new[] { until, queue.Peek() + TimeSpan.FromMinutes(1) }.Max();
        return until > now ? until - now : TimeSpan.Zero;
    }

    internal async Task<IReadOnlyList<AccountLootFilter>> ListAsync(Guid user, bool refresh, CancellationToken token)
    {
        if (!refresh && cache.TryGetValue(user, out var saved)) return saved;
        if (Wait(user) > TimeSpan.Zero) throw new InvalidOperationException("Please wait before refreshing your filter list.");
        requests[user].Enqueue(time.GetUtcNow());
        try
        {
            var list = await api.ListLootFiltersAsync(token);
            token.ThrowIfCancellationRequested();
            cache[user] = list;
            return list;
        }
        catch (LootFilterRateLimitException ex)
        {
            Block(user, ex.RetryAt);
            throw;
        }
    }

    internal void Block(Guid user, DateTimeOffset until) => blocked[user] = until;

    internal static bool IsSupportedFilterPlugin(string? fileName)
        => fileName is not null
           && (fileName.Equals("pyre-lootfilter.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("d2rl-pyre-lootfilter.dll", StringComparison.OrdinalIgnoreCase));

    internal static bool HasLegacySingleFileFilterPluginOnly(D2RLoaderInventory? inventory, LaunchExperience experience)
        => inventory is { IsInstalled: true, AllowModExtensions: true }
           && !IsFilterPluginAvailable(inventory, experience)
           && inventory.Plugins.Any(plugin => plugin.IsEnabled
               && plugin.FileName.Equals("unhoarder.dll", StringComparison.OrdinalIgnoreCase)
               && (plugin.Scope == D2RLoaderExtensionScope.Reimagined
                   || experience != LaunchExperience.Ladder && inventory.AllowGlobalExtensions));

    internal static bool IsFilterPluginAvailable(D2RLoaderInventory? inventory, LaunchExperience experience)
        => inventory is { IsInstalled: true, AllowModExtensions: true }
           && inventory.Plugins.Any(plugin => plugin.IsEnabled
               && IsSupportedFilterPlugin(plugin.FileName)
               && (plugin.Scope == D2RLoaderExtensionScope.Reimagined
                   || experience != LaunchExperience.Ladder && inventory.AllowGlobalExtensions));

    internal static bool TryResolveRoot(InstallationProfile profile, out string? root)
    {
        try
        {
            root = LocalPluginInstallService.ResolveRoot(profile);
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            root = null;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"Skipping loot filter root ({profile.LaunchExperience}): {ex.Message}");
            root = null;
            return false;
        }
    }

    internal static IReadOnlyList<string> ResolveInstalledSyncRoots(InstallationProfile profile)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new List<string>();
        if (TryResolveRoot(profile, out var primary) && primary is not null)
        {
            roots.Add(primary);
        }

        foreach (var experience in new[] { LaunchExperience.Online, LaunchExperience.Ladder })
        {
            if (experience == profile.LaunchExperience) continue;
            var candidateProfile = new InstallationProfile
            {
                Type = profile.Type,
                InstallDirectory = profile.InstallDirectory,
                LaunchExperience = experience
            };
            if (TryResolveRoot(candidateProfile, out var secondary)
                && secondary is not null
                && !roots.Contains(secondary, pathComparer))
            {
                roots.Add(secondary);
            }
        }

        return roots;
    }

    internal static string SanitizeFileStem(string? name, string fallbackId)
    {
        var raw = (name ?? string.Empty).Trim();
        var sb = new StringBuilder(raw.Length);
        var invalidChars = Path.GetInvalidFileNameChars();
        var previousSpace = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                {
                    if (previousSpace)
                    {
                        sb.Append(' ');
                        previousSpace = false;
                    }
                    sb.Append(ch);
                    sb.Append(raw[++i]);
                }
                continue;
            }
            if (char.IsLowSurrogate(ch))
            {
                continue;
            }
            if (char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || invalidChars.Contains(ch))
            {
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append('-');
                    previousSpace = false;
                }
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && !previousSpace && sb[^1] != '-')
                    previousSpace = true;
                continue;
            }
            if (previousSpace)
            {
                sb.Append(' ');
                previousSpace = false;
            }
            sb.Append(ch);
        }

        var stem = sb.ToString().Trim(' ', '.', '-');
        if (stem.Length > 48)
        {
            var cut = char.IsHighSurrogate(stem[47]) ? 47 : 48;
            stem = stem[..cut].Trim(' ', '.', '-');
        }

        var prefixBeforeDot = stem.Split('.')[0].Trim(' ', '-');
        if (string.IsNullOrWhiteSpace(stem) || IsReservedDeviceName(stem) || IsReservedDeviceName(prefixBeforeDot))
        {
            var cleanId = new string((fallbackId ?? string.Empty)
                .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                .Take(8)
                .ToArray())
                .Trim('-', '_');
            stem = string.IsNullOrEmpty(cleanId) ? "Filter" : $"Filter-{cleanId}";
        }

        return stem;
    }

    private static bool IsReservedDeviceName(string stem)
        => stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
           || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
           || (stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3'));

    internal static IReadOnlyList<SyncedLootFilterEntry> BuildTargetPlan(IReadOnlyList<AccountLootFilter> remoteFilters)
    {
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<SyncedLootFilterEntry>(remoteFilters.Count);
        foreach (var filter in remoteFilters)
        {
            var baseStem = SanitizeFileStem(filter.Name, filter.Id);
            var candidate = baseStem + ".json";
            var suffix = 2;
            while (!usedFileNames.Add(candidate))
            {
                candidate = $"{baseStem} ({suffix}).json";
                suffix++;
            }
            entries.Add(new SyncedLootFilterEntry(filter.Id, filter.Name, filter.Revision, candidate));
        }
        return entries;
    }

    private static Dictionary<string, SyncedLootFilterEntry> IndexById(IReadOnlyList<SyncedLootFilterEntry>? entries)
    {
        var byId = new Dictionary<string, SyncedLootFilterEntry>(StringComparer.Ordinal);
        if (entries is null) return byId;
        foreach (var entry in entries)
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                byId[entry.Id] = entry;
        }
        return byId;
    }

    internal static bool IsFullySynced(
        string root,
        IReadOnlyList<AccountLootFilter> remoteFilters,
        IReadOnlyList<SyncedLootFilterEntry>? previousSynced,
        bool hadLegacyInstall = false)
    {
        try
        {
            var plan = BuildTargetPlan(remoteFilters);
            var previousById = IndexById(previousSynced);
            if (plan.Count == 0 && previousById.Count == 0 && !hadLegacyInstall)
            {
                return true;
            }

            var filterDir = LocalPluginInstallService.SafePath(root, "config/filter");
            var legacyFile = LocalPluginInstallService.SafePath(root, "config/filter.json");
            if (File.Exists(legacyFile)) return false;

            if (plan.Count == 0)
            {
                return !Directory.Exists(filterDir)
                       || !Directory.EnumerateFiles(filterDir, "*.json", SearchOption.TopDirectoryOnly).Any();
            }

            if (!Directory.Exists(filterDir)) return false;
            var rawFiles = Directory.GetFiles(filterDir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
            var diskFilesOrdinal = rawFiles.ToHashSet(StringComparer.Ordinal);

            if (rawFiles.Length != plan.Count || diskFilesOrdinal.Count != plan.Count) return false;
            foreach (var target in plan)
            {
                if (!diskFilesOrdinal.Contains(target.FileName)) return false;
                if (!previousById.TryGetValue(target.Id, out var prev)) return false;
                if (!string.Equals(prev.FileName, target.FileName, StringComparison.Ordinal)) return false;
                if (prev.Revision != target.Revision) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal Task<PreparedLootFilterSync> PrepareSyncAsync(
        string root,
        IReadOnlyList<AccountLootFilter> remoteFilters,
        IReadOnlyList<SyncedLootFilterEntry>? previousSynced,
        Action checkContext,
        CancellationToken token)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var map = new Dictionary<string, IReadOnlyList<SyncedLootFilterEntry>>(pathComparer);
        if (previousSynced is not null) map[root] = previousSynced;
        return PrepareMultiRootSyncAsync([root], remoteFilters, map, checkContext, token);
    }

    internal async Task<PreparedLootFilterSync> PrepareMultiRootSyncAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<AccountLootFilter> remoteFilters,
        IReadOnlyDictionary<string, IReadOnlyList<SyncedLootFilterEntry>> previousSyncedByRoot,
        Action checkContext,
        CancellationToken token)
    {
        var plan = BuildTargetPlan(remoteFilters);
        var validTargets = new List<SyncedLootFilterEntry>(plan.Count);
        var payloadsById = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var preservedPreviousIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedWarnings = new List<string>();
        InvalidDataException? firstValidationFailure = null;

        foreach (var target in plan)
        {
            checkContext();
            var needsDownload = false;
            foreach (var root in roots)
            {
                previousSyncedByRoot.TryGetValue(root, out var previousSynced);
                var prev = previousSynced?.LastOrDefault(entry => entry is not null && string.Equals(entry.Id, target.Id, StringComparison.Ordinal));
                LocalPluginInstallService.SafePath(root, "config/filter/" + target.FileName);
                var existingPath = prev is not null
                    ? LocalPluginInstallService.SafePath(root, "config/filter/" + prev.FileName)
                    : null;
                if (prev is null || prev.Revision != target.Revision || existingPath is null || !File.Exists(existingPath))
                {
                    needsDownload = true;
                    break;
                }
            }

            if (!needsDownload)
            {
                validTargets.Add(target);
                continue;
            }

            AccountLootFilterDetails details;
            try
            {
                details = await api.GetLootFilterAsync(target.Id, token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            checkContext();
            try
            {
                var json = LootFilterValidation.Validate(details.Document);
                payloadsById[target.Id] = Encoding.UTF8.GetBytes(json);
                var effectiveRevision = details.Revision > 0 ? details.Revision : target.Revision;
                validTargets.Add(target with { Revision = effectiveRevision });
            }
            catch (InvalidDataException ex)
            {
                firstValidationFailure ??= ex;
                preservedPreviousIds.Add(target.Id);
                skippedWarnings.Add($"Skipped '{target.Name}': {ex.Message}");
            }
        }

        if (validTargets.Count == 0 && firstValidationFailure is not null)
        {
            throw firstValidationFailure;
        }

        var warning = skippedWarnings.Count > 0 ? string.Join(" ", skippedWarnings) : null;
        return new PreparedLootFilterSync(validTargets, payloadsById, preservedPreviousIds, warning);
    }

    internal static LootFilterSyncResult CommitSync(
        string root,
        PreparedLootFilterSync prepared,
        Action checkContext,
        IReadOnlyList<SyncedLootFilterEntry>? previousSynced = null,
        bool hadLegacyInstall = false)
    {
        checkContext();
        LocalPluginInstallService.EnsureNoLinks(root);
        var configDir = LocalPluginInstallService.SafePath(root, "config");
        if (Directory.Exists(configDir)) LocalPluginInstallService.EnsureNoLinks(configDir);
        var filterDir = LocalPluginInstallService.SafePath(root, "config/filter");
        if (Directory.Exists(filterDir)) LocalPluginInstallService.EnsureNoLinks(filterDir);

        var previousById = IndexById(previousSynced);
        if (prepared.TargetEntries.Count == 0
            && (prepared.PreservedPreviousIds is null || prepared.PreservedPreviousIds.Count == 0)
            && previousById.Count == 0
            && !hadLegacyInstall)
        {
            return new LootFilterSyncResult([], 0, 0, prepared.Warning);
        }

        var effectiveEntries = new List<SyncedLootFilterEntry>(prepared.TargetEntries);
        var usedNames = effectiveEntries.Select(entry => entry.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (prepared.PreservedPreviousIds is { Count: > 0 })
        {
            foreach (var preservedId in prepared.PreservedPreviousIds)
            {
                if (!previousById.TryGetValue(preservedId, out var prev)) continue;
                var prevPath = LocalPluginInstallService.SafePath(root, "config/filter/" + prev.FileName);
                if (File.Exists(prevPath) && usedNames.Add(prev.FileName))
                {
                    effectiveEntries.Add(prev);
                }
            }
        }

        var existingOrdinalNames = Directory.Exists(filterDir)
            ? Directory.GetFiles(filterDir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // Read any renamed/shifted local source payloads into memory before modifying files on disk
        // so swaps (A <-> B) and disambiguated shifts (Filter (2).json -> Filter.json) never clobber sources.
        var renamedSourceBytesById = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var renamedOldFileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in prepared.TargetEntries)
        {
            if (prepared.DownloadedPayloadsById.ContainsKey(entry.Id)) continue;
            if (!previousById.TryGetValue(entry.Id, out var prev))
                throw new IOException($"Missing cached filter state for '{entry.Name}'. Refresh to retry.");
            if (string.Equals(prev.FileName, entry.FileName, StringComparison.Ordinal)
                && existingOrdinalNames.Contains(entry.FileName))
            {
                continue;
            }

            var previousPath = LocalPluginInstallService.SafePath(root, "config/filter/" + prev.FileName);
            if (!File.Exists(previousPath))
                throw new IOException($"Previous filter file '{prev.FileName}' was removed during sync. Refresh to retry.");
            LocalPluginInstallService.EnsureNoLinks(previousPath);
            renamedSourceBytesById[entry.Id] = File.ReadAllBytes(previousPath);
            if (!string.Equals(prev.FileName, entry.FileName, StringComparison.Ordinal))
            {
                renamedOldFileNames.Add(prev.FileName);
            }
        }

        var filesToCommit = new List<LocalInstallFile>();
        foreach (var entry in prepared.TargetEntries)
        {
            var relative = "config/filter/" + entry.FileName;
            var targetPath = LocalPluginInstallService.SafePath(root, relative);
            byte[]? bytesToWrite = null;

            if (prepared.DownloadedPayloadsById.TryGetValue(entry.Id, out var downloadedBytes))
            {
                var currentHash = File.Exists(targetPath) ? LocalPluginInstallService.Hash(targetPath) : null;
                var incomingHash = Convert.ToHexString(SHA256.HashData(downloadedBytes));
                if (currentHash != incomingHash || !existingOrdinalNames.Contains(entry.FileName))
                {
                    bytesToWrite = downloadedBytes;
                }
            }
            else if (renamedSourceBytesById.TryGetValue(entry.Id, out var localBytes))
            {
                bytesToWrite = localBytes;
            }

            if (bytesToWrite is not null)
            {
                filesToCommit.Add(new LocalInstallFile(
                    relative,
                    bytesToWrite,
                    File.Exists(targetPath) ? LocalPluginInstallService.Hash(targetPath) : null));
            }
        }

        string? backupDir = null;
        if (filesToCommit.Count > 0)
        {
            backupDir = LocalPluginInstallService.Commit(root, filesToCommit, checkContext, ".launcher-filter-backups");
        }

        // On case-insensitive filesystems (Windows NTFS), overwriting "Strict.json" via "strict.json" preserves
        // the old directory-entry casing. Normalize casing in place when no distinct target entry already exists.
        if (Directory.Exists(filterDir))
        {
            var currentFiles = Directory.GetFiles(filterDir, "*.json", SearchOption.TopDirectoryOnly);
            var currentOrdinalNames = currentFiles
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
            var desiredByIgnoreCase = effectiveEntries.ToDictionary(
                entry => entry.FileName,
                entry => entry.FileName,
                StringComparer.OrdinalIgnoreCase);

            foreach (var existingFile in currentFiles)
            {
                var actualName = Path.GetFileName(existingFile);
                if (desiredByIgnoreCase.TryGetValue(actualName, out var desiredName)
                    && !string.Equals(actualName, desiredName, StringComparison.Ordinal)
                    && !currentOrdinalNames.Contains(desiredName))
                {
                    LocalPluginInstallService.EnsureNoLinks(existingFile);
                    var desiredPath = LocalPluginInstallService.SafePath(root, "config/filter/" + desiredName);
                    File.Move(existingFile, desiredPath);
                    currentOrdinalNames.Remove(actualName);
                    currentOrdinalNames.Add(desiredName);
                }
            }
        }

        var expectedNamesOrdinal = effectiveEntries
            .Select(entry => entry.FileName)
            .ToHashSet(StringComparer.Ordinal);

        var deletedCount = 0;
        if (Directory.Exists(filterDir))
        {
            foreach (var existingFile in Directory.GetFiles(filterDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                checkContext();
                var fileName = Path.GetFileName(existingFile);
                if (expectedNamesOrdinal.Contains(fileName)) continue;
                BackupBeforeDelete(root, ref backupDir, existingFile, "config/filter/" + fileName);
                File.Delete(existingFile);
                if (!renamedOldFileNames.Contains(fileName))
                {
                    deletedCount++;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(filterDir).Any())
            {
                Directory.Delete(filterDir);
            }
        }

        var legacyFilter = LocalPluginInstallService.SafePath(root, "config/filter.json");
        if (File.Exists(legacyFilter))
        {
            BackupBeforeDelete(root, ref backupDir, legacyFilter, "config/filter.json");
            File.Delete(legacyFilter);
            deletedCount++;
        }

        var backupWarning = TrimBackups(root);
        var combinedWarning = string.IsNullOrWhiteSpace(prepared.Warning)
            ? backupWarning
            : string.IsNullOrWhiteSpace(backupWarning)
                ? prepared.Warning
                : $"{prepared.Warning} {backupWarning}";
        return new LootFilterSyncResult(effectiveEntries, filesToCommit.Count, deletedCount, combinedWarning);
    }

    private static void BackupBeforeDelete(string root, ref string? backupDir, string sourcePath, string relativePath)
    {
        LocalPluginInstallService.EnsureNoLinks(sourcePath);
        backupDir ??= Path.Combine(
            LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups"),
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        var backupCopy = LocalPluginInstallService.SafePath(backupDir, relativePath);
        if (!File.Exists(backupCopy))
        {
            var expectedHash = LocalPluginInstallService.Hash(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupCopy)!);
            File.Copy(sourcePath, backupCopy, overwrite: false);
            if (LocalPluginInstallService.Hash(backupCopy) != expectedHash)
                throw new IOException($"A filter file changed while being backed up: {relativePath}");
        }
    }

    internal static string? TrimBackups(string root)
    {
        try
        {
            var parent = LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups");
            if (!Directory.Exists(parent)) return null;
            var backups = Directory.GetDirectories(parent)
                .Where(HasFilterBackupFiles)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
            foreach (var folder in backups.Skip(3))
            {
                LocalPluginInstallService.EnsureNoLinks(folder);
                var legacy = LocalPluginInstallService.SafePath(folder, "config/filter.json");
                if (File.Exists(legacy)) File.Delete(legacy);
                var filterSubdir = LocalPluginInstallService.SafePath(folder, "config/filter");
                if (Directory.Exists(filterSubdir))
                {
                    LocalPluginInstallService.EnsureNoLinks(filterSubdir);
                    foreach (var file in Directory.GetFiles(filterSubdir, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        LocalPluginInstallService.EnsureNoLinks(file);
                        File.Delete(file);
                    }
                    if (!Directory.EnumerateFileSystemEntries(filterSubdir).Any())
                        Directory.Delete(filterSubdir);
                }
                var config = LocalPluginInstallService.SafePath(folder, "config");
                if (Directory.Exists(config) && !Directory.EnumerateFileSystemEntries(config).Any())
                    Directory.Delete(config);
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Filters synced, but older backups could not be removed: " + ex.Message;
        }
    }

    private static bool HasFilterBackupFiles(string folder)
    {
        if (File.Exists(Path.Combine(folder, "config", "filter.json")))
            return true;
        var filterSubdir = Path.Combine(folder, "config", "filter");
        return Directory.Exists(filterSubdir)
               && Directory.EnumerateFiles(filterSubdir, "*.json", SearchOption.TopDirectoryOnly).Any();
    }
}
