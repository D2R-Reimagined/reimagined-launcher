using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Update;

public partial class UpdateView : UserControl
{
    private bool _isInstalling;

    public UpdateView()
    {
        InitializeComponent();
        RefreshUpdateState();
    }

    public void RefreshUpdateState()
    {
        var isAuthenticated = MainWindow.UserViewModel.User != null;
        var isInstallMissing = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase);
        var canDownload = isInstallMissing || MainWindow.IsUpdateAvailable;
        StatusTitleText.Text = MainWindow.UpdateStatusTitle;
        StatusMessageText.Text = MainWindow.UpdateStatusMessage;
        CurrentVersionText.Text = MainWindow.UpdateCurrentVersion;
        LatestVersionText.Text = MainWindow.UpdateLatestVersion;
        InstallOrUpdateButton.IsEnabled = MainWindow.CanInstallOrUpdate && !_isInstalling && isAuthenticated && canDownload;
        InstallOrUpdateButton.Content = MainWindow.UpdateCurrentVersion.Equals("Not detected", StringComparison.OrdinalIgnoreCase)
            ? "Download and Install"
            : "Download and Update";
        AuthWarningBanner.IsVisible = !isAuthenticated;
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
            Notifications.SendNotification("Downloading mod archive. This may take a moment.");

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"reimagined-{Guid.NewGuid():N}.zip");

            try
            {
                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(
                    MainWindow.UpdateDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
                }

                if (!IsZipArchive(tempZipPath))
                {
                    Notifications.SendNotification(
                        "Automatic install needs a direct download link. Opening the download page instead.",
                        "Warning");
                    OnOpenDownloadPageClick(null, null);
                    return;
                }

                ZipFile.ExtractToDirectory(tempZipPath, installDirectory, overwriteFiles: true);
            }
            finally
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }

            Notifications.SendNotification("Mod installed successfully.", "Success");

            if (this.GetVisualRoot() is MainWindow mainWindow)
            {
                mainWindow.RefreshLocalModState();
                await mainWindow.RefreshUpdateStateAsync();
                await mainWindow.NavigateToLaunchViewAsync();
            }
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

    private async void OnRecheckClick(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is MainWindow mainWindow)
        {
            await mainWindow.RefreshUpdateStateAsync();
            RefreshUpdateState();
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
}
