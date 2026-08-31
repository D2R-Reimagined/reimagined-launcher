using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Points D2R at a per-ladder save folder by rewriting the mod's
/// <c>savepath</c>, so ladder characters and the player's own characters live in
/// physically separate directories and neither is ever moved.
///
/// This replaces an earlier design where the plugin quarantined local saves into
/// a subfolder and moved them back on exit. That could not survive the plugin
/// being removed - the code that undid the move was the code being uninstalled.
/// </summary>
public static class LadderSaveDirectoryService
{
    private const string CleanSuffix = "_launcher_clean";

    /// <summary>Longest slug taken from a ladder's name, before the id suffix.</summary>
    private const int MaxSlugLength = 40;

    /// <summary>
    /// Files that describe how the player likes to play rather than who their
    /// characters are. Seeded into a new ladder folder so a ladder session does
    /// not start with default graphics settings and no loot filters.
    /// </summary>
    private static readonly string[] CarriedFileNames = ["Settings.json", "lootfilter.json"];

    private static readonly string[] CarriedPatterns = ["*.fltr"];

    /// <summary>
    /// Folder name for one ladder, e.g. "ReimaginedThree-Bens-Bitchin-HC-Ladder-d630a3fc".
    /// The id suffix keeps two ladders apart even if their names slugify the same.
    /// </summary>
    public static string BuildLadderSavePath(string baseSavePath, Guid ladderId, string? ladderName)
    {
        var root = baseSavePath.Trim().Trim('/', '\\');
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "Reimagined";
        }

        var slug = Slugify(ladderName);
        var suffix = ladderId.ToString("N")[..8];
        return slug.Length == 0 ? $"{root}-{suffix}" : $"{root}-{slug}-{suffix}";
    }

    /// <summary>
    /// Turns a ladder name into a safe folder fragment. Apostrophes are dropped
    /// rather than replaced so "Ben's" reads as "Bens", not "Ben-s"; every other
    /// run of non-alphanumerics collapses to a single dash.
    /// </summary>
    internal static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is '\'' or '’' or '`' or '"')
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(character);
                if (builder.Length >= MaxSlugLength)
                {
                    break;
                }

                continue;
            }

            pendingDash = true;
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// Redirects the mod at <paramref name="installDirectory"/> to this ladder's
    /// own save folder and seeds it with the player's settings and loot filters.
    /// Returns the resolved ladder save directory, or null on failure.
    /// </summary>
    public static async Task<string?> PrepareAsync(
        string? installDirectory,
        Guid ladderId,
        string? ladderName,
        CancellationToken cancellationToken = default)
    {
        var modInfoPath = ResolveModInfoPath(installDirectory);
        if (modInfoPath is null)
        {
            LaunchDiagnostics.Log("ladder saves: modinfo.json could not be located; the ladder save folder was not prepared.");
            return null;
        }

        try
        {
            var baseSavePath = await ReadBaseSavePathAsync(modInfoPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(baseSavePath))
            {
                LaunchDiagnostics.Log("ladder saves: modinfo.json has no savepath; the ladder save folder was not prepared.");
                return null;
            }

            var ladderSavePath = BuildLadderSavePath(baseSavePath, ladderId, ladderName);
            if (!await WriteSavePathAsync(modInfoPath, ladderSavePath, cancellationToken))
            {
                return null;
            }

            var baseDirectory = ResolveSaveDirectory(baseSavePath);
            var ladderDirectory = ResolveSaveDirectory(ladderSavePath);
            if (ladderDirectory is null)
            {
                return null;
            }

            Directory.CreateDirectory(ladderDirectory);
            SeedPlayerPreferences(baseDirectory, ladderDirectory);

            LaunchDiagnostics.Log($"ladder saves: D2R will use \"{ladderSavePath}\" for this session.");
            return ladderDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LaunchDiagnostics.Log($"ladder saves: could not prepare the ladder save folder: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts the mod back on its normal save folder. Every non-ladder launch must
    /// do this, or the player would find their own characters missing.
    /// </summary>
    public static async Task<bool> RestoreAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var modInfoPath = ResolveModInfoPath(installDirectory);
        if (modInfoPath is null)
        {
            return true;
        }

        try
        {
            var baseSavePath = await ReadBaseSavePathAsync(modInfoPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(baseSavePath))
            {
                return true;
            }

            var current = await ReadSavePathAsync(modInfoPath, cancellationToken);
            if (string.Equals(current, baseSavePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return await WriteSavePathAsync(modInfoPath, baseSavePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LaunchDiagnostics.Log($"ladder saves: could not restore the normal save folder: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The savepath the mod shipped with, taken from a pristine copy made the
    /// first time this ran. Reading it back out of a file we may already have
    /// rewritten would compound the ladder suffix every launch.
    /// </summary>
    private static async Task<string?> ReadBaseSavePathAsync(string modInfoPath, CancellationToken cancellationToken)
    {
        var cleanPath = GetCleanModInfoPath(modInfoPath);
        if (!File.Exists(cleanPath))
        {
            File.Copy(modInfoPath, cleanPath, overwrite: false);
        }

        return await ReadSavePathAsync(cleanPath, cancellationToken);
    }

    private static async Task<string?> ReadSavePathAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var node = JsonNode.Parse(json);
        return node?["savepath"]?.GetValue<string>()?.Trim().Trim('/', '\\');
    }

    private static async Task<bool> WriteSavePathAsync(string modInfoPath, string savePath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(modInfoPath, cancellationToken);
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return false;
        }

        // D2R's own modinfo uses a trailing slash; match it rather than betting
        // on the parser being lenient.
        node["savepath"] = $"{savePath}/";
        await File.WriteAllTextAsync(
            modInfoPath,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken);
        return true;
    }

    internal static string GetCleanModInfoPath(string modInfoPath)
    {
        var directory = Path.GetDirectoryName(modInfoPath)
                        ?? throw new DirectoryNotFoundException("The modinfo.json directory could not be resolved.");
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(modInfoPath)}{CleanSuffix}{Path.GetExtension(modInfoPath)}");
    }

    /// <summary>
    /// Copies graphics/audio settings and loot filters into a ladder folder that
    /// does not have them yet. Existing files are never overwritten, so a player
    /// who tunes settings inside a ladder keeps them.
    /// </summary>
    internal static int SeedPlayerPreferences(string? baseDirectory, string ladderDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return 0;
        }

        var sources = new List<string>();
        foreach (var name in CarriedFileNames)
        {
            var path = Path.Combine(baseDirectory, name);
            if (File.Exists(path))
            {
                sources.Add(path);
            }
        }

        foreach (var pattern in CarriedPatterns)
        {
            sources.AddRange(Directory.EnumerateFiles(baseDirectory, pattern, SearchOption.TopDirectoryOnly));
        }

        var copied = 0;
        foreach (var source in sources)
        {
            var destination = Path.Combine(ladderDirectory, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                continue;
            }

            try
            {
                File.Copy(source, destination);
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"ladder saves: could not carry over {Path.GetFileName(source)}: {exception.Message}");
            }
        }

        if (copied > 0)
        {
            LaunchDiagnostics.Log($"ladder saves: carried {copied} settings/loot-filter file(s) into the ladder folder.");
        }

        return copied;
    }

    /// <summary>Saved Games\Diablo II Resurrected\mods\&lt;savePath&gt;.</summary>
    public static string? ResolveSaveDirectory(string savePath)
    {
        var trimmed = savePath.Trim().Trim('/', '\\');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var savedGamesPath = SaveFileService.GetSavedGamesPath();
        if (string.IsNullOrWhiteSpace(savedGamesPath))
        {
            return null;
        }

        var d2rPath = SaveFileService.ResolveDirectoryCaseInsensitive(savedGamesPath, "Diablo II Resurrected");
        if (d2rPath == null)
        {
            return null;
        }

        var modsPath = SaveFileService.ResolveDirectoryCaseInsensitive(d2rPath, "mods");
        return modsPath == null ? null : Path.Combine(modsPath, trimmed);
    }

    private static string? ResolveModInfoPath(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var modsPath = SaveFileService.ResolveDirectoryCaseInsensitive(normalized, "mods");
        if (modsPath == null)
        {
            return null;
        }

        var reimaginedPath = SaveFileService.ResolveDirectoryCaseInsensitive(modsPath, "Reimagined");
        if (reimaginedPath == null)
        {
            return null;
        }

        var mpqPath = SaveFileService.ResolveDirectoryCaseInsensitive(reimaginedPath, "Reimagined.mpq");
        if (mpqPath == null)
        {
            return null;
        }

        var modInfoPath = Path.Combine(mpqPath, "modinfo.json");
        return File.Exists(modInfoPath) ? modInfoPath : null;
    }
}
