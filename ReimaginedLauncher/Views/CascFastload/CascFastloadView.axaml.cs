using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;

namespace ReimaginedLauncher.Views.CascFastload;

public partial class CascFastloadView : UserControl
{
    private readonly NativeCascLib _native;
    private readonly CascExtractionService _extraction;

    // True when the persisted manifest has entries; gates the "Update (Delta)" button.
    private bool _manifestHasEntries;

    public CascFastloadView()
    {
        InitializeComponent();
        _native = new NativeCascLib();
        _extraction = new CascExtractionService(_native);

        Loaded += OnLoaded;
        // Attach/detach only — the singleton state survives navigation; an in-flight op is not cancelled.
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
        // Singleton marshals to UI thread; defensive re-dispatch in case that ever changes.
        if (Dispatcher.UIThread.CheckAccess())
        {
            RenderOperationState();
        }
        else
        {
            Dispatcher.UIThread.Post(RenderOperationState);
        }
    }

    // Refresh install path / native status / manifest summary / live operation panel.
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
                _manifestHasEntries = manifest.Files.Count > 0;
                if (manifest.Files.Count == 0)
                {
                    LastExtractionText.Text = "(never)";
                    FilesTrackedText.Text = "0";
                    BuildText.Text = "(unknown — run Extract CASC to populate)";
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
                _manifestHasEntries = false;
                LastExtractionText.Text = "(error)";
                FilesTrackedText.Text = "—";
                BuildText.Text = $"(failed to load manifest: {ex.Message})";
            }
        }
        else
        {
            _manifestHasEntries = false;
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
        var profile = MainWindow.Settings?.CurrentProfile;
        var hasInstall = !string.IsNullOrWhiteSpace(profile?.InstallDirectory);
        var running = CascFastloadOperationState.Instance.IsRunning;
        var available = _extraction.IsAvailable && hasInstall && !running;

        ExtractButton.IsEnabled = available;
        UpdateButton.IsEnabled = available && _manifestHasEntries;
        UndoButton.IsEnabled = hasInstall && !running;
        // Cross-extract is BN -> Steam only: enabled on Steam profile when a sibling BN install exists.
        CrossInstallButton.IsEnabled =
            available &&
            profile?.Type == InstallationType.Steam &&
            TryResolveBattleNetSiblingInstall() is not null;
        RefreshButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    /// <summary>Returns a validated Battle.net install dir distinct from the current profile, or <c>null</c>; gates the cross-extract button.</summary>
    private static string? TryResolveBattleNetSiblingInstall()
    {
        var settings = MainWindow.Settings;
        if (settings is null)
        {
            return null;
        }

        var current = settings.CurrentProfile;
        foreach (var p in settings.Profiles)
        {
            if (p.Type != InstallationType.BattleNet)
            {
                continue;
            }
            if (!p.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(p.InstallDirectory))
            {
                continue;
            }
            if (!Directory.Exists(p.InstallDirectory))
            {
                continue;
            }
            if (string.Equals(
                    InstallDirectoryValidator.NormalizeInstallDirectory(p.InstallDirectory) ?? p.InstallDirectory,
                    InstallDirectoryValidator.NormalizeInstallDirectory(current.InstallDirectory) ?? current.InstallDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return p.InstallDirectory;
        }

        return null;
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CascFastloadOperationState.Instance.Cancel();
    }

    // "Extract CASC" handler: destructive bootstrap that wipes Reimagined.mpq\data\ then re-extracts.
    private async void OnExtractCascClick(object? sender, RoutedEventArgs e)
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        var confirmed = await ShowDestructiveExtractPromptAsync().ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        // Run the wipe + re-extract on the background pipeline; snapshot UI text up front because
        // the worker body must not touch controls.
        var scopeText = ScopePrefixesTextBox?.Text ?? string.Empty;
        await CascFastloadOperationState.Instance.TryRunAsync("Extract / Update", async (ct, progress, setStatus) =>
        {
            setStatus("Preparing: removing previous extraction...");
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                UninstallReimaginedModForFastloadBootstrap(installDir);
            }, ct).ConfigureAwait(false);

            // Refresh "mod installed?" on the UI thread so the Launch button is greyed out until apply.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    MainWindow.Instance?.RefreshLocalModState();
                }
                catch (Exception ex)
                {
                    LaunchDiagnostics.Log($"CASC Extract CASC: RefreshLocalModState threw: {ex.Message}");
                }
            });

            await RunExtractBodyAsync(installDir, scopeText, ct, progress, setStatus).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // "Update (Delta)" handler: non-destructive delta extraction; gated on a non-empty manifest.
    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        var installDir = MainWindow.Settings?.CurrentProfile?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            Notifications.SendNotification("CASC fastload: no install directory is configured.", "Warning");
            return;
        }

        if (!_manifestHasEntries)
        {
            // Defensive: button should be disabled; redirect to Extract CASC instead of running destructively.
            Notifications.SendNotification(
                "No fastload manifest yet. Use Extract CASC for the initial extraction.",
                "Information");
            return;
        }

        await StartExtractAsync(installDir).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the destructive-extract confirmation dialog and returns the
    /// user's choice. Owner is the top-level Window so the dialog modals
    /// the launcher.
    /// </summary>
    private async Task<bool> ShowDestructiveExtractPromptAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            // No window to host a modal dialog — assume the user did not
            // confirm rather than silently proceeding with a destructive op.
            return false;
        }

        var dialog = new Window
        {
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "CASC fastload — first-time extraction is destructive"
        };

        var continueButton = new Button { Content = "Continue", Classes = { "accent" }, MinWidth = 110 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };

        continueButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new Border
        {
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Initial CASC fastload extraction will replace your Reimagined mod data with vanilla files.",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Because there is no fastload manifest yet, the launcher cannot tell which files the mod authored. To produce a clean baseline it will:" + Environment.NewLine +
                               "  • delete modinfo.json from mods\\Reimagined\\Reimagined.mpq" + Environment.NewLine +
                               "  • wipe the Reimagined.mpq\\data\\ tree" + Environment.NewLine +
                               "  • extract vanilla CASC files into Reimagined.mpq\\data\\" + Environment.NewLine + Environment.NewLine +
                               "Afterwards the launcher will report the mod as not installed. Run Install/Update to reapply the mod on top of the fast-loaded vanilla baseline. Future mod and D2R updates will then be incremental.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { cancelButton, continueButton }
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    // Wipes modinfo.json + Reimagined.mpq\data\ so the next extract starts clean.
    private static void UninstallReimaginedModForFastloadBootstrap(string installDir)
    {
        var mpqRoot = Path.Combine(installDir, "mods", "Reimagined", "Reimagined.mpq");
        var modinfoPath = Path.Combine(mpqRoot, "modinfo.json");
        if (File.Exists(modinfoPath))
        {
            File.Delete(modinfoPath);
            LaunchDiagnostics.Log($"CASC Extract bootstrap: deleted '{modinfoPath}'.");
        }

        var dataDir = Path.Combine(mpqRoot, "data");
        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
            LaunchDiagnostics.Log($"CASC Extract bootstrap: wiped '{dataDir}'.");
        }

        // Pre-create the data root so the extractor's early heartbeat output stays clean.
        Directory.CreateDirectory(dataDir);
    }

    // Public entry point so external callers (e.g. startup build-mismatch prompt) can trigger an Extract.
    public Task StartExtractAsync(string installDir)
    {
        // Snapshot scope on the UI thread; the worker must not touch controls. Empty = default scope.
        var scopeText = ScopePrefixesTextBox?.Text ?? string.Empty;
        return CascFastloadOperationState.Instance.TryRunAsync("Extract / Update",
            (ct, progress, setStatus) => RunExtractBodyAsync(installDir, scopeText, ct, progress, setStatus));
    }

    // Core extract body; must run on a worker thread (via TryRunAsync) and not touch controls.
    private async Task RunExtractBodyAsync(
        string installDir,
        string scopeText,
        CancellationToken ct,
        IProgress<CascProgress> progress,
        Action<string> setStatus)
    {
        // Pass locale opt-in to CascOpenStorage so the TVFS iterator skips uninstalled-locale branches.
            var prefixes = ParseScopePrefixes(scopeText);
            var filter = prefixes.Count == 0
                ? CascExtractionFilter.Default
                : CascExtractionFilter.Default with { PathPrefixes = prefixes };
            if (prefixes.Count > 0)
            {
                LaunchDiagnostics.Log($"CASC StartExtract: scoping to {prefixes.Count} prefix(es): {string.Join(" ; ", prefixes)}");
            }
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

            // Fastload bytes go inside the Reimagined mod tree so D2R's overlay path resolution composes.
            var modRoot = Path.Combine(installDir, "mods", "Reimagined", "Reimagined.mpq");
            Directory.CreateDirectory(modRoot);
            LaunchDiagnostics.Log($"CASC StartExtract: destination root='{modRoot}'.");

            var manifestService = new CascFastloadManifestService(installDir);
            var delta = new CascDeltaService(_extraction, manifestService);

            setStatus("Indexing CASC...");
            LaunchDiagnostics.Log("CASC StartExtract: planning delta (will index storage).");
            var indexProgress = new Progress<CascIndexProgress>(ip =>
            {
                // Live heartbeat while walking the TVFS so the UI doesn't appear frozen.
                CascFastloadOperationState.Instance.SetStatus(
                    $"Indexing CASC... {ip.EntriesSeen:N0} entries seen, {ip.EntriesAccepted:N0} matched");
                CascFastloadOperationState.Instance.SetCurrentFile(ip.CurrentPath);
            });
            var plan = await delta
                .PlanAsync(storage, modRoot, filter, indexProgress, ct)
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
                setStatus,
                modRoot,
                product,
                progress,
                TimeSpan.FromMilliseconds(100),
                ct).ConfigureAwait(false);

            CascFastloadOperationState.Instance.SetResult(
                $"Extract done. +{result.Added} ~{result.Updated} ↺{result.Restored} −{result.Removed} • {CascFastloadOperationState.FormatBytes(result.BytesWritten)} in {CascFastloadOperationState.FormatElapsed(result.Elapsed)}.");
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

            // Throttle progress text to ~1 Hz to avoid UI thrash on the 500-entry heartbeat.
            var lastUiUpdate = DateTime.MinValue;
            var progressSink = new Progress<CascUndoProgress>(p =>
            {
                var now = DateTime.UtcNow;
                if (p.EntriesProcessed < p.EntriesTotal && (now - lastUiUpdate).TotalMilliseconds < 1000)
                {
                    return;
                }
                lastUiUpdate = now;
                setStatus($"Undoing CASC fastload... {p.EntriesProcessed:N0} / {p.EntriesTotal:N0} entries ({p.FilesDeleted:N0} deleted)");
            });

            var modRoot = Path.Combine(installDir, "mods", "Reimagined", "Reimagined.mpq");
            var result = await undo.UndoAsync(modRoot, CascUndoOptions.Default, progressSink, ct).ConfigureAwait(false);

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

        // Defensive recheck of the Battle.net sibling source (already gated by UpdateButtonStates).
        var sourceInstall = TryResolveBattleNetSiblingInstall();
        if (string.IsNullOrWhiteSpace(sourceInstall))
        {
            Notifications.SendNotification(
                "Cross-extract requires a configured Battle.net install. Validate the Battle.net profile and try again.",
                "Warning");
            return;
        }

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

    // Parses the scope textbox: ';' or newline-separated path prefixes; '/' normalised to '\\'.
    private static IReadOnlyList<string> ParseScopePrefixes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var separators = new[] { ';', '\r', '\n' };
        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            var p = raw.Replace('/', '\\').Trim();
            if (p.Length > 0)
            {
                result.Add(p);
            }
        }

        return result;
    }
}
