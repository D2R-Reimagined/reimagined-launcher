using System;
using System.IO;
using System.Text.Json;

namespace ReimaginedLauncher.Utilities.Json;

/// <summary>
/// Resolves the installed D2R Reimagined mod version from the mod's <c>modinfo.json</c>.
/// </summary>
public static class CharacterSelectPanelService
{
    /// <summary>Reads the <c>version</c> string from <paramref name="modRoot"/>/<c>modinfo.json</c>; <c>null</c> when missing/unreadable.</summary>
    public static string? GetModVersion(string? modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
            return null;

        var modInfoPath = Path.Combine(modRoot, "modinfo.json");
        return GetModVersionFromFile(modInfoPath);
    }

    /// <summary>Reads the <c>version</c> string from a full <c>modinfo.json</c> path; returns <c>null</c> on any failure.</summary>
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
