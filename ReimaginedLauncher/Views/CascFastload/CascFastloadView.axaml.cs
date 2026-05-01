using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;

namespace ReimaginedLauncher.Views.CascFastload;

public partial class CascFastloadView : UserControl
{
    private readonly NativeCascLib _native;
    private readonly CascExtractionService _extraction;

    private CancellationTokenSource? _cts;
    private bool _operationInProgress;

    // EWMA throughput (bytes/sec) for ETA smoothing.
    private double _ewmaBytesPerSec;
    private DateTime _lastProgressUtc;
    private long _lastBytesDone;

    public CascFastloadView()
    {
        InitializeComponent();
        _native = new NativeCascLib();
        _extraction = new CascExtractionService(_native);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    public void RefreshState()
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        InstallPathText.Text = string.IsNullOrWhiteSpace(installDir)
            ? "(not configured)"
            : installDir;

        if (_extraction.IsAvailable)
        {
            UnavailableBanner.IsVisible = false;
            NativeStatusText.Text = "Loaded.";
        }
        else
        {
            UnavailableBanner.IsVisible = true;
            UnavailableReasonText.Text = _extraction.UnavailableReason ?? "Native CascLib binary is not available.";
            NativeStatusText.Text = "Unavailable.";
        }

        // Manifest summary (best-effort; absence is normal pre-extract).
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            try
            {
                var manifestService = new CascFastloadManifestService(installDir);
                var manifest = manifestService.LoadAsync().GetAwaiter().GetResult();
                if (manifest.Files.Count == 0 && manifest.LastUpdatedUtc == default)
                {
                    LastExtractionText.Text = "(never)";
                    FilesTrackedText.Text = "0";
                    BuildText.Text = "(unknown — run Extract to populate)";
                }
                else
                {
                    LastExtractionText.Text = manifest.LastUpdatedUtc.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    FilesTrackedText.Text = manifest.Files.Count.ToString(CultureInfo.InvariantCulture);
                    BuildText.Text = string.IsNullOrWhiteSpace(manifest.BuildName)
                        ? $"#{manifest.BuildNumber}"
                        : $"{manifest.BuildName} (#{manifest.BuildNumber})";
                }
            }
            catch (Exception ex)
            {
                LastExtractionText.Text = "(error)";
                FilesTrackedText.Text = "—";
                BuildText.Text = $"(failed to load manifest: {ex.Message})";
            }
        }
        else
        {
            LastExtractionText.Text = "—";
            FilesTrackedText.Text = "—";
            BuildText.Text = "—";
        }

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var hasInstall = !string.IsNullOrWhiteSpace(MainWindow.Settings?.CurrentProfile?.InstallDirectory);
        var available = _extraction.IsAvailable && hasInstall && !_operationInProgress;

        ExtractButton.IsEnabled = available;
        UndoButton.IsEnabled = hasInstall && !_operationInProgress;
        CrossInstallButton.IsEnabled = available;
        RefreshButton.IsEnabled = !_operationInProgress;
        CancelButton.IsEnabled = _operationInProgress;
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            StatusText.Text = "Cancelling...";
        }
        catch
        {
            // No-op: token may already be disposed.
        }
    }

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        await RunOperationAsync("Extract / Update", async ct =>
        {
            using var storage = _extraction.OpenLocal(installDir);
            if (storage is null || storage.IsInvalid)
            {
                throw new InvalidOperationException("Failed to open the local CASC storage. Confirm the install path points at the D2R root.");
            }

            var product = _extraction.GetProduct(storage);
            var manifestService = new CascFastloadManifestService(installDir);
            var delta = new CascDeltaService(_extraction, manifestService);

            StatusText.Text = "Indexing CASC...";
            var plan = await delta.PlanAsync(storage, installDir, CascExtractionFilter.Default, ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = plan.IsNoOp
                    ? "Already up to date."
                    : $"Applying delta: {plan.Added.Count} added, {plan.Updated.Count} updated, {plan.Restored.Count} restored, {plan.Removed.Count} removed.";
            });

            if (plan.IsNoOp)
            {
                return;
            }

            var progress = new Progress<CascProgress>(OnProgress);
            ResetEwma();

            var result = await delta.ApplyAsync(
                storage,
                plan,
                installDir,
                product,
                progress,
                TimeSpan.FromMilliseconds(100),
                ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"Done. +{result.Added} ~{result.Updated} ↺{result.Restored} −{result.Removed} • {FormatBytes(result.BytesWritten)} in {FormatElapsed(result.Elapsed)}.";
            });
        });
    }

    private async void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        await RunOperationAsync("Undo", async ct =>
        {
            var manifestService = new CascFastloadManifestService(installDir);
            var undo = new CascUndoService(manifestService);

            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = "Undoing CASC fastload...");

            var result = await undo.UndoAsync(installDir, CascUndoOptions.Default, ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"Undo complete. Deleted {result.FilesDeleted}, preserved {result.OverlaysPreserved} overlay(s), pruned {result.DirectoriesPruned} dir(s) in {FormatElapsed(result.Elapsed)}.";
                ExtractProgressBar.Value = 0;
                ProgressDetailText.Text = string.Empty;
                ProgressEtaText.Text = string.Empty;
                CurrentFileText.Text = string.Empty;
            });
        });
    }

    private async void OnCrossInstallClick(object? sender, RoutedEventArgs e)
    {
        var targetInstall = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(targetInstall) || !Directory.Exists(targetInstall))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Source D2R install (e.g. Battle.net)",
            AllowMultiple = false
        });

        if (folders.Count <= 0)
        {
            return;
        }

        var sourceInstall = folders[0].Path.LocalPath;

        await RunOperationAsync("Cross-extract", async ct =>
        {
            var manifestService = new CascFastloadManifestService(targetInstall);
            var delta = new CascDeltaService(_extraction, manifestService);
            var cross = new CascCrossInstallService(_native, _extraction, delta);

            var eligibility = cross.CheckEligibility(sourceInstall, targetInstall);
            if (!eligibility.IsEligible)
            {
                throw new InvalidOperationException(BuildIneligibilityMessage(eligibility));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"Cross-extracting from {sourceInstall} (build #{eligibility.SourceProduct?.BuildNumber})...";
            });

            var progress = new Progress<CascProgress>(OnProgress);
            ResetEwma();

            var result = await cross.ApplyAsync(
                sourceInstall,
                targetInstall,
                CascExtractionFilter.Default,
                progress,
                TimeSpan.FromMilliseconds(100),
                CascLocale.All,
                ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"Cross-extract done. +{result.Added} ~{result.Updated} ↺{result.Restored} −{result.Removed} • {FormatBytes(result.BytesWritten)} in {FormatElapsed(result.Elapsed)}.";
            });
        });
    }

    private async Task RunOperationAsync(string label, Func<CancellationToken, Task> body)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        _cts = new CancellationTokenSource();
        UpdateButtonStates();

        try
        {
            ExtractProgressBar.Value = 0;
            ProgressDetailText.Text = string.Empty;
            ProgressEtaText.Text = string.Empty;
            CurrentFileText.Text = string.Empty;
            StatusText.Text = $"{label}: starting...";

            await body(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{label} cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{label} failed: {ex.Message}";
            Notifications.SendNotification($"CASC {label} failed: {ex.Message}", "Error");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _operationInProgress = false;
            UpdateButtonStates();
            RefreshState();
        }
    }

    private void OnProgress(CascProgress p)
    {
        // Marshal to UI thread; throttling is already done by the service (~10 Hz).
        Dispatcher.UIThread.Post(() =>
        {
            var nowUtc = DateTime.UtcNow;
            var deltaSec = (nowUtc - _lastProgressUtc).TotalSeconds;
            var deltaBytes = p.BytesDone - _lastBytesDone;

            if (_lastProgressUtc != default && deltaSec > 0 && deltaBytes >= 0)
            {
                var instant = deltaBytes / deltaSec;
                // EWMA with alpha ~0.3 (~5 s smoothing at 10 Hz).
                _ewmaBytesPerSec = _ewmaBytesPerSec <= 0
                    ? instant
                    : (0.3 * instant) + (0.7 * _ewmaBytesPerSec);
            }

            _lastProgressUtc = nowUtc;
            _lastBytesDone = p.BytesDone;

            var pct = p.BytesTotal > 0
                ? Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0.0, 100.0)
                : 0.0;
            ExtractProgressBar.Value = pct;

            ProgressDetailText.Text =
                $"{p.FilesDone:N0} / {p.FilesTotal:N0} files • {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)} • {FormatBytes((long)_ewmaBytesPerSec)}/s";

            ProgressEtaText.Text = ComputeEtaText(p, _ewmaBytesPerSec);
            CurrentFileText.Text = p.CurrentPath ?? string.Empty;
        });
    }

    private void ResetEwma()
    {
        _ewmaBytesPerSec = 0;
        _lastProgressUtc = default;
        _lastBytesDone = 0;
    }

    private static string ComputeEtaText(CascProgress p, double ewmaBytesPerSec)
    {
        if (ewmaBytesPerSec <= 0 || p.BytesTotal <= p.BytesDone)
        {
            return string.Empty;
        }

        var remaining = p.BytesTotal - p.BytesDone;
        var seconds = remaining / ewmaBytesPerSec;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return string.Empty;
        }

        var eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds));
        return $"ETA {FormatElapsed(eta)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double KiB = 1024d;
        const double MiB = 1024d * 1024d;
        const double GiB = 1024d * 1024d * 1024d;

        if (bytes >= GiB) return $"{bytes / GiB:F2} GiB";
        if (bytes >= MiB) return $"{bytes / MiB:F1} MiB";
        if (bytes >= KiB) return $"{bytes / KiB:F0} KiB";
        return $"{bytes} B";
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string BuildIneligibilityMessage(CascCrossInstallEligibility eligibility)
    {
        return eligibility.Reason switch
        {
            CascCrossInstallEligibilityReason.NativeUnavailable
                => "Native CascLib binary is not available; cross-extract is disabled.",
            CascCrossInstallEligibilityReason.InvalidInstallPaths
                => "Source and target install paths must both be set and distinct.",
            CascCrossInstallEligibilityReason.SourceOpenFailed
                => "Failed to open the source install's CASC storage.",
            CascCrossInstallEligibilityReason.TargetOpenFailed
                => "Failed to open the target install's CASC storage.",
            CascCrossInstallEligibilityReason.SourceProductMissing
                => "Could not read the source install's CASC build descriptor.",
            CascCrossInstallEligibilityReason.TargetProductMissing
                => "Could not read the target install's CASC build descriptor.",
            CascCrossInstallEligibilityReason.BuildMismatch
                => $"Build mismatch — source {DescribeProduct(eligibility.SourceProduct)} vs target {DescribeProduct(eligibility.TargetProduct)}. Update the lagging install via its own client and try again.",
            _ => $"Cross-extract refused: {eligibility.Reason}."
        };
    }

    private static string DescribeProduct(CascStorageProduct? product)
    {
        if (product is null)
        {
            return "(unknown)";
        }

        var name = string.IsNullOrEmpty(product.CodeName) ? "(unnamed)" : product.CodeName;
        return $"{name} #{product.BuildNumber}";
    }
}
