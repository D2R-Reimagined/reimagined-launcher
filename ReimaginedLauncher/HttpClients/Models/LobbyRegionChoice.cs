using System;
using System.Collections.Generic;
using System.Linq;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed class LobbyRegionChoice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsMeasured { get; init; }
    public double? PingMs { get; init; }
    public bool IsSelected { get; set; }
    public bool CanToggle { get; init; } = true;
    public bool IsOffline => !IsAvailable;
    public string Badge => !IsAvailable
        ? "offline"
        : PingMs is { } ping
            ? $"{Math.Round(ping):0} ms"
            : IsMeasured ? "no ping" : "testing...";
}

public sealed class LobbyServerChoice
{
    public static LobbyServerChoice Automatic { get; } = new() { Id = null, Label = "Automatic (balanced)" };

    public string? Id { get; init; }
    public required string Label { get; init; }

    public static LobbyServerChoice From(LobbyAdminServerResponse server, IReadOnlyDictionary<string, string> regionNames)
    {
        var name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name;
        var regions = server.RegionIds is { Count: > 0 } ids
            ? string.Join(", ", ids.Select(id => regionNames.TryGetValue(id, out var regionName) ? regionName : id))
            : "no region";
        var label = $"{name} — {regions} — {server.Games}/{server.MaxGames}";
        return new LobbyServerChoice { Id = server.Id, Label = server.Online ? label : label + " (offline)" };
    }
}
