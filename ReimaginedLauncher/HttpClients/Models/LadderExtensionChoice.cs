using System;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed class LadderExtensionChoice
{
    public required Guid ApprovalId { get; init; }
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required D2RLoaderExtensionKind Kind { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsLadderDisabled { get; init; }
    public bool IsSelected { get; set; }
    public string Detail => !IsInstalled
        ? $"{FileName} (not installed)"
        : IsLadderDisabled
            ? $"{FileName} (currently ladder-disabled)"
            : FileName;
}
