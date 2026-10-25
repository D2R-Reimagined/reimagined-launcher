using ReimaginedLauncher.HttpClients.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LocalRigMirrorResult(int Copied, int Unchanged, int Removed);

/// <summary>
/// The staff local d2rds rig: a dedicated server and a ladder client on this PC,
/// from the same files, against the launcher's API.
/// </summary>
/// <remarks>
/// The player's own mods\Reimagined - plugins built from source, d2rl-d2rds.dll
/// included - is mirrored into mods\ReimaginedLadder, so both processes run the
/// ladder's mod identity and hash to the same environment (D2RLoader refuses a
/// join otherwise). The server is that folder started with -d2rds, which the
/// d2rds plugin turns into a headless server and every other plugin treats as a
/// server (no player session). The client is the same folder as a ladder
/// launch: lobby-bridge lists and hosts through the API, Host lands on the
/// local server, and server-saves plus the server's save authority carry the
/// characters, as on a real ladder. The API side (staff-test/session) gives the
/// staff member a hidden ladder of their own whose only server is this one.
///
/// The mirror replaces a signed ladder package in mods\ReimaginedLadder; Ladder
/// mode then offers Update, which restores it from the launcher's cache.
/// </remarks>
public static class LocalD2rdsRigService
{
    public const int ServerApiPort = 8080;
    public const int ServerGamePort = 4000;
    private static readonly string[] StaffRoles = ["Admin", "Moderator"];

    // Never mirrored, never removed: the launcher writes configs (some hold
    // tokens), logs are the game's, ladder-disabled is the extension policy's.
    private static readonly string[] KeptPrefixes = ["d2rloader/config/", "d2rloader/logs/", "d2rloader/ladder-disabled/"];
    private static readonly string[] SkippedExtensions = [".pdb", ".ilk", ".exp", ".lib", ".bak", ".tmp"];
    private static readonly string[] RequiredPlugins = ["d2rl-d2rds.dll", "d2rl-server-saves.dll", "d2rl-lobby-bridge.dll"];

    public static bool IsStaff(ReimaginedUserResponse? user)
        => user?.Roles?.Any(role => StaffRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) == true;

    public static string SourceRoot(string installDirectory) => Path.Combine(installDirectory, "mods", ModInstallationPaths.NormalModName);

    /// <summary>Why the rig cannot start from this install, or null.</summary>
    public static string? Problem(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !File.Exists(Path.Combine(installDirectory, "D2RLoader.exe")))
            return "Select a D2R installation with D2RLoader installed.";
        var plugins = Path.Combine(SourceRoot(installDirectory), "d2rloader", "plugins");
        if (!Directory.Exists(plugins)) return "mods\\Reimagined is not installed; the rig runs your local Reimagined.";
        var missing = RequiredPlugins.Where(name => !File.Exists(Path.Combine(plugins, name))).ToArray();
        return missing.Length == 0
            ? null
            : $"mods\\Reimagined\\d2rloader\\plugins is missing {string.Join(", ", missing)}"
              + (missing.Contains("d2rl-d2rds.dll") ? " (scripts\\setup-local-d2rds.ps1 in d2rl-plugins installs it)." : ".");
    }

    /// <summary>
    /// Makes mods\ReimaginedLadder an exact copy of mods\Reimagined, but for the
    /// kept folders. Files already the same size and time are left alone.
    /// </summary>
    public static LocalRigMirrorResult Mirror(string installDirectory, CancellationToken cancellationToken = default)
    {
        var source = SourceRoot(installDirectory);
        var target = ModInstallationPaths.LadderModRoot(installDirectory);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, unchanged = 0, removed = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (IsKept(relative) || SkippedExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) continue;
            var ladderRelative = LadderRelative(relative);
            wanted.Add(ladderRelative);
            var destination = Path.Combine(target, ladderRelative.Replace('/', Path.DirectorySeparatorChar));
            var from = new FileInfo(file);
            var to = new FileInfo(destination);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
            {
                unchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, from.LastWriteTimeUtc);
            copied++;
        }

        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToArray())
            {
                var relative = Path.GetRelativePath(target, file).Replace('\\', '/');
                if (IsKept(relative) || wanted.Contains(relative)) continue;
                File.Delete(file);
                removed++;
            }
        }

        return new LocalRigMirrorResult(copied, unchanged, removed);
    }

    internal static bool IsKept(string relativePath)
        => KeptPrefixes.Any(prefix => relativePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    // mods\Reimagined's Reimagined.mpq is ReimaginedLadder.mpq in the ladder folder.
    internal static string LadderRelative(string relativePath)
        => ModInstallationPaths.ToLadderPath("mods/Reimagined/" + relativePath)["mods/ReimaginedLadder/".Length..];

    /// <summary>
    /// d2rds.toml in the ladder folder: dormant unless the process has -d2rds, then
    /// a server for this API with server-authoritative saves and no games of its
    /// own (Host makes them).
    /// </summary>
    public static void WriteServerConfig(string installDirectory, string apiBaseUrl)
    {
        if (apiBaseUrl.IndexOfAny(['"', '\\', '\r', '\n']) >= 0) throw new ArgumentException("The API address cannot be written to d2rds.toml.");
        var path = Path.Combine(ModInstallationPaths.LadderModRoot(installDirectory), "d2rloader", "config", "d2rds.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config = $"""
            # Written by the Reimagined launcher for a staff local d2rds test.
            # Only a launch with -d2rds becomes a server; every other launch of this
            # folder leaves the plugin dormant.

            enabled = false
            dry_run = false
            verbose = true
            sentry = false
            authoritative_saves = true
            standing_games = false

            [server]
            game_port = {ServerGamePort}
            api_port = {ServerApiPort}
            api_bind_address = "127.0.0.1"

            [game]
            game_version = 3
            max_players = 8
            level_diff = 255
            startup_game_name = ""
            startup_game_pass = ""

            [lobby]
            report_telemetry = false
            lobby_api_base_url = "{apiBaseUrl}"
            # The key is in reimagined-secrets\server.toml, outside the mod folder.
            lobby_server_key = ""

            [stash]
            server_tabs = 4

            """;
        File.WriteAllText(path, config.ReplaceLineEndings(Environment.NewLine), new UTF8Encoding(false));
    }

    /// <summary>
    /// reimagined-secrets\server.toml: this session's server key, keeping any
    /// other line (a control API token) as it was.
    /// </summary>
    public static void WriteServerSecret(string installDirectory, string serverKey)
    {
        if (serverKey.Length == 0 || !serverKey.All(Uri.IsHexDigit)) throw new ArgumentException("The API issued an unexpected server key.");
        var path = Path.Combine(installDirectory, "reimagined-secrets", "server.toml");
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("lobby_server_key", StringComparison.Ordinal)).ToList()
            : ["# This server's own secrets, read by d2rds. Outside mods\\ on purpose: D2RLoader hands a host's mod folder to joining players."];
        lines.Add($"lobby_server_key = \"{serverKey}\"");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    /// <summary>True when a d2rds control API answers on this PC.</summary>
    public static async Task<bool> IsServerUpAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync($"http://127.0.0.1:{ServerApiPort}/api/status", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The game processes running now, by id.</summary>
    public static HashSet<int> GameProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "D2R", "D2RLoader" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                ids.Add(process.Id);
                process.Dispose();
            }
        }

        return ids;
    }

    public static ProcessStartInfo ServerStartInfo(string installDirectory) => new(Path.Combine(installDirectory, "D2RLoader.exe"))
    {
        Arguments = $"-mod {ModInstallationPaths.LadderModName} -txt -d2rds",
        WorkingDirectory = installDirectory,
        UseShellExecute = false
    };

    public static ProcessStartInfo ClientStartInfo(string installDirectory, InstallationProfile profile) => new(Path.Combine(installDirectory, "D2RLoader.exe"))
    {
        Arguments = GameLauncherService.BuildLaunchParameters(new InstallationProfile
        {
            LaunchExperience = LaunchExperience.Ladder,
            NoRumble = profile.NoRumble,
            ForceDesktop = profile.ForceDesktop,
            WindowedMode = profile.WindowedMode,
            NoSound = profile.NoSound
        }),
        WorkingDirectory = installDirectory,
        UseShellExecute = false
    };

    /// <summary>Ends the given processes and their children; ones already gone are fine.</summary>
    public static void Stop(IEnumerable<int> processIds)
    {
        foreach (var id in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                                  or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
