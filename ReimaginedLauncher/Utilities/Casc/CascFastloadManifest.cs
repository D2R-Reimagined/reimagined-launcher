using System;
using System.Collections.Generic;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Per-install record of every file the launcher materialised via CASC fastload.
/// Source of truth for delta extract, undo, orphan recovery, and plugin reconciliation.
/// </summary>
public sealed class CascFastloadManifest
{
    /// <summary>
    /// Schema version. Bumped on any breaking shape change so older
    /// launchers can detect (and quarantine) a manifest they cannot read.
    /// </summary>
    public int Schema { get; set; } = CurrentSchema;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdatedUtc { get; set; }

    /// <summary>
    /// CASC product code name (e.g. <c>"d2r"</c>) captured at the time of
    /// the last successful pass. Used together with
    /// <see cref="BuildNumber"/> as the fast-path "nothing changed since
    /// last run" check.
    /// </summary>
    public string? BuildName { get; set; }

    /// <summary>CASC build number captured at the last successful pass.</summary>
    public uint BuildNumber { get; set; }

    /// <summary>
    /// Per-file entries. Path-keyed semantics enforced by
    /// <see cref="CascFastloadManifestService"/> (no duplicate paths).
    /// </summary>
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
    /// <summary>
    /// CASC-relative path, e.g. <c>data\global\excel\armor.txt</c>. Stored
    /// with the casing CascLib reports so cross-platform writers (Linux)
    /// preserve it byte-for-byte.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Hex-encoded 16-byte CASC CKey of the extracted content.</summary>
    public string CKey { get; set; } = string.Empty;

    /// <summary>Decoded file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>
    /// Logical source flags describing who currently owns the on-disk
    /// bytes for this path. Combinations are surfaced in the
    /// <see cref="CascFastloadEntry.Source"/> string as <c>"casc"</c>,
    /// <c>"casc+mod"</c>, <c>"casc+plugin"</c>, <c>"mod+plugin"</c>, etc.
    /// The CASC default is always recorded in
    /// <see cref="CascCKey"/> when present so we can restore it on
    /// disable/uninstall (orphan recovery).
    /// </summary>
    public string Source { get; set; } = SourceTokens.Casc;

    /// <summary>
    /// CASC CKey of the underlying default this path was overlaid onto,
    /// when an overlay is in effect. Null when the path is mod- or
    /// plugin-only with no CASC counterpart.
    /// </summary>
    public string? CascCKey { get; set; }

    /// <summary>Mod version that last wrote this path (<c>"mod"</c> source).</summary>
    public string? ModVersion { get; set; }

    /// <summary>
    /// Identifiers of every enabled plugin that currently claims this
    /// path. Used for conflict detection and reconciliation when a
    /// plugin is disabled or uninstalled.
    /// </summary>
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
