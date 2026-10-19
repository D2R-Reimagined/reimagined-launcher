using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// ICMP round trips to the lobby regions. Results are keyed by region id: a
/// missing key means the region has not been measured, a null value means
/// every echo failed.
/// </summary>
public sealed class RegionPingService
{
    public const int EchoesPerTarget = 3;
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    public static RegionPingService Shared { get; } = new();

    private readonly Func<string, TimeSpan, CancellationToken, Task<long?>> _echo;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, TargetEntry> _targets = new(StringComparer.OrdinalIgnoreCase);

    public RegionPingService(
        Func<string, TimeSpan, CancellationToken, Task<long?>>? echo = null,
        TimeProvider? timeProvider = null)
    {
        _echo = echo ?? SendEchoAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyDictionary<string, double?>> MeasureAsync(
        IReadOnlyList<LobbyRegionResponse> regions,
        bool force = false)
    {
        var targets = regions
            .SelectMany(region => region.PingTargets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(target => target, target => GetOrStart(target, force), StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(targets.Values);

        var results = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var region in regions)
        {
            results[region.Id] = Best((region.PingTargets ?? [])
                .Where(targets.ContainsKey)
                .Select(target => targets[target].Result));
        }

        return results;
    }

    /// <summary>The most recent finished measurement of each region, however old.</summary>
    public IReadOnlyDictionary<string, double?> GetLastKnown(IReadOnlyList<LobbyRegionResponse> regions)
    {
        var results = new Dictionary<string, double?>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var region in regions)
            {
                var measured = (region.PingTargets ?? [])
                    .Select(target => _targets.GetValueOrDefault(target))
                    .Where(entry => entry is { Measured: true })
                    .Select(entry => entry!.Last)
                    .ToArray();
                if (measured.Length > 0) results[region.Id] = Best(measured);
            }
        }

        return results;
    }

    /// <summary>Median of the successful echoes, or null when none answered.</summary>
    internal static double? Median(IReadOnlyList<long> samples)
    {
        if (samples.Count == 0) return null;
        var sorted = samples.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static double? Best(IEnumerable<double?> pings)
    {
        var answered = pings.OfType<double>().ToArray();
        return answered.Length == 0 ? null : answered.Min();
    }

    private Task<double?> GetOrStart(string target, bool force)
    {
        lock (_gate)
        {
            if (_targets.TryGetValue(target, out var entry)
                && (!entry.Pending.IsCompleted
                    || (!force && _timeProvider.GetElapsedTime(entry.StartedAt) < CacheLifetime)))
            {
                return entry.Pending;
            }

            entry ??= new TargetEntry();
            _targets[target] = entry;
            entry.StartedAt = _timeProvider.GetTimestamp();
            entry.Pending = MeasureTargetAsync(target, entry);
            return entry.Pending;
        }
    }

    private async Task<double?> MeasureTargetAsync(string target, TargetEntry entry)
    {
        await Task.Yield();
        var samples = new List<long>(EchoesPerTarget);
        for (var echo = 0; echo < EchoesPerTarget; echo++)
        {
            try
            {
                if (await _echo(target, EchoTimeout, CancellationToken.None) is { } roundTrip)
                    samples.Add(roundTrip);
            }
            catch (Exception exception)
            {
                LaunchDiagnostics.Log($"Region ping to {target} failed: {exception.Message}");
            }
        }

        var result = Median(samples);
        lock (_gate)
        {
            entry.Last = result;
            entry.Measured = true;
        }

        return result;
    }

    private static async Task<long?> SendEchoAsync(string address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken);
        return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
    }

    private sealed class TargetEntry
    {
        public Task<double?> Pending { get; set; } = Task.FromResult<double?>(null);
        public long StartedAt { get; set; }
        public double? Last { get; set; }
        public bool Measured { get; set; }
    }
}
