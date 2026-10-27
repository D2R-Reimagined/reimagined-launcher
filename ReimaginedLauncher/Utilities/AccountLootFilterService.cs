using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients;
namespace ReimaginedLauncher.Utilities;

public sealed record InstalledLootFilter(string Id, string Name);
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
        catch (LootFilterRateLimitException ex) { Block(user, ex.RetryAt); throw; }
    }
    internal TimeSpan ServerWait(Guid user) => blocked.GetValueOrDefault(user) > time.GetUtcNow()
        ? blocked[user] - time.GetUtcNow() : TimeSpan.Zero;
    internal void Block(Guid user, DateTimeOffset until) => blocked[user] = until;
    internal static bool IsSupportedFilterPlugin(string? fileName)
        => fileName is not null
           && (fileName.Equals("pyre-lootfilter.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("d2rl-pyre-lootfilter.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("unhoarder.dll", StringComparison.OrdinalIgnoreCase));
    internal static bool IsFilterPluginAvailable(D2RLoaderInventory? inventory, LaunchExperience experience)
        => inventory is { IsInstalled: true, AllowModExtensions: true }
           && inventory.Plugins.Any(plugin => plugin.IsEnabled
               && IsSupportedFilterPlugin(plugin.FileName)
               && (plugin.Scope == D2RLoaderExtensionScope.Reimagined
                   || experience != LaunchExperience.Ladder && inventory.AllowGlobalExtensions));
    internal static string Install(string root, JsonElement document, Action checkContext)
    {
        var json = LootFilterValidation.Validate(document);
        var target = LocalPluginInstallService.SafePath(root, "config/filter.json");
        var backup = LocalPluginInstallService.Commit(root,
            [new LocalInstallFile("config/filter.json", Encoding.UTF8.GetBytes(json), File.Exists(target) ? LocalPluginInstallService.Hash(target) : null)], checkContext, ".launcher-filter-backups");
        return backup;
    }
    internal static string? TrimBackups(string root)
    {
        try
        {
            var parent = LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups");
            if (!Directory.Exists(parent)) return null;
            var backups = Directory.GetDirectories(parent).Where(folder => File.Exists(Path.Combine(folder, "config", "filter.json")))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
            foreach (var folder in backups.Skip(3))
            {
                LocalPluginInstallService.EnsureNoLinks(folder);
                // Only delete the file this feature owns, never recursively remove arbitrary contents.
                var filter = LocalPluginInstallService.SafePath(folder, "config/filter.json");
                File.Delete(filter);
                var config = Path.GetDirectoryName(filter)!;
                if (!Directory.EnumerateFileSystemEntries(config).Any()) Directory.Delete(config);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Filter installed, but older backups could not be removed: " + ex.Message; }
    }
}
