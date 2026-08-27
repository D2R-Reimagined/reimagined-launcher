using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LadderExtensionApproval(
    Guid Id,
    string Name,
    string FileName,
    string Sha256,
    D2RLoaderExtensionKind Kind);

public sealed record LadderApprovedExtensionState(
    LadderExtensionApproval Approval,
    bool IsInstalled,
    bool IsLadderDisabled);

public sealed record LadderD2RLoaderPolicyPreview(
    IReadOnlyList<LadderApprovedExtensionState> ApprovedExtensions,
    IReadOnlyList<D2RLoaderExtensionInfo> UnapprovedExtensions);

public sealed record LadderD2RLoaderPolicyResult(
    IReadOnlyList<D2RLoaderExtensionInfo> UnapprovedMoved,
    IReadOnlyList<D2RLoaderExtensionInfo> UnselectedMoved,
    int RestoredCount);

public static partial class D2RLoaderService
{
    private const string LadderDisabledFolderName = "ladder-disabled";

    public static async Task<LadderD2RLoaderPolicyPreview> PreviewLadderPolicyAsync(
        string? installDirectory,
        IReadOnlyList<LadderExtensionApproval> approvals,
        CancellationToken cancellationToken = default)
    {
        var inventory = DiscoverForLadderPolicy(installDirectory);
        var matches = await MatchApprovalsAsync(inventory.Extensions, approvals, cancellationToken);
        var approvedStates = approvals
            .Select(approval =>
            {
                var installed = matches.FirstOrDefault(match => match.Approval.Id == approval.Id)?.Extension;
                return new LadderApprovedExtensionState(
                    approval,
                    installed is not null,
                    installed?.IsLadderDisabled ?? false);
            })
            .OrderBy(state => state.Approval.Kind)
            .ThenBy(state => state.Approval.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var approvedPaths = matches
            .Select(match => match.Extension.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unapproved = inventory.Extensions
            .Where(extension => !approvedPaths.Contains(extension.FilePath))
            .ToArray();

        return new LadderD2RLoaderPolicyPreview(approvedStates, unapproved);
    }

    public static async Task<LadderD2RLoaderPolicyResult> ApplyLadderPolicyAsync(
        string? installDirectory,
        IReadOnlyList<LadderExtensionApproval> approvals,
        IReadOnlySet<Guid> selectedApprovalIds,
        CancellationToken cancellationToken = default)
    {
        var restoredCount = RestoreLadderDisabledExtensions(installDirectory);
        var inventory = Discover(installDirectory);
        var matches = await MatchApprovalsAsync(inventory.Extensions, approvals, cancellationToken);
        var approvalByPath = matches.ToDictionary(
            match => match.Extension.FilePath,
            match => match.Approval,
            StringComparer.OrdinalIgnoreCase);
        var unapprovedMoved = new List<D2RLoaderExtensionInfo>();
        var unselectedMoved = new List<D2RLoaderExtensionInfo>();

        foreach (var extension in inventory.Extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!approvalByPath.TryGetValue(extension.FilePath, out var approval))
            {
                MoveToLadderDisabledFolder(inventory, extension);
                unapprovedMoved.Add(extension);
            }
            else if (!selectedApprovalIds.Contains(approval.Id))
            {
                MoveToLadderDisabledFolder(inventory, extension);
                unselectedMoved.Add(extension);
            }
        }

        return new LadderD2RLoaderPolicyResult(unapprovedMoved, unselectedMoved, restoredCount);
    }

    public static int RestoreLadderDisabledExtensions(string? installDirectory)
    {
        var inventory = Discover(installDirectory);
        return RestoreDisabledRoot(inventory.GlobalRoot, inventory.ModRoot)
               + RestoreDisabledRoot(inventory.ModRoot, inventory.GlobalRoot);
    }

    private static D2RLoaderInventory DiscoverForLadderPolicy(string? installDirectory)
    {
        var inventory = Discover(installDirectory);
        var extensions = inventory.Extensions.ToList();
        AddExtensions(
            extensions,
            Path.Combine(inventory.GlobalRoot, LadderDisabledFolderName),
            D2RLoaderExtensionScope.Global,
            isLadderDisabled: true);
        AddExtensions(
            extensions,
            Path.Combine(inventory.ModRoot, LadderDisabledFolderName),
            D2RLoaderExtensionScope.Reimagined,
            isLadderDisabled: true);

        return new D2RLoaderInventory
        {
            InstallDirectory = inventory.InstallDirectory,
            LoaderPath = inventory.LoaderPath,
            GlobalRoot = inventory.GlobalRoot,
            ModRoot = inventory.ModRoot,
            IsInstalled = inventory.IsInstalled,
            Version = inventory.Version,
            AllowGlobalExtensions = inventory.AllowGlobalExtensions,
            AllowModExtensions = inventory.AllowModExtensions,
            Extensions = extensions
        };
    }

    private static async Task<IReadOnlyList<ApprovalMatch>> MatchApprovalsAsync(
        IReadOnlyList<D2RLoaderExtensionInfo> extensions,
        IReadOnlyList<LadderExtensionApproval> approvals,
        CancellationToken cancellationToken)
    {
        var matches = new List<ApprovalMatch>();
        foreach (var extension in extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = approvals.Where(approval =>
                approval.Kind == extension.Kind
                && string.Equals(approval.FileName, extension.FileName, StringComparison.OrdinalIgnoreCase));
            if (!candidates.Any())
            {
                continue;
            }

            var sha256 = await ComputeSha256Async(extension.FilePath, cancellationToken);
            var approval = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Sha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase));
            if (approval is not null)
            {
                matches.Add(new ApprovalMatch(approval, extension));
            }
        }

        return matches;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void MoveToLadderDisabledFolder(
        D2RLoaderInventory inventory,
        D2RLoaderExtensionInfo extension)
    {
        var root = extension.Scope == D2RLoaderExtensionScope.Global
            ? inventory.GlobalRoot
            : inventory.ModRoot;
        var kindFolder = extension.Kind == D2RLoaderExtensionKind.Plugin ? "plugins" : "patches";
        var disabledDirectory = Path.Combine(root, LadderDisabledFolderName, kindFolder);
        var destination = Path.Combine(disabledDirectory, extension.FileName);

        Directory.CreateDirectory(disabledDirectory);
        // The active copy is authoritative. A held copy can be left behind by an
        // interrupted policy change or by moving the extension between scopes.
        File.Move(extension.FilePath, destination, overwrite: true);
    }

    private static int RestoreDisabledRoot(string root, string alternateActiveRoot)
    {
        var disabledRoot = Path.Combine(root, LadderDisabledFolderName);
        return RestoreDisabledKind(disabledRoot, root, alternateActiveRoot, "plugins", "*.dll")
               + RestoreDisabledKind(disabledRoot, root, alternateActiveRoot, "patches", "*.json");
    }

    private static int RestoreDisabledKind(
        string disabledRoot,
        string activeRoot,
        string alternateActiveRoot,
        string kindFolder,
        string pattern)
    {
        var sourceDirectory = Path.Combine(disabledRoot, kindFolder);
        if (!Directory.Exists(sourceDirectory))
        {
            return 0;
        }

        var destinationDirectory = Path.Combine(activeRoot, kindFolder);
        var restoredCount = 0;
        foreach (var source in Directory.GetFiles(sourceDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(source);
            var destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
            {
                continue;
            }

            // D2RLoader loads extensions from both its global folder and the Reimagined
            // mod folder. If the same extension is already active in the other scope,
            // it is already available and must not be restored as a duplicate.
            var alternateDestination = Path.Combine(alternateActiveRoot, kindFolder, fileName);
            if (File.Exists(alternateDestination))
            {
                continue;
            }

            Directory.CreateDirectory(destinationDirectory);
            File.Move(source, destination);
            restoredCount++;
        }

        return restoredCount;
    }

    private sealed record ApprovalMatch(
        LadderExtensionApproval Approval,
        D2RLoaderExtensionInfo Extension);
}
