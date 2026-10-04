using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

internal sealed record SteamHandoffRegistration(uint AppId, string SteamRoot, string LauncherPath);
internal sealed record SteamHandoffRequest(string Id, DateTimeOffset Created, string SteamRoot, string GameDirectory,
    string ProtonExecutable, string Executable, string[] Arguments);
internal sealed record SteamHandoffStatus(string Id, string State, string? Error = null);
internal sealed record SteamSetupAccount(string UserId, string Name, bool MostRecent)
{
    public override string ToString() => $"{Name} ({UserId})";
}

internal sealed class SteamGameHandoff : IDisposable
{
    private readonly FileStream _launchLock;
    private readonly string _directory;
    private readonly string _id;
    internal static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReimaginedLauncher", "steam-handoff");
    internal static bool IsGamingMode => OperatingSystem.IsLinux()
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY_0"))
            || Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "gamescope");

    private SteamGameHandoff(string directory, string id, FileStream launchLock)
        => (_directory, _id, _launchLock) = (directory, id, launchLock);

    internal static SteamSetupAccount[] GetSetupAccounts(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var accounts = Directory.GetDirectories(userdata)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id) && id != "0" && id.All(char.IsAsciiDigit)
                && Directory.Exists(Path.Combine(userdata, id, "config")))
            .Select(id => new SteamSetupAccount(id!, "Steam account", false))
            .ToDictionary(account => account.UserId);
        var loginUsers = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(loginUsers), "\"([0-9]{17})\"\\s*\\{([^{}]*)\\}"))
            {
                if (!ulong.TryParse(match.Groups[1].Value, out var steamId) || steamId < 76561197960265728) continue;
                var id = (steamId - 76561197960265728).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!accounts.ContainsKey(id)) continue;
                var body = match.Groups[2].Value;
                var persona = Regex.Match(body, "\"PersonaName\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
                var name = persona.Success ? persona.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : "Steam account";
                accounts[id] = new SteamSetupAccount(id, name,
                    Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\"", RegexOptions.IgnoreCase));
            }
        }
        return accounts.Values.OrderByDescending(account => account.MostRecent).ThenBy(account => account.UserId).ToArray();
    }

    internal static string ResolveSetupLauncherPath(string? appImage, string? processPath)
    {
        var path = !string.IsNullOrWhiteSpace(appImage) ? appImage : processPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The launcher executable could not be located. Reopen the AppImage and try again.");
        path = Path.GetFullPath(path);
        if (path.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".mount_", StringComparison.Ordinal)))
            throw new InvalidOperationException("Open the original AppImage before setting up Steam shortcuts.");
        return path;
    }

    internal static string PrepareSetupAppImage(string source, string destinationDirectory)
    {
        var destination = Path.GetFullPath(Path.Combine(destinationDirectory, "D2RReimagined.ReimaginedLauncher.AppImage"));
        if (Path.GetFullPath(source) == Path.GetFullPath(destination)) return destination;
        Directory.CreateDirectory(destinationDirectory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(source) | UnixFileMode.UserExecute);
            if (File.Exists(destination))
                File.Copy(destination, destination + "." + Guid.NewGuid().ToString("N") + ".bak");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void InstallFromLauncher(string steamRoot, string userId, bool resetMouseOnly = false)
    {
        if (!OperatingSystem.IsLinux() || IsGamingMode)
            throw new InvalidOperationException("Set up Steam shortcuts in Desktop Mode, with Steam fully exited.");
        if (GameLauncherService.IsSteamFlatpakInstall())
            throw new InvalidOperationException("Steam shortcut setup currently requires native Steam, not Flatpak Steam.");
        if (Process.GetProcessesByName("steam").Any(IsLiveSteamProcess))
            throw new InvalidOperationException("In desktop Steam, choose Steam → Exit, then click Set up Steam shortcuts again.");
        if (!GetSetupAccounts(steamRoot).Any(account => account.UserId == userId))
            throw new InvalidOperationException("Select an existing Steam account before setting up shortcuts.");
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var path = ResolveSetupLauncherPath(appImage, Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(appImage))
            path = PrepareSetupAppImage(path, Path.Combine(StateDirectory, "..", "launcher"));
        Install(steamRoot, userId, path, resetMouseOnly);
    }

    internal static uint Install(string steamRoot, string userId, string launcherPath, bool resetMouseOnly = false)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Steam handoff setup is Linux-only.");
        if (Process.GetProcessesByName("steam").Any(IsLiveSteamProcess))
            throw new InvalidOperationException("Exit Steam before registering the launcher shortcuts, then restart Steam.");
        if (!userId.All(char.IsAsciiDigit) || userId.Length == 0 || !File.Exists(launcherPath)
            || !Directory.Exists(Path.Combine(steamRoot, "userdata", userId, "config")))
            throw new InvalidOperationException("Specify the installed native Steam root, existing userdata ID, and stable launcher executable.");
        var directory = StateDirectory;
        Directory.CreateDirectory(directory);
        var quotedPath = "\"" + Path.GetFullPath(launcherPath) + "\"";
        var game = new LauncherSteamShortcut("Diablo II: Reimagined (Launcher Game Session)", quotedPath,
            "--steam-game \"" + directory + "\"", "D2R Reimagined (Launcher Game Session)");
        var gui = new LauncherSteamShortcut("Diablo II: Reimagined (Native Launcher)", quotedPath, "",
            "D2R Reimagined (Native Launcher)");
        var path = Path.Combine(steamRoot, "userdata", userId, "config", "shortcuts.vdf");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var updated = SteamShortcutFile.AppendOwnedShortcuts(original, game, gui);
        if (!updated.SequenceEqual(original))
        {
            if (File.Exists(path)) File.Copy(path, path + ".reimagined-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak", false);
            WriteShortcutFile(path, updated);
        }
        WriteJson(Path.Combine(directory, "registration.json"), new SteamHandoffRegistration(game.AppId, steamRoot, launcherPath));
        SteamControllerLayout.SetUpLauncher(steamRoot, userId, gui.LegacyName!, gui.Name, resetMouseOnly);
        Console.WriteLine($"Registered launcher-owned game session {game.AppId}; GUI game ID {gui.GameId}. Restart Steam before launching.");
        return game.AppId;
    }

    private static bool IsLiveSteamProcess(Process process)
    {
        using (process)
        {
            try
            {
                if (process.HasExited) return false;
                var stat = File.ReadAllText($"/proc/{process.Id}/stat");
                var stateIndex = stat.LastIndexOf(')') + 2;
                return stateIndex < 2 || stateIndex >= stat.Length || stat[stateIndex] is not ('Z' or 'X');
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { return !process.HasExited; }
        }
    }

    internal static (SteamGameHandoff Session, Process Command) Start(InstallationProfile profile, string arguments)
    {
        var directory = StateDirectory;
        var registration = ReadJson<SteamHandoffRegistration>(Path.Combine(directory, "registration.json"));
        var gameDirectory = profile.InstallDirectory!;
        var executable = Path.Combine(gameDirectory, GameLauncherService.UsesD2RLoader(profile) ? "D2RLoader.exe" : "D2R.exe");
        if (!GameLauncherService.IsValidSteamProtonLaunch(profile, registration.SteamRoot, executable, out var error))
            throw new InvalidOperationException(error);
        if (!File.Exists(executable)) throw new InvalidOperationException("The selected game executable is missing.");
        if (FindGameProcesses(Path.Combine(gameDirectory, "D2R.exe")).Length != 0
            || FindGameProcesses(Path.Combine(gameDirectory, "D2RLoader.exe")).Length != 0)
            throw new InvalidOperationException("Close the running D2R game before starting another session.");
        var launchLock = new FileStream(Path.Combine(directory, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var request = new SteamHandoffRequest(id, DateTimeOffset.UtcNow, registration.SteamRoot, gameDirectory,
                profile.ProtonExecutable!, executable, SplitArguments(arguments));
            WriteJson(Path.Combine(directory, "request.json"), request);
            WriteJson(Path.Combine(directory, "status.json"), new SteamHandoffStatus(id, "pending"));
            var command = new ProcessStartInfo(GameLauncherService.FindExecutableOnPath("steam")
                ?? throw new InvalidOperationException("Native Steam was not found on PATH.")) { UseShellExecute = false };
            command.ArgumentList.Add("steam://rungameid/" + (((ulong)registration.AppId << 32) | 0x02000000));
            foreach (var key in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId", "LD_PRELOAD" }) command.Environment.Remove(key);
            var process = Process.Start(command) ?? throw new InvalidOperationException("Steam did not accept the game handoff.");
            LaunchDiagnostics.Log($"Steam handoff {id}: launcher-owned session {registration.AppId}, {Path.GetFileName(executable)} {arguments}");
            return (new SteamGameHandoff(directory, id, launchLock), process);
        }
        catch { launchLock.Dispose(); throw; }
    }

    internal async Task<bool> WaitForExitAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var started = false;
        while (started || DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadJson<SteamHandoffStatus>(Path.Combine(_directory, "status.json"));
            if (status.Id != _id) throw new InvalidOperationException("Steam handoff session was replaced unexpectedly.");
            if (status.State == "failed") throw new InvalidOperationException(status.Error ?? "Steam game session failed.");
            if (status.State == "exited") return true;
            started |= status.State == "running";
            if (started && !FileIsLocked(Path.Combine(_directory, "game.lock")))
                throw new InvalidOperationException("Steam stopped the game helper before it confirmed game exit.");
            await Task.Delay(500);
        }
        throw new TimeoutException("Steam did not start the registered game session. Restart Steam after shortcut setup.");
    }

    internal static int RunGame(string directory)
    {
        if (Path.GetFullPath(directory) != Path.GetFullPath(StateDirectory))
            throw new InvalidOperationException("The game helper requires the launcher handoff directory.");
        using var gameLock = new FileStream(Path.Combine(directory, "game.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var request = ReadJson<SteamHandoffRequest>(Path.Combine(directory, "request.json"));
        var registration = ReadJson<SteamHandoffRegistration>(Path.Combine(directory, "registration.json"));
        if (DateTimeOffset.UtcNow - request.Created > TimeSpan.FromMinutes(2) || request.Created > DateTimeOffset.UtcNow.AddSeconds(30)
            || request.SteamRoot != registration.SteamRoot || !FileIsLocked(Path.Combine(directory, "launch.lock")))
            throw new InvalidOperationException("No current launcher game request. Start the game from the native launcher.");
        var ownGameId = (((ulong)registration.AppId << 32) | 0x02000000).ToString();
        if (Environment.GetEnvironmentVariable("SteamGameId") != ownGameId)
            throw new InvalidOperationException("Start the game helper through its separate registered Steam shortcut.");
        try
        {
            var info = CreateGameStartInfo(request);
            WriteJson(Path.Combine(directory, "status.json"), new SteamHandoffStatus(request.Id, "starting"));
            using var proton = Process.Start(info) ?? throw new InvalidOperationException("Proton could not start.");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            int[] pids;
            while ((pids = FindGameProcesses(request.Executable)).Length == 0)
            {
                if (proton.HasExited || DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("Proton exited or timed out before the game started.");
                System.Threading.Thread.Sleep(250);
            }
            WriteJson(Path.Combine(directory, "status.json"), new SteamHandoffStatus(request.Id, "running"));
            while ((pids = FindGameProcesses(request.Executable)).Length != 0)
            {
                System.Threading.Thread.Sleep(500);
            }
            WriteJson(Path.Combine(directory, "status.json"), new SteamHandoffStatus(request.Id, "exited"));
            return 0;
        }
        catch (Exception exception)
        {
            WriteJson(Path.Combine(directory, "status.json"), new SteamHandoffStatus(request.Id, "failed", exception.Message));
            return 1;
        }
    }

    internal static ProcessStartInfo CreateGameStartInfo(SteamHandoffRequest request)
    {
        var profile = new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = request.GameDirectory,
            ProtonExecutable = request.ProtonExecutable };
        if (!GameLauncherService.IsValidSteamProtonLaunch(profile, request.SteamRoot, request.Executable, out var error))
            throw new InvalidOperationException(error);
        if (request.Executable != Path.Combine(request.GameDirectory, "D2R.exe")
            && request.Executable != Path.Combine(request.GameDirectory, "D2RLoader.exe"))
            throw new InvalidOperationException("Unsupported Steam game executable.");
        var info = new ProcessStartInfo(request.ProtonExecutable) { UseShellExecute = false, WorkingDirectory = request.GameDirectory };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add(request.Executable);
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        var environment = new Dictionary<string, string>();
        GameLauncherService.PopulateSteamProtonEnvVars(environment, profile, request.SteamRoot,
            FileService.FindAncestorDirectory(request.GameDirectory, "steamapps"));
        foreach (var (key, value) in environment) info.Environment[key] = value;
        info.Environment["WINE_SIMULATE_WRITECOPY"] = "1";
        if (Path.GetFileName(request.Executable) == "D2RLoader.exe")
        {
            info.Environment.TryGetValue("WINEDLLOVERRIDES", out var existing);
            info.Environment["WINEDLLOVERRIDES"] = (string.IsNullOrWhiteSpace(existing) ? "" : existing + ";") + "loader=n,b;dinput8=n,b;version=n,b";
        }
        return info;
    }

    internal static string[] SplitArguments(string text)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var value = text[i];
            if (value == '"') { quoted = !quoted; started = true; }
            else if (quoted && value == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\') token.Append(text[++i]);
            else if (!quoted && char.IsWhiteSpace(value))
            {
                if (!started) continue;
                result.Add(token.ToString()); token.Clear(); started = false;
            }
            else { token.Append(value); started = true; }
        }
        if (quoted) throw new ArgumentException("Launch arguments contain an unmatched quote.");
        if (started) result.Add(token.ToString());
        return result.ToArray();
    }

    internal static int[] FindGameProcesses(string executable)
    {
        var result = new List<int>();
        var drives = new Dictionary<char, string>();
        var apps = FileService.FindAncestorDirectory(Path.GetDirectoryName(executable), "steamapps");
        var devices = apps is null ? null : Path.Combine(apps, "compatdata", "2536520", "pfx", "dosdevices");
        if (devices is not null && Directory.Exists(devices))
        {
            foreach (var device in Directory.EnumerateFileSystemEntries(devices))
            {
                var name = Path.GetFileName(device);
                if (name.Length != 2 || name[1] != ':' || !char.IsAsciiLetter(name[0])) continue;
                try
                {
                    if (new DirectoryInfo(device).ResolveLinkTarget(true) is { } target)
                        drives[char.ToUpperInvariant(name[0])] = target.FullName;
                }
                catch (IOException) { }
            }
        }
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                if (MatchesGameCommandLine(File.ReadAllText(Path.Combine(directory, "cmdline")), executable, drives)) result.Add(pid);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }

    internal static bool MatchesGameCommandLine(string commandLine, string executable, IReadOnlyDictionary<char, string>? drives = null)
    {
        var argv0 = commandLine.Split('\0')[0].Replace('\\', '/');
        if (argv0.Length >= 3 && argv0[1] == ':' && argv0[2] == '/')
        {
            if (drives is not null && drives.TryGetValue(char.ToUpperInvariant(argv0[0]), out var root))
                argv0 = Path.GetFullPath(Path.Combine(root, argv0[3..]));
            else if (argv0.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)) argv0 = argv0[2..];
        }
        return string.Equals(argv0.Replace('\\', '/'), executable.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileIsLocked(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return File.Exists(path); }
    }

    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Invalid Steam handoff configuration.");
    private static void WriteJson<T>(string path, T value) => AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value));
    internal static void WriteShortcutFile(string path, byte[] data)
    {
        UnixFileMode? permissions = OperatingSystem.IsLinux() && File.Exists(path)
            ? File.GetUnixFileMode(path) : null;
        AtomicWrite(path, data, permissions);
    }

    private static void AtomicWrite(string path, byte[] data, UnixFileMode? permissions = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            if (OperatingSystem.IsLinux() && permissions is { } mode) File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => _launchLock.Dispose();
}
