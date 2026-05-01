using System;
using System.IO;
using System.Text.Json;

namespace ReimaginedLauncher.Utilities.Json;

/// <summary>
/// Resolves the installed D2R Reimagined mod version from the mod's <c>modinfo.json</c>.
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
            // Treat malformed modinfo.json as "unknown"; callers handle the null return.
        }

        return null;
    }
}
