using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Outcome of <see cref="CascCrossInstallService.CheckEligibility"/>. The UI
/// surfaces these reasons verbatim so the user knows exactly why a BN→Steam
/// (or Steam→BN) offline cross-extract is or isn't possible.
/// </summary>
public enum CascCrossInstallEligibilityReason
{
    /// <summary>Both installs opened cleanly and report the same build.</summary>
    Eligible,

    /// <summary>The native CascLib binary isn't loadable on this machine.</summary>
    NativeUnavailable,

    /// <summary>
    /// One or both install paths were null/empty/whitespace, or pointed at
    /// the same directory (cross-extract requires two distinct installs).
    /// </summary>
    InvalidInstallPaths,

    /// <summary><see cref="ICascNative.OpenStorage"/> failed for the source install.</summary>
    SourceOpenFailed,

    /// <summary><see cref="ICascNative.OpenStorage"/> failed for the target install.</summary>
    TargetOpenFailed,

    /// <summary><see cref="ICascNative.GetStorageProduct"/> returned null for the source.</summary>
    SourceProductMissing,

    /// <summary><see cref="ICascNative.GetStorageProduct"/> returned null for the target.</summary>
    TargetProductMissing,

    /// <summary>
    /// Both storages opened but their build descriptors disagree. Cross-extract
    /// is refused (per the design — falling back to online would mask the
    /// mismatch and risk extracting against the wrong build).
    /// </summary>
    BuildMismatch
}

/// <summary>
/// Eligibility verdict for a cross-install extract attempt. When
/// <see cref="Reason"/> is <see cref="CascCrossInstallEligibilityReason.Eligible"/>
/// both <see cref="SourceProduct"/> and <see cref="TargetProduct"/> are
/// populated and identical (same <c>CodeName</c> and <c>BuildNumber</c>).
/// On <see cref="CascCrossInstallEligibilityReason.BuildMismatch"/> both
/// descriptors are populated so the UI can show "BN build X vs Steam build Y".
/// </summary>
public sealed record CascCrossInstallEligibility(
    CascCrossInstallEligibilityReason Reason,
    CascStorageProduct? SourceProduct,
    CascStorageProduct? TargetProduct,
    string? Message = null)
{
    public bool IsEligible => Reason == CascCrossInstallEligibilityReason.Eligible;
}

/// <summary>
/// Phase 1f — orchestrates a fully-offline BN↔Steam cross-extraction.
/// </summary>
/// <remarks>
/// <para>
/// Both Battle.net and Steam ship byte-identical CASC content for any matching
/// build (same <c>buildName</c> + <c>buildNumber</c> in
/// <see cref="CascStorageProduct"/>), so when a user has both installs and
/// they're at the same version we can satisfy the slower install's fastload
/// extraction by reading from the faster install's local CASC — no internet
/// required, no online CDN fallback, no risk of a Steam-vs-CDN build skew.
/// </para>
/// <para>
/// On a build mismatch the service refuses with both descriptors so the UI
/// can prompt the user to update the lagging install via its own client. The
/// design intentionally does <i>not</i> auto-fall-back to online extraction —
/// that decision belongs to the user via a separate UI action once they're
/// aware of the mismatch.
/// </para>
/// </remarks>
public sealed class CascCrossInstallService
{
    private readonly ICascNative _native;
    private readonly CascExtractionService _extraction;
    private readonly CascDeltaService _delta;

    public CascCrossInstallService(
        ICascNative native,
        CascExtractionService extraction,
        CascDeltaService delta)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(delta);
        _native = native;
        _extraction = extraction;
        _delta = delta;
    }

    public bool IsAvailable => _native.IsAvailable;

    /// <summary>
    /// Opens both installs, compares their build descriptors, and returns
    /// the eligibility verdict. The handles are disposed before returning so
    /// callers can safely re-open them in <see cref="ApplyAsync"/>.
    /// </summary>
    public CascCrossInstallEligibility CheckEligibility(
        string sourceInstallDirectory,
        string targetInstallDirectory,
        uint localeMask = CascLocale.All)
    {
        if (!_native.IsAvailable)
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.NativeUnavailable,
                null,
                null,
                _native.UnavailableReason);
        }

        if (string.IsNullOrWhiteSpace(sourceInstallDirectory) ||
            string.IsNullOrWhiteSpace(targetInstallDirectory) ||
            string.Equals(
                System.IO.Path.GetFullPath(sourceInstallDirectory),
                System.IO.Path.GetFullPath(targetInstallDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.InvalidInstallPaths,
                null,
                null);
        }

        using var source = _extraction.OpenLocal(sourceInstallDirectory, localeMask);
        if (source is null || source.IsInvalid)
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.SourceOpenFailed,
                null,
                null);
        }

        using var target = _extraction.OpenLocal(targetInstallDirectory, localeMask);
        if (target is null || target.IsInvalid)
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.TargetOpenFailed,
                null,
                null);
        }

        var sourceProduct = _extraction.GetProduct(source);
        if (sourceProduct is null)
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.SourceProductMissing,
                null,
                null);
        }

        var targetProduct = _extraction.GetProduct(target);
        if (targetProduct is null)
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.TargetProductMissing,
                sourceProduct,
                null);
        }

        if (!ProductsMatch(sourceProduct, targetProduct))
        {
            return new CascCrossInstallEligibility(
                CascCrossInstallEligibilityReason.BuildMismatch,
                sourceProduct,
                targetProduct);
        }

        return new CascCrossInstallEligibility(
            CascCrossInstallEligibilityReason.Eligible,
            sourceProduct,
            targetProduct);
    }

    /// <summary>
    /// Performs a build-matched, offline cross-extraction: opens the source
    /// CASC, plans against the <i>target</i> install's manifest +
    /// <c>destinationRoot</c>, and extracts from the source storage into the
    /// target install directory. Throws <see cref="InvalidOperationException"/>
    /// when the eligibility check fails — callers are expected to surface
    /// <see cref="CheckEligibility"/>'s verdict to the user before invoking
    /// this method.
    /// </summary>
    public async Task<CascDeltaApplyResult> ApplyAsync(
        string sourceInstallDirectory,
        string targetInstallDirectory,
        CascExtractionFilter? filter = null,
        IProgress<CascProgress>? progress = null,
        TimeSpan progressInterval = default,
        uint localeMask = CascLocale.All,
        CancellationToken cancellationToken = default)
    {
        var eligibility = CheckEligibility(sourceInstallDirectory, targetInstallDirectory, localeMask);
        if (!eligibility.IsEligible)
        {
            throw new InvalidOperationException(
                $"Cross-install extract refused: {eligibility.Reason}. " +
                $"Source build: {Describe(eligibility.SourceProduct)}; " +
                $"Target build: {Describe(eligibility.TargetProduct)}.");
        }

        using var source = _extraction.OpenLocal(sourceInstallDirectory, localeMask)
            ?? throw new InvalidOperationException("Source CASC storage failed to open after eligibility check.");

        // Fastload bytes belong inside the target install's Reimagined mod
        // tree so D2R's mod overlay path resolution composes correctly with
        // mod updates and orphan recovery on the target side.
        var targetModRoot = System.IO.Path.Combine(targetInstallDirectory, "mods", "Reimagined", "Reimagined.mpq");
        System.IO.Directory.CreateDirectory(targetModRoot);

        var plan = await _delta
            .PlanAsync(source, targetModRoot, filter, indexProgress: null, cancellationToken)
            .ConfigureAwait(false);

        return await _delta
            .ApplyAsync(
                source,
                plan,
                setStatus: null,
                targetModRoot,
                eligibility.SourceProduct,
                progress,
                progressInterval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ProductsMatch(CascStorageProduct a, CascStorageProduct b)
    {
        return string.Equals(a.CodeName ?? string.Empty, b.CodeName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && a.BuildNumber == b.BuildNumber;
    }

    private static string Describe(CascStorageProduct? product)
    {
        if (product is null)
        {
            return "<unknown>";
        }

        var name = string.IsNullOrEmpty(product.CodeName) ? "<unnamed>" : product.CodeName;
        return $"{name} (#{product.BuildNumber})";
    }
}
