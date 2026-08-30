using System;
using System.Collections.Generic;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed record LadderResponse(
    Guid Id,
    string Name,
    DateTimeOffset StartDateUtc,
    DateTimeOffset EndDateUtc,
    IReadOnlyList<LadderAllowedExtensionResponse> AllowedExtensions,
    LadderBundleResponse? ActiveBundle)
{
    public string DateRangeDisplay =>
        $"{StartDateUtc.ToLocalTime():MMM d, yyyy h:mm tt} - {EndDateUtc.ToLocalTime():MMM d, yyyy h:mm tt} local";
}

public sealed record LadderAllowedExtensionResponse(
    Guid Id,
    string Name,
    string FileName,
    string Sha256,
    D2RLoaderExtensionKind Kind,
    bool IsRequired);

public sealed record LadderBundleResponse(
    Guid Id,
    Guid LadderId,
    int Revision,
    string Status,
    string ArtifactSha256,
    string ManifestSha256,
    string ManifestSignature,
    string SigningKeyId,
    string SourceCommit,
    long ArtifactSizeBytes,
    LadderBundleCompatibility Compatibility,
    IReadOnlyList<LadderBundleManifestFile> Files,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string DownloadPath);

public sealed record LadderBundleCompatibility(
    string MinimumLauncherVersion,
    string RequiredD2RLoaderVersion,
    string RequiredD2RLoaderSha256,
    string RequiredD2RCoreSha256,
    string RequiredModVersion,
    string SupportedGameVersion);

public sealed record LadderBundleManifestFile(
    Guid PluginReleaseId,
    string PluginId,
    string Name,
    string Version,
    D2RLoaderExtensionKind Kind,
    bool IsRequired,
    string ArchivePath,
    string TargetPath,
    string FileName,
    long SizeBytes,
    string Sha256);

public sealed record LadderLaunchTicketResponse(
    string LaunchTicket,
    DateTimeOffset ExpiresAtUtc,
    Guid LadderId,
    Guid BundleId,
    int BundleRevision);
