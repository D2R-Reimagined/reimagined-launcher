using System;
using System.Collections.Generic;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed record LadderResponse(
    Guid Id,
    string Name,
    DateTimeOffset StartDateUtc,
    DateTimeOffset EndDateUtc,
    IReadOnlyList<LadderAllowedExtensionResponse> AllowedExtensions)
{
    public string DateRangeDisplay =>
        $"{StartDateUtc.ToLocalTime():MMM d, yyyy h:mm tt} - {EndDateUtc.ToLocalTime():MMM d, yyyy h:mm tt} local";
}

public sealed record LadderAllowedExtensionResponse(
    Guid Id,
    string Name,
    string FileName,
    string Sha256,
    D2RLoaderExtensionKind Kind);
