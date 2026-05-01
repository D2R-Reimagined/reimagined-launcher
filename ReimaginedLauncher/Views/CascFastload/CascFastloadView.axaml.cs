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

    public CascFastloadView()
    {
        InitializeComponent();
        _native = new NativeCascLib();
        _extraction = new CascExtractionService(_native);

        Loaded += OnLoaded;
        // Subscribe/unsubscribe in attach/detach so navigating away does NOT
        // cancel an in-flight CASC operation — the singleton state survives,
        // and re-entering the view re-renders it from the live state.
        AttachedToVisualTree += (_, _) =>
        {
            CascFastloadOperationState.Instance.StateChanged += OnOperationStateChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CascFastloadOperationState.Instance.StateChanged -= OnOperationStateChanged;
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    private void OnOperationStateChanged(object? sender, EventArgs e)
    {
        // Already on UI thread (singleton marshals before raising), but be
        // defensive in case future callers raise off-thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            RenderOperationState();
        }
        else
        {
            Dispatcher.UIThread.Post(RenderOperationState);
        }
    }

    /// <summary>
    /// Refreshes everything visible: install path / native status / manifest
    /// summary, plus the live operation panel from the singleton.
    /// </summary>
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

        RenderOperationState();
    }

    private void RenderOperationState()
    {
        var state = CascFastloadOperationState.Instance;

        var status = state.IsRunning
            ? state.StatusMessage
            : (string.IsNullOrEmpty(state.LastResultMessage) ? "Idle." : state.LastResultMessage);
        StatusText.Text = string.IsNullOrEmpty(status) ? "Idle." : status;

        ExtractProgressBar.Value = state.ProgressPercent;
        ProgressDetailText.Text = state.ProgressDetail;
        ProgressEtaText.Text = state.ProgressEta;
        CurrentFileText.Text = state.CurrentFile;

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var hasInstall = !string.IsNullOrWhiteSpace(MainWindow.Settings?.CurrentProfile?.InstallDirectory);
        var running = CascFastloadOperationState.Instance.IsRunning;
        var available = _extraction.IsAvailable && hasInstall && !running;

        ExtractButton.IsEnabled = available;
        UndoButton.IsEnabled = hasInstall && !running;
        CrossInstallButton.IsEnabled = available;
        RefreshButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CascFastloadOperationState.Instance.Cancel();
    }

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        await StartExtractAsync(installDir).ConfigureAwait(false);
    }

    /// <summary>
    /// Public entry point so callers outside the view (e.g. the startup
    /// build-mismatch prompt in <c>MainWindow</c>) can trigger an Extract
    /// against the active install via the same singleton path.
    /// </summary>
    public Task StartExtractAsync(string installDir)
    {
        return CascFastloadOperationState.Instance.TryRunAsync("Extract / Update", async (ct, progress, setStatus) =>
        {
            // Pass the filter's locale opt-in to CascOpenStorage so CascLib's
            // TVFS iterator never enters uninstalled-locale branches. With
            // CascLocale.All (the previous default), CascFindNextFile can
            // hang for tens of seconds resolving a locale entry whose data
            // isn't on disk. The default extraction filter opts out of
            // locale, so the default mask is CascLocale.None.
            var filter = CascExtractionFilter.Default;
            var openMask = filter.IncludeLocal ? filter.LocaleMask : CascLocale.None;
            LaunchDiagnostics.Log($"CASC StartExtract: opening storage at '{installDir}' (localeMask=0x{openMask:X}).");
            using var storage = _extraction.OpenLocal(installDir, openMask);
            if (storage is null || storage.IsInvalid)
            {
                LaunchDiagnostics.Log($"CASC StartExtract: OpenLocal returned null/invalid for '{installDir}'.");
                throw new InvalidOperationException("Failed to open the local CASC storage. Confirm the install path points at the D2R root.");
            }
            LaunchDiagnostics.Log("CASC StartExtract: storage opened, querying product info.");

            var product = _extraction.GetProduct(storage);
            LaunchDiagnostics.Log($"CASC StartExtract: product='{product?.CodeName ?? "(null)"}' build={product?.BuildNumber ?? 0}.");
            var manifestService = new CascFastloadManifestService(installDir);
            var delta = new CascDeltaService(_extraction, manifestService);

            setStatus("Indexing CASC...");
            LaunchDiagnostics.Log("CASC StartExtract: planning delta (will index storage).");
            var indexProgress = new Progress<CascIndexProgress>(ip =>
            {
                // Live heartbeat while walking the TVFS — without this the UI
                // sits frozen for minutes on the static "Indexing CASC..." line.
                CascFastloadOperationState.Instance.SetStatus(
                    $"Indexing CASC... {ip.EntriesSeen:N0} entries seen, {ip.EntriesAccepted:N0} matched");
                CascFastloadOperationState.Instance.SetCurrentFile(ip.CurrentPath);
            });
            var plan = await delta
                .PlanAsync(storage, installDir, CascExtractionFilter.Default, indexProgress, ct)
                .ConfigureAwait(false);

            setStatus(plan.IsNoOp
                ? "Already up to date."
                : $"Applying delta: {plan.Added.Count} added, {plan.Updated.Count} updated, {plan.Restored.Count} restored, {plan.Removed.Count} removed.");

            if (plan.IsNoOp)
            {
                CascFastloadOperationState.Instance.SetResult("Already up to date.");
                return;
            }

            var result = await delta.ApplyAsync(
                storage,
                plan,
                installDir,
                product,
                progress,
                TimeSpan.FromMilliseconds(100),
                ct).ConfigureAwait(false);

            CascFastloadOperationState.Instance.SetResult(
                $"Extract done. +{result.Added} ~{result.Updated} ↺{result.Restored} −{result.Removed} • {CascFastloadOperationState.FormatBytes(result.BytesWritten)} in {CascFastloadOperationState.FormatElapsed(result.Elapsed)}.");
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

        await CascFastloadOperationState.Instance.TryRunAsync("Undo", async (ct, _, setStatus) =>
        {
            var manifestService = new CascFastloadManifestService(installDir);
            var undo = new CascUndoService(manifestService);

            setStatus("Undoing CASC fastload...");
            var result = await undo.UndoAsync(installDir, CascUndoOptions.Default, ct).ConfigureAwait(false);

            CascFastloadOperationState.Instance.SetResult(
                $"Undo complete. Deleted {result.FilesDeleted}, preserved {result.OverlaysPreserved} overlay(s), pruned {result.DirectoriesPruned} dir(s) in {CascFastloadOperationState.FormatElapsed(result.Elapsed)}.");
        }).ConfigureAwait(false);
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

        await CascFastloadOperationState.Instance.TryRunAsync("Cross-extract", async (ct, progress, setStatus) =>
        {
            var manifestService = new CascFastloadManifestService(targetInstall);
            var delta = new CascDeltaService(_extraction, manifestService);
            var cross = new CascCrossInstallService(_native, _extraction, delta);

            var eligibility = cross.CheckEligibility(sourceInstall, targetInstall);
            if (!eligibility.IsEligible)
            {
                throw new InvalidOperationException(BuildIneligibilityMessage(eligibility));
            }

            setStatus($"Cross-extracting from {sourceInstall} (build #{eligibility.SourceProduct?.BuildNumber})...");

            var result = await cross.ApplyAsync(
                sourceInstall,
                targetInstall,
                CascExtractionFilter.Default,
                progress,
                TimeSpan.FromMilliseconds(100),
                CascLocale.All,
                ct).ConfigureAwait(false);

            CascFastloadOperationState.Instance.SetResult(
                $"Cross-extract done. +{result.Added} ~{result.Updated} ↺{result.Restored} −{result.Removed} • {CascFastloadOperationState.FormatBytes(result.BytesWritten)} in {CascFastloadOperationState.FormatElapsed(result.Elapsed)}.");
        }).ConfigureAwait(false);
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
