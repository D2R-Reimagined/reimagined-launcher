using System;
using System.Collections.Generic;
using System.Linq;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Which lobby regions a ladder launch asks for. Ping maps are keyed by region
/// id; a missing key or a null value both count as "no ping".
/// </summary>
public static class LobbyRegionSelection
{
    public const int AutomaticRegionCount = 2;

    /// <summary>The fastest available regions, topped up with available ones by name.</summary>
    public static IReadOnlyList<string> Automatic(
        IReadOnlyList<LobbyRegionResponse> regions,
        IReadOnlyDictionary<string, double?> pings)
    {
        var available = regions.Where(region => region.Available).ToArray();
        var pinged = available
            .Where(region => PingOf(pings, region.Id) is not null)
            .OrderBy(region => PingOf(pings, region.Id))
            .ThenBy(region => region.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.Id, StringComparer.Ordinal);
        var unpinged = available
            .Where(region => PingOf(pings, region.Id) is null)
            .OrderBy(region => region.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.Id, StringComparer.Ordinal);
        return pinged.Concat(unpinged)
            .Take(AutomaticRegionCount)
            .Select(region => region.Id)
            .ToArray();
    }

    /// <summary>True when an explicit selection still names a region the API returns.</summary>
    public static bool IsCustom(IReadOnlyList<LobbyRegionResponse> regions, IReadOnlyCollection<string>? explicitSelection)
        => explicitSelection is not null && regions.Any(region => explicitSelection.Contains(region.Id));

    /// <summary>
    /// The ticked regions in API order. An explicit set drops ids the API no
    /// longer returns and falls back to automatic when none are left.
    /// </summary>
    public static IReadOnlyList<string> Effective(
        IReadOnlyList<LobbyRegionResponse> regions,
        IReadOnlyDictionary<string, double?> pings,
        IReadOnlyCollection<string>? explicitSelection)
    {
        if (!IsCustom(regions, explicitSelection)) return Automatic(regions, pings);
        return regions
            .Where(region => explicitSelection!.Contains(region.Id))
            .Select(region => region.Id)
            .ToArray();
    }

    /// <summary>
    /// The explicit set after ticking or unticking one region, or null when that
    /// would leave nothing ticked.
    /// </summary>
    public static List<string>? Toggle(IReadOnlyList<string> effective, string regionId, bool isSelected)
    {
        var next = effective.Where(id => !string.Equals(id, regionId, StringComparison.Ordinal)).ToList();
        if (isSelected) next.Add(regionId);
        return next.Count == 0 ? null : next;
    }

    /// <summary>Closest first; regions without a ping go last, by name.</summary>
    public static IReadOnlyList<string> OrderForLaunch(
        IEnumerable<string> selectedIds,
        IReadOnlyList<LobbyRegionResponse> regions,
        IReadOnlyDictionary<string, double?> pings)
    {
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        return regions
            .Where(region => selected.Contains(region.Id))
            .OrderBy(region => PingOf(pings, region.Id) is null)
            .ThenBy(region => PingOf(pings, region.Id) ?? 0)
            .ThenBy(region => region.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.Id, StringComparer.Ordinal)
            .Select(region => region.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static double? PingOf(IReadOnlyDictionary<string, double?> pings, string regionId)
        => pings.TryGetValue(regionId, out var ping) ? ping : null;
}
