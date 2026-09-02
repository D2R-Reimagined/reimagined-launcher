using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

internal static class NormalModInstallationService
{
    internal const string RecoveryMessage =
        "The normal Reimagined installation could not be recovered. Reinstall the mod from Nexus Mods using the Updates page before playing Online or Offline. Your character saves have not been moved or deleted.";

    private static string ManagementRoot(string installDirectory) =>
        Path.Combine(installDirectory, ".reimagined-launcher");

    internal static string NormalModRoot(string installDirectory) =>
        Path.Combine(ManagementRoot(installDirectory), "normal-mod", "Reimagined");

    private static string ActiveMarker(string installDirectory) =>
        Path.Combine(ManagementRoot(installDirectory), "ladder-active");

    internal static string BundleStatePath(string installDirectory) =>
        Path.Combine(ManagementRoot(installDirectory), "ladder-bundles", "ladder-bundle-state.json");

    internal static bool HasLadderInstallation(string installDirectory) =>
        File.Exists(ActiveMarker(installDirectory))
        || File.Exists(BundleStatePath(installDirectory))
        || File.Exists(Path.Combine(installDirectory, "mods", "Reimagined", "d2rloader", "ladder-bundle-state.json"));

    internal static string? FindModInfo(string modRoot)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(modRoot, "Reimagined.mpq", "modinfo.json"),
                     Path.Combine(modRoot, "modinfo.json")
                 })
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    internal static bool HasNormalSavePath(string modInfoPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("savepath", out var value)
                || value.ValueKind != JsonValueKind.String)
                return false;

            var savePath = value.GetString()?.Trim().Trim('/', '\\');
            return !string.IsNullOrWhiteSpace(savePath)
                   && !Regex.IsMatch(savePath, "-[0-9a-fA-F]{8}$");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void PreserveBeforeLadderInstall(string installDirectory)
    {
        if (HasLadderInstallation(installDirectory)) return;

        var modRoot = Path.Combine(installDirectory, "mods", "Reimagined");
        var modInfo = FindModInfo(modRoot);
        // An untracked redirect must never become the normal installation.
        if (modInfo is not null && HasNormalSavePath(modInfo))
            ReplaceFromCopy(installDirectory, modRoot, NormalModRoot(installDirectory));

        Directory.CreateDirectory(ManagementRoot(installDirectory));
        File.WriteAllText(ActiveMarker(installDirectory), "ladder");
    }

    internal static void RecordNexusInstallation(string installDirectory)
    {
        var modRoot = Path.Combine(installDirectory, "mods", "Reimagined");
        var modInfo = FindModInfo(modRoot);
        if (modInfo is null || !HasNormalSavePath(modInfo))
            throw new InvalidDataException("The downloaded mod has no valid normal savepath in modinfo.json.");

        ReplaceFromCopy(installDirectory, modRoot, NormalModRoot(installDirectory));
        ClearLadderState(installDirectory);
    }

    internal static void Restore(string installDirectory)
    {
        if (!HasLadderInstallation(installDirectory)) return;

        var normalRoot = NormalModRoot(installDirectory);
        if (FindModInfo(normalRoot) is not { } modInfo || !HasNormalSavePath(modInfo))
            throw new InvalidDataException(RecoveryMessage);

        ReplaceFromCopy(installDirectory, normalRoot, Path.Combine(installDirectory, "mods", "Reimagined"));
        ClearLadderState(installDirectory);
    }

    private static void ClearLadderState(string installDirectory)
    {
        LadderRuntimeFileService.DeleteBaselines(installDirectory);
        foreach (var path in new[]
                 {
                     BundleStatePath(installDirectory),
                     Path.Combine(installDirectory, "mods", "Reimagined", "d2rloader", "ladder-bundle-state.json"),
                     ActiveMarker(installDirectory)
                 })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void ReplaceFromCopy(string installDirectory, string source, string destination)
    {
        var transaction = Path.Combine(ManagementRoot(installDirectory), "mod-backups", Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(transaction, "staged");
        var backup = Path.Combine(transaction, "previous");
        CopyDirectory(source, staged);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var backedUp = false;
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                backedUp = true;
            }

            Directory.Move(staged, destination);
        }
        catch
        {
            if (backedUp && !Directory.Exists(destination))
                Directory.Move(backup, destination);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked mod directories cannot be backed up safely.");

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked mod files cannot be backed up safely.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
