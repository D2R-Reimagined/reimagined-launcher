using ReimaginedLauncher.HttpClients.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LocalRigMirrorResult(int Copied, int Unchanged, int Removed);

/// <summary>Which loader runs the local rig's game client (the server is always D2RLoader).</summary>
public enum LocalRigLoader
{
    /// <summary>D2RLoader.exe and its game browser (the production lobby).</summary>
    D2RLoader,

    /// <summary>The Reimagined Loader's ReimaginedHost.exe with lobby-bridge in Battle.net lobby mode.</summary>
    ReimaginedLoader
}

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

    // Plugins that talk to the API but have no launcher config step: pointed at
    // this API for a rig session, then put back (see PointAtApiAsync).
    private static readonly string[] UnmanagedApiPlugins = ["supporter-mtx", "reimagined-online-count"];
    private static readonly string[] RequiredPlugins = ["d2rl-d2rds.dll", "d2rl-server-saves.dll", "d2rl-lobby-bridge.dll"];

    public static bool IsStaff(ReimaginedUserResponse? user)
        => user?.Roles?.Any(role => StaffRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) == true;

    public static string SourceRoot(string installDirectory) => Path.Combine(installDirectory, "mods", ModInstallationPaths.NormalModName);

    /// <summary>
    /// The Reimagined Loader's four shipping files (reimagined-loader scriptsuild-dist.ps1
    /// stages them in out\dist), copied into the D2R folder. Its core is D2RRCore.dll, so
    /// it sits beside D2RLoader.exe and D2RLoader's own D2RCore.dll.
    /// </summary>
    public static readonly string[] ReimaginedLoaderFiles =
        ["ReimaginedHost.exe", "D2RRCore.dll", "ReimaginedHostRunner.dll", "ReimaginedRuntime.dll"];

    // The packed host carries the engine (about 18 MB); the unpacked dev stub is a few KB
    // and will not start without --unpatched-engine or --package-host.
    internal const long PackedHostMinimumBytes = 1024 * 1024;

    /// <summary>Why the rig cannot start with this loader, or null.</summary>
    public static string? Problem(string? installDirectory, LocalRigLoader loader)
    {
        // The Reimagined Loader rig runs server and client on ReimaginedHost.exe:
        // D2RLoader.exe need not be installed at all.
        if (loader == LocalRigLoader.ReimaginedLoader)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !File.Exists(Path.Combine(installDirectory, "D2R.exe")))
                return "Select a D2R installation.";
            return ReimaginedLoaderProblem(installDirectory) ?? PluginProblem(installDirectory);
        }
        return Problem(installDirectory);
    }

    /// <summary>Why the D2R folder cannot run the Reimagined Loader client, or null when its 4 files are there.</summary>
    public static string? ReimaginedLoaderProblem(string installDirectory)
    {
        var missing = ReimaginedLoaderFiles.Where(name => !File.Exists(Path.Combine(installDirectory, name))).ToArray();
        if (missing.Length > 0)
            return $"The D2R folder is missing {string.Join(", ", missing)}. Copy the Reimagined Loader's 4 files from "
                   + @"reimagined-loader out\dist (scripts\build-dist.ps1) into it.";
        if (new FileInfo(Path.Combine(installDirectory, "ReimaginedHost.exe")).Length < PackedHostMinimumBytes)
            return @"The D2R folder's ReimaginedHost.exe is the unpacked dev host; copy the packed one from reimagined-loader out\dist.";
        return null;
    }

    /// <summary>Why the D2RLoader rig cannot start from this install, or null.</summary>
    public static string? Problem(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !File.Exists(Path.Combine(installDirectory, "D2RLoader.exe")))
            return "Select a D2R installation with D2RLoader installed.";
        return PluginProblem(installDirectory);
    }

    /// <summary>Why mods\Reimagined lacks the rig's plugins, or null.</summary>
    private static string? PluginProblem(string installDirectory)
    {
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
    /// <remarks>
    /// The ladder's packed .mpq carries data/global/.DISABLE_DEBUG, D2RLoader's
    /// switch for its debug tools and cheats. The rig wants both, so it removes
    /// that marker like any other file the local mod lacks. (2026-10-25: with
    /// debug on, Teeth once looped its cast animation for ~30 s with nothing cast.)
    /// </remarks>
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

        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = Path.GetRelativePath(target, file).Replace('\\', '/');
            if (IsKept(relative) || wanted.Contains(relative)) continue;
            File.Delete(file);
            removed++;
        }

        return new LocalRigMirrorResult(copied, unchanged, removed);
    }

    /// <summary>
    /// Every excel table of the local mod, zipped flat. The API keeps the ones its
    /// save parser reads and checks this ladder's saves against them; its own
    /// built-in copy trails the mod (2026-10-25: "Stat ID 449 out of range"
    /// refused every save the server wrote).
    /// </summary>
    public static byte[] PackTables(string installDirectory)
    {
        var excel = Path.Combine(SourceRoot(installDirectory), ModInstallationPaths.NormalModName + ".mpq", "data", "global", "excel");
        if (!Directory.Exists(excel)) throw new DirectoryNotFoundException($"{excel} is missing; the local mod has no excel tables.");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(excel, "*.txt", SearchOption.TopDirectoryOnly))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Points the plugins with no launcher config step at this API, in the ladder
    /// folder. Returns their configs as they were, for <see cref="RestoreConfigs"/>
    /// when the session ends - so a real ladder launch afterwards is not left
    /// talking to a local API.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string?>> PointAtApiAsync(string installDirectory, string apiBaseUrl)
    {
        var originals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var value = D2RLoaderPluginPackage.Quote(D2RLoaderPluginPackage.NormalizeBaseUrl(apiBaseUrl));
        foreach (var id in UnmanagedApiPlugins)
        {
            var path = Path.Combine(ModInstallationPaths.LadderModRoot(installDirectory), "d2rloader", "config", id + ".toml");
            if (!File.Exists(Path.Combine(ModInstallationPaths.LadderModRoot(installDirectory), "d2rloader", "plugins", $"d2rl-{id}.dll"))) continue;
            originals[path] = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            var package = new D2RLoaderPluginPackage(id, $"d2rl-{id}.dll", $"# Written by the Reimagined launcher for a local d2rds test.{Environment.NewLine}",
                modName: ModInstallationPaths.LadderModName);
            await package.WriteAsync(installDirectory, new Dictionary<string, string> { ["api_base_url"] = value }, requireInstalled: true,
                CancellationToken.None);
        }

        return originals;
    }

    public static void RestoreConfigs(IReadOnlyDictionary<string, string?> originals)
    {
        foreach (var (path, text) in originals)
        {
            try
            {
                if (text is null) File.Delete(path);
                else File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"local d2rds rig: could not restore {path}: {exception.Message}");
            }
        }
    }

    internal static bool IsKept(string relativePath)
        => KeptPrefixes.Any(prefix => relativePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    // mods\Reimagined's Reimagined.mpq is ReimaginedLadder.mpq in the ladder folder.
    internal static string LadderRelative(string relativePath)
        => ModInstallationPaths.ToLadderPath("mods/Reimagined/" + relativePath)["mods/ReimaginedLadder/".Length..];

    /// <summary>
    /// d2rds.toml in the ladder folder: dormant unless the process has -d2rds, then
    /// a server for this API with server-authoritative saves and no games of its
    /// own (Host makes them). Every player who joins is a cheat operator, as the
    /// host's `op` would make them; a headless server has nobody to type it.
    /// </summary>
    /// <param name="battleNetJoins">
    /// The client is the Reimagined Loader in Battle.net lobby mode. d2rds then takes
    /// Battle.net join tokens, which switches D2RCore's environment handshake off for the
    /// whole process (bnet_join.cpp): every D2RLoader client would time out waiting for
    /// "host acceptance". So it is on only for a Reimagined Loader rig.
    /// </param>
    public static void WriteServerConfig(string installDirectory, string apiBaseUrl, bool battleNetJoins = false)
    {
        if (apiBaseUrl.IndexOfAny(['"', '\\', '\r', '\n']) >= 0) throw new ArgumentException("The API address cannot be written to d2rds.toml.");
        var path = Path.Combine(ModInstallationPaths.LadderModRoot(installDirectory), "d2rloader", "config", "d2rds.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Diagnostic switches a staff member set by hand survive the rewrite (off otherwise).
        var previous = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var logPackets = KeptSwitch(previous, "log_packets");
        var captureJoinSaves = KeptSwitch(previous, "capture_join_saves");
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
            cheat_operators = true
            # Battle.net join tokens (Reimagined Loader client only: it turns off the
            # D2RLoader environment handshake for every client of this server).
            bnet_token_join = {(battleNetJoins ? "true" : "false")}
            # Diagnostics, kept from the previous file when set to true by hand.
            log_packets = {logPackets}
            capture_join_saves = {captureJoinSaves}

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

    /// <summary>"true" when <paramref name="toml"/> sets <paramref name="key"/> = true, else "false".</summary>
    internal static string KeptSwitch(string toml, string key)
    {
        foreach (var raw in toml.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
            var rest = line[key.Length..].TrimStart();
            if (!rest.StartsWith('=')) continue;
            var value = rest[1..].Split('#')[0].Trim();
            return value == "true" ? "true" : "false";
        }
        return "false";
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
        foreach (var name in new[] { "D2R", "D2RLoader", "ReimaginedHost" })
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

    /// <summary>
    /// The d2rds server on the Reimagined Loader: ReimaginedHost.exe with the same
    /// game arguments, so nothing of D2RLoader (D2RLoader.exe, D2RCore.dll) runs.
    /// </summary>
    public static ProcessStartInfo ReimaginedServerStartInfo(string installDirectory)
    {
        var info = new ProcessStartInfo(Path.Combine(installDirectory, "ReimaginedHost.exe"))
        {
            WorkingDirectory = installDirectory,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--game-dir");
        info.ArgumentList.Add(installDirectory);
        info.ArgumentList.Add("--");
        foreach (var argument in ServerStartInfo(installDirectory).Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>The server start for the rig's loader.</summary>
    public static ProcessStartInfo ServerStartInfo(string installDirectory, LocalRigLoader loader)
        => loader == LocalRigLoader.ReimaginedLoader ? ReimaginedServerStartInfo(installDirectory) : ServerStartInfo(installDirectory);

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

    /// <summary>
    /// The client on the Reimagined Loader: the packed host in the D2R folder, with the
    /// same game arguments a D2RLoader ladder launch gets.
    /// </summary>
    public static ProcessStartInfo ReimaginedClientStartInfo(string installDirectory, InstallationProfile profile)
    {
        var info = new ProcessStartInfo(Path.Combine(installDirectory, "ReimaginedHost.exe"))
        {
            WorkingDirectory = installDirectory,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--game-dir");
        info.ArgumentList.Add(installDirectory);
        info.ArgumentList.Add("--");
        foreach (var argument in ClientStartInfo(installDirectory, profile).Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>
    /// Puts lobby-bridge in the ladder folder into Battle.net lobby mode for a
    /// Reimagined Loader session. Returns the file as it was, for <see cref="RestoreConfigs"/>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string?>> UseBattleNetLobbyAsync(string installDirectory)
    {
        var path = Path.Combine(ModInstallationPaths.LadderModRoot(installDirectory), "d2rloader", "config", "lobby-bridge.toml");
        var original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        await File.WriteAllTextAsync(path, WithLobbyMode(original, "battlenet"), new UTF8Encoding(false));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [path] = original };
    }

    /// <summary>The config text with its lobby_mode line set (replaced, or added at the end).</summary>
    internal static string WithLobbyMode(string? text, string mode)
    {
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var setting = $"lobby_mode = \"{mode}\"";
        var index = lines.FindIndex(line => line.TrimStart().StartsWith("lobby_mode", StringComparison.Ordinal)
                                            && line.TrimStart()["lobby_mode".Length..].TrimStart().StartsWith('='));
        if (index >= 0)
        {
            lines[index] = setting;
        }
        else
        {
            lines.Add("# Written by the Reimagined launcher for a local Reimagined Loader test; put back when it ends.");
            lines.Add(setting);
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

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
