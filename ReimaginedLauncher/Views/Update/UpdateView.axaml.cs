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
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Update;

public partial class UpdateView : UserControl
{
    private bool _isInstalling;
    private bool _isLoading;

    public UpdateView()
    {
        InitializeComponent();
        RefreshUpdateState();
    }

    public void RefreshUpdateState()
    {
        var isAuthenticated = MainWindow.UserViewModel.User != null;
        var usesDownloadsWatcher = isAuthenticated && MainWindow.Settings.NexusPremiumDownloadAccess == false;
        var isInstallMissing = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase);
        var canDownload = isInstallMissing || MainWindow.IsUpdateAvailable;

        LoadingBanner.IsVisible = _isLoading;
        InstallProgressBanner.IsVisible = _isInstalling;
        StatusBorder.IsVisible = !_isLoading && !_isInstalling;
        VersionsBorder.IsVisible = !_isLoading && !_isInstalling;
        AuthWarningBanner.IsVisible = !_isLoading && !_isInstalling && !isAuthenticated;
        NonPremiumWarningBanner.IsVisible = !_isLoading && !_isInstalling && usesDownloadsWatcher && canDownload;

        StatusTitleText.Text = MainWindow.UpdateStatusTitle;
        StatusMessageText.Text = MainWindow.UpdateStatusMessage;
        CurrentVersionText.Text = MainWindow.UpdateCurrentVersion;
        LatestVersionText.Text = MainWindow.UpdateLatestVersion;
        InstallOrUpdateButton.IsEnabled = !_isLoading &&
                                          MainWindow.CanInstallOrUpdate &&
                                          !_isInstalling &&
                                          isAuthenticated &&
                                          canDownload;
        SelectZipManuallyButton.IsEnabled = !_isLoading &&
                                            !_isInstalling &&
                                            MainWindow.Settings.CurrentProfile.IsInstallDirectoryValidated &&
                                            !string.IsNullOrWhiteSpace(MainWindow.Settings.InstallDirectory);
        OpenDownloadPageButton.IsEnabled = !_isLoading && !string.IsNullOrWhiteSpace(MainWindow.UpdateDownloadUrl);
        RecheckButton.IsEnabled = !_isLoading;
        InstallOrUpdateButton.Content = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase)
            ? "Download and Install"
            : "Download and Update";
    }

    public void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        RefreshUpdateState();
    }

    private async void OnInstallOrUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_isInstalling || string.IsNullOrWhiteSpace(MainWindow.UpdateDownloadUrl))
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
            _isInstalling = true;
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
            _isInstalling = false;
            RefreshUpdateState();
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
        if (_isInstalling)
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
            _isInstalling = true;
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
            _isInstalling = false;
            RefreshUpdateState();
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
        InstallProgressTitle.Text = title;
        InstallProgressMessage.Text = message;
        InstallProgressBanner.IsVisible = true;
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

                    var sourceMpqDir = Path.Combine(tempDir, "mods", "Reimagined", "Reimagined.mpq");
                    if (!Directory.Exists(sourceMpqDir))
                        sourceMpqDir = Path.Combine(tempDir, "Reimagined", "Reimagined.mpq");
                    if (!Directory.Exists(sourceMpqDir))
                    {
                        var found = Directory.GetDirectories(tempDir, "Reimagined.mpq", SearchOption.AllDirectories);
                        if (found.Length > 0)
                            sourceMpqDir = found[0];
                    }

                    if (!Directory.Exists(sourceMpqDir))
                        return false;

                    var targetMpqDir = Path.Combine(installDirectory, "Reimagined.mpq");
                    if (Directory.Exists(targetMpqDir))
                    {
                        var backupDir = Path.Combine(installDirectory, "Reimagined.mpq.backup");
                        if (Directory.Exists(backupDir))
                            Directory.Delete(backupDir, recursive: true);
                        Directory.Move(targetMpqDir, backupDir);
                    }

                    CopyDirectory(sourceMpqDir, targetMpqDir);
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
                Notifications.SendNotification("Reimagined.mpq not found in the mod archive.", "Warning");
                return;
            }

            Notifications.SendNotification("Mod installed to D2RMM mods folder successfully.", "Success");
        }
        else
        {
            await Task.Run(() =>
            {
                var modDir = Path.Combine(installDirectory, "mods", "Reimagined");
                if (Directory.Exists(modDir))
                {
                    var backupDir = Path.Combine(installDirectory, "mods", "Reimagined.backup");
                    if (Directory.Exists(backupDir))
                    {
                        Directory.Delete(backupDir, recursive: true);
                    }
                    Directory.Move(modDir, backupDir);
                }

                ZipFile.ExtractToDirectory(zipPath, installDirectory, overwriteFiles: true);
            });

            Notifications.SendNotification("Mod installed successfully.", "Success");
        }

        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            mainWindow.RefreshLocalModState();
            await mainWindow.RefreshUpdateStateAsync();
            await mainWindow.NavigateToLaunchViewAsync();
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
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
