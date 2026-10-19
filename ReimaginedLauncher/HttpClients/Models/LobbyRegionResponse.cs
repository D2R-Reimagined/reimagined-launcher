using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ReimaginedLauncher.HttpClients.Models;

// The lobby wire format is snake_case, unlike the rest of the API.
public sealed record LobbyRegionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("ping_targets")] IReadOnlyList<string>? PingTargets);

public sealed record LobbyAdminServerResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("region_ids")] IReadOnlyList<string>? RegionIds,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("games")] int Games,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("max_games")] int MaxGames);
