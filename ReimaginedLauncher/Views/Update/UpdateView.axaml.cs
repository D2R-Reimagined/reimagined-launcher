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
using Avalonia.VisualTree;
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
        StatusBorder.IsVisible = !_isLoading;
        VersionsBorder.IsVisible = !_isLoading;
        AuthWarningBanner.IsVisible = !_isLoading && !isAuthenticated;
        NonPremiumWarningBanner.IsVisible = !_isLoading && usesDownloadsWatcher && canDownload;

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
                                            MainWindow.Settings.IsInstallDirectoryValidated &&
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
        
        var installDirectory = MainWindow.Settings.InstallDirectory;
        if (!MainWindow.Settings.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(installDirectory))
        {
            Notifications.SendNotification(
                "Install directory not validated",
                "Select the Diablo II: Resurrected folder before installing the mod.");
            return;
        }

        try
        {
            _isInstalling = true;
            RefreshUpdateState();

            if (MainWindow.Settings.NexusPremiumDownloadAccess == false)
            {
                Notifications.SendNotification("Open Manual Download in browser. Waiting for zip in Downloads...", "Info");
                OnOpenDownloadPageClick(null, null);
                var downloadedZip = await WaitForNewZipFromDownloadsAsync(TimeSpan.FromMinutes(8));
                if (string.IsNullOrWhiteSpace(downloadedZip))
                {
                    Notifications.SendNotification("No new zip detected in Downloads. Try again after download completes.", "Warning");
                    return;
                }

                await ExtractAndFinalizeInstallAsync(downloadedZip, installDirectory);
                return;
            }

            Notifications.SendNotification("Downloading mod archive. This may take a moment.");
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

    private void OnOpenDownloadPageClick(object? sender, RoutedEventArgs e)
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

        var installDirectory = MainWindow.Settings.InstallDirectory;
        if (!MainWindow.Settings.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(installDirectory))
        {
            Notifications.SendNotification(
                "Install directory not validated",
                "Select the Diablo II: Resurrected folder before installing the mod.");
            return;
        }

        if (this.GetVisualRoot() is not Window window)
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
        if (this.GetVisualRoot() is MainWindow mainWindow)
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

    private async Task ExtractAndFinalizeInstallAsync(string zipPath, string installDirectory)
    {
        if (!IsZipArchive(zipPath))
        {
            Notifications.SendNotification("Downloaded file is not a valid zip archive.", "Warning");
            return;
        }

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
        Notifications.SendNotification("Mod installed successfully.", "Success");

        if (this.GetVisualRoot() is MainWindow mainWindow)
        {
            mainWindow.RefreshLocalModState();
            await mainWindow.RefreshUpdateStateAsync();
            await mainWindow.NavigateToLaunchViewAsync();
        }
    }

    private static async Task<string?> WaitForNewZipFromDownloadsAsync(TimeSpan timeout)
    {
        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        if (!Directory.Exists(downloadsFolder))
            return null;

        var start = DateTime.UtcNow;
        var baseline = SnapshotZipFiles(downloadsFolder);

        while (DateTime.UtcNow - start < timeout)
        {
            var candidates = Directory.GetFiles(downloadsFolder, "*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => Path.GetFileName(path).Contains("reimagined", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToArray();

            foreach (var path in candidates)
            {
                var signature = GetFileSignature(path);
                if (signature == null || baseline.Contains(signature))
                    continue;

                if (!IsFileReady(path))
                    continue;

                if (IsZipArchive(path))
                    return path;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
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
