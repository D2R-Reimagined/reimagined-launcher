using System;
using System.Collections.Generic;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Per-install record of every file the launcher materialised via CASC fastload.
/// Source of truth for delta extract, undo, orphan recovery, and plugin reconciliation.
/// </summary>
public sealed class CascFastloadManifest
{
    /// <summary>Schema version; bumped on breaking shape changes so older launchers can quarantine unreadable manifests.</summary>
    public int Schema { get; set; } = CurrentSchema;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdatedUtc { get; set; }

    /// <summary>CASC product code name (e.g. <c>"d2r"</c>) from the last pass; paired with <see cref="BuildNumber"/> for the no-op check.</summary>
    public string? BuildName { get; set; }

    /// <summary>CASC build number captured at the last successful pass.</summary>
    public uint BuildNumber { get; set; }

    /// <summary>Per-file entries; path-keyed (no duplicates) per <see cref="CascFastloadManifestService"/>.</summary>
    public List<CascFastloadEntry> Files { get; set; } = new();

    /// <summary>Latest schema this build of the launcher knows how to read.</summary>
    public const int CurrentSchema = 1;
}

/// <summary>
/// One tracked file under the extracted shadow tree. Fields are kept short
/// for size: at 150k entries even a few extra bytes per row adds up.
/// </summary>
public sealed class CascFastloadEntry
{
    /// <summary>CASC-relative path, e.g. <c>data\global\excel\armor.txt</c>; preserves CascLib casing for cross-platform stability.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Hex-encoded 16-byte CASC CKey of the extracted content.</summary>
    public string CKey { get; set; } = string.Empty;

    /// <summary>Decoded file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>
    /// Owner of the on-disk bytes; '+'-joined combinations of <c>casc</c>/<c>mod</c>/<c>plugin</c>. CASC default is preserved in <see cref="CascCKey"/> for orphan recovery.
    /// </summary>
    public string Source { get; set; } = SourceTokens.Casc;

    /// <summary>CKey of the underlying CASC default for an overlaid path; null when the path has no CASC counterpart.</summary>
    public string? CascCKey { get; set; }

    /// <summary>Mod version that last wrote this path (<c>"mod"</c> source).</summary>
    public string? ModVersion { get; set; }

    /// <summary>Enabled plugin ids currently claiming this path; drives conflict detection and reconciliation on disable/uninstall.</summary>
    public List<string>? PluginIds { get; set; }

    public static class SourceTokens
    {
        public const string Casc = "casc";
        public const string Mod = "mod";
        public const string Plugin = "plugin";
        public const string CascAndMod = "casc+mod";
        public const string CascAndPlugin = "casc+plugin";
        public const string ModAndPlugin = "mod+plugin";
        public const string CascAndModAndPlugin = "casc+mod+plugin";
    }
}
