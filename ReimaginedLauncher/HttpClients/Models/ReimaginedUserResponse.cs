using System;
using System.Collections.Generic;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed record ReimaginedUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string? BattleTag,
    string? BattleNetId,
    string? SteamId,
    IReadOnlyList<string> Roles,
    DateTime CreatedAtUtc);
