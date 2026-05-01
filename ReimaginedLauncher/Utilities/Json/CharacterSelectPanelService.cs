using System;
using System.IO;
using System.Text.Json;

namespace ReimaginedLauncher.Utilities.Json;

/// <summary>
/// Resolves the installed D2R Reimagined mod version from the mod's
/// <c>modinfo.json</c> file. Previously this lived inside a UI-layout
/// scraper that parsed <c>characterselectpanelhd.json</c> for a
/// "D2R Reimagined v..." text node; that approach was fragile (any
/// upstream layout edit silently broke version detection and left the
/// CASC fastload manifest stamped with <c>ModVersion: null</c>) so the
/// canonical source of truth is now <c>modinfo.json</c>'s <c>version</c>
/// field. The class name is preserved to keep the public surface stable
/// for existing callers.
/// </summary>
public static class CharacterSelectPanelService
{
    /// <summary>
    /// Reads the <c>version</c> string from the <c>modinfo.json</c>
    /// located at the root of the supplied mod folder (either the D2RMM
    /// mod folder or <c>mods/Reimagined/Reimagined.mpq</c>). Returns
    /// <c>null</c> when the file is missing, unreadable, or does not
    /// expose a string <c>version</c>.
    /// </summary>
    /// <param name="modRoot">
    /// Absolute path to the directory that contains <c>modinfo.json</c>.
    /// </param>
    public static string? GetModVersion(string? modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
            return null;

        var modInfoPath = Path.Combine(modRoot, "modinfo.json");
        return GetModVersionFromFile(modInfoPath);
    }

    /// <summary>
    /// Reads the <c>version</c> string directly from a fully qualified
    /// <c>modinfo.json</c> path. Returns <c>null</c> on any failure so
    /// callers can fall through to alternate locations.
    /// </summary>
    public static string? GetModVersionFromFile(string? modInfoPath)
    {
        if (string.IsNullOrWhiteSpace(modInfoPath) || !File.Exists(modInfoPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                var version = versionElement.GetString();
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
        }
        catch (Exception)
        {
            // Malformed modinfo.json is treated as "unknown" rather than
            // surfacing here — the caller already handles a null return
            // by displaying "version unknown" / leaving manifest entries
            // unstamped, which is the correct conservative behaviour.
        }

        return null;
    }
}
