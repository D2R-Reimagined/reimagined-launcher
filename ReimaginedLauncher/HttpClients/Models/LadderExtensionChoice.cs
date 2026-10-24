using System;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed class LadderExtensionChoice
{
    public required Guid ApprovalId { get; init; }
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required D2RLoaderExtensionKind Kind { get; init; }
    public string? Version { get; init; }
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version}";
    public string FileVersionDetail => string.IsNullOrWhiteSpace(Version) ? FileName : $"{FileName} · {VersionLabel}";
    public string AvailabilityDetail => IsProvidedByLauncher
        ? IsRequired ? "Supplied by signed Ladder package" : "Optional · select then Update to download"
        : !IsInstalled ? "Not installed or hash mismatch"
        : IsLadderDisabled ? IsRequired ? "Will be enabled at launch" : "Currently disabled for Ladder"
        : "Installed in Ladder package";
    public bool IsRequired { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsProvidedByLauncher { get; init; }
    public bool IsLadderDisabled { get; init; }
    public bool IsSelected { get; set; }
    public bool IsAvailable => IsInstalled || IsProvidedByLauncher;
    public bool CanToggle => IsAvailable && !IsRequired;
    public string Detail => IsProvidedByLauncher
        ? IsRequired
            ? $"{FileName} (required, supplied by signed ladder package)"
            : $"{FileName} (optional, select then Update to download)"
        : !IsInstalled
        ? IsRequired
            ? $"{FileName} (required, not installed or hash mismatch)"
            : $"{FileName} (not installed)"
        : IsLadderDisabled
            ? IsRequired
                ? $"{FileName} (required, will be enabled at launch)"
                : $"{FileName} (currently ladder-disabled)"
            : FileName;
}
