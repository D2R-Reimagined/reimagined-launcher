using System;
using System.IO;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Lightweight description of a single CASC file entry as surfaced through the
/// abstraction. Higher layers (extraction service, manifest, delta, UI) work
/// against this type rather than the raw native struct.
/// </summary>
public sealed record CascFileEntry(
    string Path,
    byte[] CKey,
    byte[] EKey,
    ulong FileSize,
    uint LocaleFlags,
    uint ContentFlags,
    uint FileDataId,
    string? FullName = null);

/// <summary>
/// Storage product info (e.g. "d2r" + build number) used to match BN and
/// Steam installs for offline cross-extract and to invalidate the manifest
/// when the build changes.
/// </summary>
public sealed record CascStorageProduct(string CodeName, uint BuildNumber);

/// <summary>
/// Abstraction over the CascLib native surface so that higher-level services
/// remain testable and degrade gracefully when the native binary is absent.
/// </summary>
/// <remarks>
/// The implementation is provided by <see cref="NativeCascLib"/>; consumers
/// should check <see cref="IsAvailable"/> before invoking any other method.
/// </remarks>
public interface ICascNative
{
    /// <summary>
    /// True when the vendored <c>CascLib.dll</c> (or platform equivalent) was
    /// resolved and basic exports are callable.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Last error message from native load/probe attempts, when
    /// <see cref="IsAvailable"/> is false.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Opens a local CASC storage rooted at <paramref name="storagePath"/>
    /// (the directory containing <c>.build.info</c> or any of its sub-paths).
    /// Returns <c>null</c> when the open fails.
    /// </summary>
    SafeCascStorageHandle? OpenStorage(string storagePath, uint localeMask = CascLocale.All);

    /// <summary>Returns the storage product info, or <c>null</c> if not retrievable.</summary>
    CascStorageProduct? GetStorageProduct(SafeCascStorageHandle storage);

    /// <summary>
    /// Enumerates every file entry visible in the storage. Yields lazily so
    /// the UI can show indexing progress without materialising the entire
    /// list in memory.
    /// </summary>
    System.Collections.Generic.IEnumerable<CascFileEntry> EnumerateFiles(SafeCascStorageHandle storage);

    /// <summary>
    /// Opens, reads, and writes a single CASC file (identified by its
    /// in-storage path) into <paramref name="destination"/>. Returns the
    /// number of bytes written. Throws <see cref="IOException"/> on failure.
    /// </summary>
    long ExtractTo(SafeCascStorageHandle storage, string cascPath, Stream destination, byte[]? buffer = null);
}
