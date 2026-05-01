using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;
using ReimaginedLauncher.Utilities.Json;

namespace ReimaginedLauncher.Views.Update;

public partial class UpdateView : UserControl
{
    private bool _isLoading;

    public UpdateView()
    {
        InitializeComponent();
        RefreshUpdateState();
    }

    private void OnCascStateChanged(object? sender, EventArgs e)
    {
        // Re-evaluate gated install/update buttons whenever a CASC fastload
        // operation starts or stops so the user can't kick off an install
        // mid-extraction.
        RefreshUpdateState();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CascFastloadOperationState.Instance.StateChanged += OnCascStateChanged;
        RefreshUpdateState();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        CascFastloadOperationState.Instance.StateChanged -= OnCascStateChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public void RefreshUpdateState()
    {
        var isAuthenticated = MainWindow.UserViewModel.User != null;
        var usesDownloadsWatcher = isAuthenticated && MainWindow.Settings.NexusPremiumDownloadAccess == false;
        var isInstallMissing = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase);
        var canDownload = isInstallMissing || MainWindow.IsUpdateAvailable;

        LoadingBanner.IsVisible = _isLoading;
        InstallProgressBanner.IsVisible = MainWindow.IsInstallInProgress;
        StatusBorder.IsVisible = !_isLoading && !MainWindow.IsInstallInProgress;
        VersionsBorder.IsVisible = !_isLoading && !MainWindow.IsInstallInProgress;
        AuthWarningBanner.IsVisible = !_isLoading && !MainWindow.IsInstallInProgress && !isAuthenticated;
        NonPremiumWarningBanner.IsVisible = !_isLoading && !MainWindow.IsInstallInProgress && usesDownloadsWatcher && canDownload;

        StatusTitleText.Text = MainWindow.UpdateStatusTitle;
        StatusMessageText.Text = MainWindow.UpdateStatusMessage;
        CurrentVersionText.Text = MainWindow.UpdateCurrentVersion;
        LatestVersionText.Text = MainWindow.UpdateLatestVersion;
        // Block install/update activity while a CASC fastload op is running —
        // they touch overlapping paths under <install>/data and <install>/mods.
        var cascBusy = CascFastloadOperationState.Instance.IsRunning;
        InstallOrUpdateButton.IsEnabled = !_isLoading &&
                                          !cascBusy &&
                                          MainWindow.CanInstallOrUpdate &&
                                          !MainWindow.IsInstallInProgress &&
                                          isAuthenticated &&
                                          canDownload;
        SelectZipManuallyButton.IsEnabled = !_isLoading &&
                                            !cascBusy &&
                                            !MainWindow.IsInstallInProgress &&
                                            MainWindow.Settings.CurrentProfile.IsInstallDirectoryValidated &&
                                            !string.IsNullOrWhiteSpace(MainWindow.Settings.CurrentProfile.InstallDirectory);
        OpenDownloadPageButton.IsEnabled = !_isLoading && !string.IsNullOrWhiteSpace(MainWindow.UpdateDownloadUrl);
        RecheckButton.IsEnabled = !_isLoading && !cascBusy;
        InstallOrUpdateButton.Content = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase)
            ? "Download and Install"
            : "Download and Update";

        if (MainWindow.IsInstallInProgress)
        {
            InstallProgressTitle.Text = MainWindow.InstallProgressTitle ?? "Installing...";
            InstallProgressMessage.Text = MainWindow.InstallProgressMessage ?? "Please wait while the mod is being installed.";
        }
    }

    public void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        RefreshUpdateState();
    }

    private async void OnInstallOrUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.IsInstallInProgress || string.IsNullOrWhiteSpace(MainWindow.UpdateDownloadUrl))
            return;

        if (MainWindow.UserViewModel.User == null)
        {
            Notifications.SendNotification(
                "Authenticate with Nexus Mods first to use Download and Install.",
                "Warning");
            return;
        }
        
        var profile = MainWindow.Settings.CurrentProfile;
        var installDirectory = profile.InstallDirectory;
        if (!profile.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(installDirectory))
        {
            Notifications.SendNotification(
                "Install directory not validated",
                profile.Type == InstallationType.D2RMM
                    ? "Select the D2RMM mods folder before installing the mod."
                    : "Select the Diablo II: Resurrected folder before installing the mod.");
            return;
        }

        try
        {
            MainWindow.IsInstallInProgress = true;
            RefreshUpdateState();

            if (MainWindow.Settings.NexusPremiumDownloadAccess == false)
            {
                Notifications.SendNotification("Open Manual Download in browser. Waiting for zip in Downloads...", "Info");
                SetInstallProgress("Waiting for download...", "Complete the manual download in your browser. The launcher is watching your Downloads folder.");
                OnOpenDownloadPageClick(null, null);
                var downloadedZip = await WaitForNewZipFromDownloadsAsync(TimeSpan.FromMinutes(8));
                if (string.IsNullOrWhiteSpace(downloadedZip))
                {
                    Notifications.SendNotification("No new zip detected in Downloads. Try again after download completes.", "Warning");
                    return;
                }

                SetInstallProgress("Installing...", "Download detected. Extracting and installing mod files.");
                await ExtractAndFinalizeInstallAsync(downloadedZip, installDirectory);
                return;
            }

            SetInstallProgress("Downloading...", "Downloading mod archive from Nexus Mods. This may take a moment.");
            await DownloadExtractAndFinalizeInstallAsync(MainWindow.UpdateDownloadUrl, installDirectory);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Install failed: {ex.Message}", "Warning");
        }
        finally
        {
            MainWindow.IsInstallInProgress = false;
            MainWindow.InstallProgressTitle = null;
            MainWindow.InstallProgressMessage = null;
            RefreshVisibleUpdateView();
        }
    }

    private void OnOpenDownloadPageClick(object? sender, RoutedEventArgs? e)
    {
        if (string.IsNullOrWhiteSpace(MainWindow.UpdateDownloadUrl))
            return;

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = MainWindow.UpdateDownloadUrl,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception)
        {
            // Keep launcher stable if shell open fails.
        }
    }

    private async void OnSelectZipManuallyClick(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.IsInstallInProgress)
            return;

        var profile = MainWindow.Settings.CurrentProfile;
        var installDirectory = profile.InstallDirectory;
        if (!profile.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(installDirectory))
        {
            Notifications.SendNotification(
                "Install directory not validated",
                profile.Type == InstallationType.D2RMM
                    ? "Select the D2RMM mods folder before installing the mod."
                    : "Select the Diablo II: Resurrected folder before installing the mod.");
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select D2R Reimagined Zip",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zip Archives")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        if (files.Count <= 0)
        {
            return;
        }

        var zipPath = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            Notifications.SendNotification("Selected file could not be accessed locally.", "Warning");
            return;
        }

        try
        {
            MainWindow.IsInstallInProgress = true;
            RefreshUpdateState();
            SetInstallProgress("Installing...", "Extracting and installing mod files from selected zip.");
            await ExtractAndFinalizeInstallAsync(zipPath, installDirectory);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Install failed: {ex.Message}", "Warning");
        }
        finally
        {
            MainWindow.IsInstallInProgress = false;
            MainWindow.InstallProgressTitle = null;
            MainWindow.InstallProgressMessage = null;
            RefreshVisibleUpdateView();
        }
    }

    private async void OnRecheckClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            SetLoadingState(true);
            try
            {
                await mainWindow.RefreshUpdateStateAsync();
            }
            finally
            {
                SetLoadingState(false);
            }
        }
    }

    private static bool IsZipArchive(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4)
            return false;

        Span<byte> signature = stackalloc byte[4];
        var bytesRead = stream.Read(signature);
        return bytesRead == 4 &&
               signature[0] == 0x50 &&
               signature[1] == 0x4B &&
               (signature[2] == 0x03 || signature[2] == 0x05 || signature[2] == 0x07) &&
               (signature[3] == 0x04 || signature[3] == 0x06 || signature[3] == 0x08);
    }

    private async Task DownloadExtractAndFinalizeInstallAsync(string downloadUrl, string installDirectory)
    {
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"reimagined-{Guid.NewGuid():N}.zip");

        try
        {
            SetInstallProgress("Downloading...", "Downloading mod archive. Please wait.");
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var contentStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await contentStream.CopyToAsync(fileStream);
            }

            if (!IsZipArchive(tempZipPath))
            {
                Notifications.SendNotification(
                    "Automatic install needs a direct zip. Opening the download page instead.",
                    "Warning");
                OnOpenDownloadPageClick(null, null);
                return;
            }

            SetInstallProgress("Installing...", "Extracting and installing mod files.");
            await ExtractAndFinalizeInstallAsync(tempZipPath, installDirectory);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private void SetInstallProgress(string title, string message)
    {
        MainWindow.InstallProgressTitle = title;
        MainWindow.InstallProgressMessage = message;

        Dispatcher.UIThread.Post(() =>
        {
            if (MainWindow.Instance?.ContentArea.Content is UpdateView visibleView)
            {
                visibleView.InstallProgressTitle.Text = title;
                visibleView.InstallProgressMessage.Text = message;
                visibleView.InstallProgressBanner.IsVisible = true;
            }
        });
    }

    private async Task ExtractAndFinalizeInstallAsync(string zipPath, string installDirectory)
    {
        if (!IsZipArchive(zipPath))
        {
            Notifications.SendNotification("Downloaded file is not a valid zip archive.", "Warning");
            return;
        }

        SetInstallProgress("Installing...", "Extracting and installing mod files. Please wait.");

        var profile = MainWindow.Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM)
        {
            var result = await Task.Run(() =>
            {
                string? tempDir = null;
                try
                {
                    tempDir = Path.Combine(Path.GetTempPath(), $"d2rmm_extract_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);
                    ZipFile.ExtractToDirectory(zipPath, tempDir);

                    var sourceMpqDir = ResolveSourceModFolder(tempDir);

                    if (sourceMpqDir == null || !Directory.Exists(sourceMpqDir))
                        return false;

                    var targetMpqDir = Path.Combine(installDirectory, "Reimagined");
                    if (Directory.Exists(targetMpqDir))
                        Directory.Delete(targetMpqDir, recursive: true);

                    // Also clean up legacy Reimagined.mpq folder if present
                    var legacyTargetDir = Path.Combine(installDirectory, "Reimagined.mpq");
                    if (Directory.Exists(legacyTargetDir))
                        Directory.Delete(legacyTargetDir, recursive: true);

                    CopyDirectory(sourceMpqDir, targetMpqDir);

                    var backupDir = Path.Combine(installDirectory, "Reimagined.backup");
                    if (Directory.Exists(backupDir))
                        Directory.Delete(backupDir, recursive: true);

                    var legacyBackupDir = Path.Combine(installDirectory, "Reimagined.mpq.backup");
                    if (Directory.Exists(legacyBackupDir))
                        Directory.Delete(legacyBackupDir, recursive: true);

                    return true;
                }
                finally
                {
                    if (tempDir != null && Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { /* ignore cleanup */ }
                    }
                }
            });

            if (!result)
            {
                Notifications.SendNotification("Reimagined mod folder not found in the mod archive.", "Warning");
                return;
            }

            Notifications.SendNotification("Mod installed to D2RMM mods folder successfully.", "Success");
        }
        else
        {
            // Snapshot the existing mods/Reimagined/Reimagined.mpq payload
            // before we extract the new zip over it so we can identify files
            // that the new mod version no longer ships and reconcile them
            // via CascOrphanRecoveryService below. The diff root is the
            // .mpq directory (D2R's actual mod data root with -mod
            // Reimagined -txt) so the relative paths produced here align
            // with CASC manifest keys (e.g. "data\global\excel\armor.txt").
            // This replaces the bulk "rename to Reimagined.backup" approach
            // without losing orphan cleanup correctness.
            var modRoot = Path.Combine(installDirectory, "mods", "Reimagined", "Reimagined.mpq");
            HashSet<string> oldModPaths;
            try
            {
                oldModPaths = EnumerateModRelativePaths(modRoot);
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.Log($"Failed to snapshot existing mod payload for orphan recovery: {ex.Message}");
                oldModPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            await ExtractNonD2RmmModAsync(zipPath, installDirectory);

            // Invalidate the per-launch "clean" snapshots so they are retaken
            // from the new mod files on the next launch. Previously this was
            // achieved by renaming mods/Reimagined to mods/Reimagined.backup
            // (which wiped the snapshots as a side effect); now we do it
            // explicitly so we can stop creating that 40+ GB sibling tree.
            try
            {
                ModTweaksService.InvalidateCleanSnapshots(installDirectory);
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.Log($"Failed to invalidate launcher_clean snapshots: {ex.Message}");
            }

            // Phase 1h orphan recovery: any path the *previous* mod payload
            // shipped that the new payload no longer does is reconciled
            // against the CASC fastload manifest. When fastload is not
            // configured (manifest absent) every removed path falls through
            // to a best-effort delete, which is the correct CASC-less
            // behaviour: the game then reads the underlying default from
            // CASC at runtime. When fastload is configured this same call
            // either re-extracts the CASC default (preserving the speedup)
            // or strips ownership tokens for plugin-overlaid paths.
            HashSet<string> newModPathsForFlip = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var newModPaths = EnumerateModRelativePaths(modRoot);
                newModPathsForFlip = newModPaths;
                var removed = oldModPaths
                    .Where(p => !newModPaths.Contains(p))
                    .ToArray();

                if (removed.Length > 0)
                {
                    var manifestService = new CascFastloadManifestService(installDirectory);
                    var extractionService = new CascExtractionService(new NativeCascLib());
                    var orphanService = new CascOrphanRecoveryService(extractionService, manifestService);

                    // storage: null — fastload extraction here would require an
                    // open CASC handle plus user opt-in via the upcoming Phase
                    // 1j UI. Until that lands, the null-storage path is the
                    // correct default and remains safe for CASC-less installs.
                    var result = await orphanService.ReconcileRemovedPathsAsync(
                        removed,
                        storage: null,
                        destinationRoot: modRoot);

                    LaunchDiagnostics.Log(
                        $"Mod orphan recovery: {removed.Length} removed, " +
                        $"{result.Deleted} deleted, {result.Restored} restored, " +
                        $"{result.SourceUpdated} source-updated, {result.NotTracked} not-tracked, " +
                        $"{result.Failed} failed, {result.DirectoriesPruned} dirs pruned.");
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.Log($"Mod orphan recovery failed: {ex.Message}");
            }

            // Manifest token-flip: every path the new mod zip wrote that
            // already has a CASC fastload manifest entry is now an overlay
            // (mod-authored bytes on top of the CASC default). Flip the
            // entry's Source token to include "mod" so a future delta
            // extract won't see the on-disk bytes diverging from the CKey
            // and helpfully restore the CASC default over the mod's
            // edits. CascCKey already records the underlying default for
            // restore-on-removal; we only need to add the ownership flag.
            //
            // We also stamp ModVersion on every mod-overlay entry using the
            // freshly extracted modinfo.json so the launcher can later
            // detect stale overlays (e.g. when a user reinstalls or rolls
            // back the mod) without re-reading every modinfo.json on
            // startup. Previously this field was left null, which is what
            // produced "ModVersion": null entries in the manifest even on
            // freshly installed casc+mod paths.
            try
            {
                if (newModPathsForFlip.Count > 0)
                {
                    var manifestService = new CascFastloadManifestService(installDirectory);
                    var pre = await manifestService.LoadAsync().ConfigureAwait(false);
                    if (pre.Files.Count > 0)
                    {
                        // Resolve the version once, from the just-installed
                        // payload. Null is acceptable here (e.g. modinfo.json
                        // missing the version field) — we simply skip the
                        // ModVersion stamp in that case rather than writing
                        // a placeholder we'd later have to special-case.
                        var installedVersion = CharacterSelectPanelService.GetModVersion(modRoot);

                        var flipped = 0;
                        var versionStamped = 0;
                        await manifestService.UpdateAsync(manifest =>
                        {
                            foreach (var entry in manifest.Files)
                            {
                                if (!newModPathsForFlip.Contains(entry.Path))
                                {
                                    continue;
                                }

                                var newSource = AddModToken(entry.Source);
                                if (!string.Equals(newSource, entry.Source, StringComparison.Ordinal))
                                {
                                    entry.Source = newSource;
                                    flipped++;
                                }

                                if (!string.IsNullOrWhiteSpace(installedVersion) &&
                                    !string.Equals(entry.ModVersion, installedVersion, StringComparison.Ordinal))
                                {
                                    entry.ModVersion = installedVersion;
                                    versionStamped++;
                                }
                            }
                        }).ConfigureAwait(false);

                        LaunchDiagnostics.Log(
                            $"CASC manifest token-flip: {flipped} entries now flagged as casc+mod overlays, " +
                            $"{versionStamped} ModVersion stamps written (version: {installedVersion ?? "<unknown>"}).");
                    }
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.Log($"CASC manifest token-flip failed: {ex.Message}");
            }

            // Migration courtesy: remove any leftover mods/Reimagined.backup
            // tree from previous launcher versions so users don't keep
            // double-disk usage forever.
            try
            {
                var legacyBackupDir = Path.Combine(installDirectory, "mods", "Reimagined.backup");
                if (Directory.Exists(legacyBackupDir))
                {
                    Directory.Delete(legacyBackupDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.Log($"Failed to remove legacy mods/Reimagined.backup: {ex.Message}");
            }

            Notifications.SendNotification("Mod installed successfully.", "Success");
        }

        var mainWindow = MainWindow.Instance;
        if (mainWindow != null)
        {
            mainWindow.RefreshLocalModState();
            await mainWindow.RefreshUpdateStateAsync();
            await mainWindow.NavigateToLaunchViewAsync();
        }
    }

    private static string? ResolveSourceModFolder(string tempDir)
    {
        string[] searchPaths =
        [
            Path.Combine(tempDir, "mods", "Reimagined", "Reimagined"),
            Path.Combine(tempDir, "mods", "Reimagined", "Reimagined.mpq"),
            Path.Combine(tempDir, "Reimagined", "Reimagined"),
            Path.Combine(tempDir, "Reimagined", "Reimagined.mpq")
        ];

        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path) &&
                Directory.Exists(Path.Combine(path, "data")) &&
                File.Exists(Path.Combine(path, "modinfo.json")))
            {
                return path;
            }
        }

        // Fallback: search recursively for either folder name containing data/modinfo.json
        foreach (var name in new[] { "Reimagined", "Reimagined.mpq" })
        {
            var found = Directory.GetDirectories(tempDir, name, SearchOption.AllDirectories);
            foreach (var dir in found)
            {
                if (Directory.Exists(Path.Combine(dir, "data")) &&
                    File.Exists(Path.Combine(dir, "modinfo.json")))
                {
                    return dir;
                }
            }
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Extracts the non-D2RMM mod archive over the install directory using
    /// per-file atomic replacement. This replaces the previous behaviour of
    /// renaming <c>mods/Reimagined</c> to <c>mods/Reimagined.backup</c> and
    /// re-extracting; that workaround existed because the on-disk
    /// "*_launcher_clean" snapshots were stale, not because the overwrite
    /// itself was unreliable. The clean snapshots are now invalidated
    /// explicitly by <see cref="ModTweaksService.InvalidateCleanSnapshots"/>.
    /// </summary>
    private static Task ExtractNonD2RmmModAsync(string zipPath, string installDirectory)
    {
        return FileCopyHelper.ExtractZipAsync(zipPath, installDirectory);
    }

    /// <summary>
    /// Enumerates every file beneath <paramref name="modRoot"/> and returns
    /// the set of relative paths in CASC-style backslash form (matching the
    /// fastload manifest's path convention). Returns an empty set when the
    /// directory does not exist.
    /// </summary>
    private static HashSet<string> EnumerateModRelativePaths(string modRoot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
        {
            return set;
        }

        foreach (var fullPath in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(modRoot, fullPath);
            // Manifest paths use backslashes regardless of platform; normalise so
            // diff/lookup against CascFastloadManifest entries is stable.
            if (Path.DirectorySeparatorChar != '\\')
            {
                relative = relative.Replace(Path.DirectorySeparatorChar, '\\');
            }
            set.Add(relative);
        }

        return set;
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the <c>mod</c> token added if
    /// not already present. Recognises the canonical
    /// <see cref="CascFastloadEntry.SourceTokens"/> combinations and falls
    /// back to a "+"-suffixed concat for any unexpected starting value.
    /// </summary>
    private static string AddModToken(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return CascFastloadEntry.SourceTokens.Mod;
        }

        // Tokenise on '+' so we don't depend on insertion order; rebuild in
        // the canonical casc/mod/plugin order so output matches SourceTokens
        // constants exactly.
        var parts = source.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasCasc = parts.Any(p => p.Equals(CascFastloadEntry.SourceTokens.Casc, StringComparison.OrdinalIgnoreCase));
        var hasPlugin = parts.Any(p => p.Equals(CascFastloadEntry.SourceTokens.Plugin, StringComparison.OrdinalIgnoreCase));
        // Mod is what we are adding; ignore whether it was already there.

        var rebuilt = new List<string>(3);
        if (hasCasc) rebuilt.Add(CascFastloadEntry.SourceTokens.Casc);
        rebuilt.Add(CascFastloadEntry.SourceTokens.Mod);
        if (hasPlugin) rebuilt.Add(CascFastloadEntry.SourceTokens.Plugin);
        return string.Join('+', rebuilt);
    }

    /// <summary>
    /// Refreshes the currently visible UpdateView if one is active in the content area.
    /// This allows the install operation to update the UI even when the original view
    /// instance that started the install has been replaced by tab navigation.
    /// </summary>
    private static void RefreshVisibleUpdateView()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (MainWindow.Instance?.ContentArea.Content is UpdateView visibleView)
            {
                visibleView.RefreshUpdateState();
            }
        });
    }

    private static async Task<string?> WaitForNewZipFromDownloadsAsync(TimeSpan timeout)
    {
        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        if (!Directory.Exists(downloadsFolder))
            return null;

        var baseline = SnapshotZipFiles(downloadsFolder);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new FileSystemWatcher(downloadsFolder, "*.zip");
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
        watcher.IncludeSubdirectories = false;
        watcher.EnableRaisingEvents = true;

        void OnChanged(object _, FileSystemEventArgs e) => TryResolve(e.FullPath, baseline, tcs);
        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;

        using var cts = new System.Threading.CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(null));

        return await tcs.Task;
    }

    private static void TryResolve(string path, HashSet<string> baseline, TaskCompletionSource<string?> tcs)
    {
        if (tcs.Task.IsCompleted)
            return;

        var signature = GetFileSignature(path);
        if (signature == null || baseline.Contains(signature))
            return;

        if (!IsFileReady(path) || !IsZipArchive(path))
            return;

        tcs.TrySetResult(path);
    }

    private static HashSet<string> SnapshotZipFiles(string folder)
    {
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var signature = GetFileSignature(path);
            if (!string.IsNullOrWhiteSpace(signature))
            {
                snapshot.Add(signature);
            }
        }

        return snapshot;
    }

    private static string? GetFileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFileReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
