using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

/// <summary>
/// The staff local d2rds rig (see <see cref="LocalD2rdsRigService"/>): one click
/// starts a dedicated server and a ladder client on this PC. Admin and Moderator
/// accounts only, in Mod + D2RLoader mode, on Windows.
/// </summary>
public partial class LaunchView
{
    private static readonly TimeSpan LocalRigServerStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LocalRigClientStartTimeout = TimeSpan.FromMinutes(1);

    private bool _localRigBusy;          // preparing or running
    private HashSet<int> _localRigServer = [];
    private HashSet<int> _localRigClient = [];
    private string? _localRigStatus;

    private void RefreshLocalRigPanel(InstallationProfile profile)
    {
        LocalRigPanel.IsVisible = OperatingSystem.IsWindows()
                                  && profile.Type != InstallationType.D2RMM
                                  && profile.LaunchExperience == LaunchExperience.Online
                                  && LocalD2rdsRigService.IsStaff(_launcherAuthenticationService.CurrentUser);
        if (!LocalRigPanel.IsVisible) return;

        var problem = _localRigBusy ? null : LocalD2rdsRigService.Problem(profile.InstallDirectory);
        LocalRigStatusText.Text = _localRigStatus
                                  ?? problem
                                  ?? "Runs a d2rds and a ladder client from your mods\\Reimagined against "
                                  + _apiHttpClient.BaseAddress.GetLeftPart(UriPartial.Authority) + ".";
        LocalRigStartButton.Content = _localRigBusy && _localRigServer.Count > 0 ? "Stop local test" : "Start local server + game";
        LocalRigStartButton.IsEnabled = _localRigBusy
            ? _localRigServer.Count > 0
            : problem is null && !_isLaunching && !MainWindow.IsInstallInProgress && !MainWindow.IsGameRunning();
    }

    private void SetLocalRigStatus(string? status)
    {
        _localRigStatus = status;
        if (status is not null) LaunchDiagnostics.Log($"local d2rds rig: {status}");
        RefreshLocalRigPanel(MainWindow.Settings.CurrentProfile);
    }

    private async void OnLocalRigStartClick(object? sender, RoutedEventArgs e)
    {
        if (_localRigBusy)
        {
            if (_localRigServer.Count == 0) return;
            SetLocalRigStatus("Stopping the local test...");
            LocalD2rdsRigService.Stop(_localRigClient.Concat(_localRigServer));
            return;
        }

        await StartLocalRigAsync();
    }

    private async Task StartLocalRigAsync()
    {
        var profile = MainWindow.Settings.CurrentProfile;
        var install = profile.InstallDirectory;
        if (_isLaunching || MainWindow.IsInstallInProgress || MainWindow.IsGameRunning())
        {
            Notifications.SendNotification("Close the game (and any server) before starting a local d2rds test.", "Warning");
            return;
        }
        if (LocalD2rdsRigService.Problem(install) is { } problem)
        {
            Notifications.SendNotification(problem, "Warning");
            return;
        }

        _localRigBusy = true;
        _isLaunching = true;
        _localRigServer = [];
        _localRigClient = [];
        GameSessionToken? gameToken = null;
        var handedOff = false;
        using var http = new HttpClient();
        try
        {
            SetLocalRigStatus("Checking for a server already running...");
            if (await LocalD2rdsRigService.IsServerUpAsync(http))
                throw new InvalidOperationException(
                    $"A d2rds already answers on 127.0.0.1:{LocalD2rdsRigService.ServerApiPort}. Close it first.");

            var accessToken = await _launcherAuthenticationService.GetAccessTokenAsync()
                              ?? throw new InvalidOperationException("Sign in to your Reimagined account first.");
            SetLocalRigStatus("Starting a staff test session...");
            var session = await _apiHttpClient.StartStaffTestSessionAsync(LadderBundleService.LauncherVersion, accessToken);
            var apiBase = _apiHttpClient.BaseAddress.GetLeftPart(UriPartial.Authority);

            // No player session while the server starts: the server shares this
            // install, and its plugins must not take the player's.
            GameSessionSecretsService.Delete(install);
            await GameSessionSecretsService.ScrubPluginConfigsAsync(install);

            SetLocalRigStatus("Mirroring mods\\Reimagined into mods\\ReimaginedLadder...");
            var mirror = await Task.Run(() => LocalD2rdsRigService.Mirror(install!));
            LaunchDiagnostics.Log($"local d2rds rig: mirror copied {mirror.Copied}, kept {mirror.Unchanged}, removed {mirror.Removed} file(s).");
            LocalD2rdsRigService.WriteServerConfig(install!, apiBase);
            LocalD2rdsRigService.WriteServerSecret(install!, session.ServerKey);
            D2RLoaderService.EnableExtensionDetailLogs(install!);
            D2RLoaderService.SetDefaultMod(install!, LaunchExperience.Ladder);

            var saves = await LadderSaveDirectoryService.PrepareAsync(install, session.LadderId, session.LadderName);
            if (saves.DirectoryPath is null)
                throw new InvalidOperationException(saves.ErrorMessage ?? "The ladder save folder could not be prepared.");

            SetLocalRigStatus("Starting the local d2rds (its console opens)...");
            var beforeServer = LocalD2rdsRigService.GameProcessIds();
            using (Process.Start(LocalD2rdsRigService.ServerStartInfo(install!))) { }
            var serverDeadline = DateTime.UtcNow + LocalRigServerStartTimeout;
            while (!await LocalD2rdsRigService.IsServerUpAsync(http))
            {
                if (DateTime.UtcNow > serverDeadline)
                    throw new TimeoutException("The local d2rds did not come up within 3 minutes; see mods\\ReimaginedLadder\\d2rloader\\logs\\d2rds.log.");
                await Task.Delay(1000);
            }
            _localRigServer = LocalD2rdsRigService.GameProcessIds();
            _localRigServer.ExceptWith(beforeServer);

            SetLocalRigStatus("Server up; starting the game...");
            gameToken = await _launcherAuthenticationService.CreateGameTokenAsync()
                        ?? throw new InvalidOperationException("The API did not issue a game session token.");
            var statusSessionId = Guid.NewGuid().ToString("N");
            if (!await ServerSavesConfigService.EnableAsync(install, new ServerSavesLaunchSettings(
                    apiBase, gameToken.AccessToken, session.LadderId, session.LaunchTicket, statusSessionId)))
                throw new InvalidOperationException("The server-saves configuration could not be written.");
            if (!await GameSessionSecretsService.WriteAsync(install, new GameSessionSecrets(
                    gameToken.AccessToken, gameToken.ExpiresAtUtc, session.LaunchTicket, statusSessionId)))
                throw new InvalidOperationException("The session file could not be written.");

            var beforeClient = LocalD2rdsRigService.GameProcessIds();
            using (Process.Start(LocalD2rdsRigService.ClientStartInfo(install!, profile))) { }
            handedOff = true;
            SetLocalRigStatus($"Running on {session.LadderName}: Multiplayer > Host makes the game on your local server. Closing the game stops the server.");
            _ = WatchLocalRigAsync(install!, beforeClient, gameToken);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("local d2rds rig failed", exception);
            Notifications.SendNotification(exception.Message, "Local d2rds test");
            LocalD2rdsRigService.Stop(_localRigServer);
            SetLocalRigStatus("Failed: " + exception.Message);
        }
        finally
        {
            if (!handedOff)
            {
                GameSessionSecretsService.Delete(install);
                if (gameToken is not null) _ = _launcherAuthenticationService.RevokeGameTokenAsync(gameToken);
                _localRigBusy = false;
                _localRigServer = [];
            }
            _isLaunching = false;
            RefreshInstallDirectoryState();
        }
    }

    /// <summary>
    /// Until the client closes - its game processes are the ones that appeared
    /// after it was started - then the server goes too, and the session with it.
    /// </summary>
    private async Task WatchLocalRigAsync(string install, HashSet<int> beforeClient, GameSessionToken gameToken)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(2000);
                var running = LocalD2rdsRigService.GameProcessIds();
                var client = running.Where(id => !beforeClient.Contains(id) && !_localRigServer.Contains(id)).ToHashSet();
                _localRigClient.UnionWith(client);
                if (client.Count > 0) continue;
                if (_localRigClient.Count == 0 && DateTime.UtcNow - started < LocalRigClientStartTimeout) continue;
                break;
            }
        }
        finally
        {
            LocalD2rdsRigService.Stop(_localRigServer);
            GameSessionSecretsService.Delete(install);
            _ = _launcherAuthenticationService.RevokeGameTokenAsync(gameToken);
            _localRigBusy = false;
            _localRigServer = [];
            _localRigClient = [];
            SetLocalRigStatus("Local test ended; the server was stopped.");
            RefreshInstallDirectoryState();
        }
    }
}
