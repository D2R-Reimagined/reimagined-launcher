using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LutrisGame(int Id, string Slug, string Name, string Runner, string Platform)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Slug : Name;
}

/// <summary>
/// Read-only view of a local Lutris installation. Paths come from the game's
/// YAML config because <c>lutris -loj</c> reports a null directory for most
/// entries. Nothing under the Lutris data directory is ever written.
/// </summary>
public static class LutrisService
{
    private const string ExecutableName = "lutris";
    private const int ListTimeoutMilliseconds = 30_000;
    private const string ProcRoot = "/proc";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static readonly Lock ListingLock = new();
    private static Task<IReadOnlyList<LutrisGame>>? _listing;

    public static bool IsAvailable() => GameLauncherService.FindExecutableOnPath(ExecutableName) != null;

    /// <summary>
    /// Lutris is a single-instance GApplication: a second concurrent
    /// <c>lutris -loj</c> is dispatched to the primary instance and exits
    /// non-zero with no output, so the listing is shared between callers.
    /// </summary>
    public static Task<IReadOnlyList<LutrisGame>> GetInstalledGamesAsync(bool forceRefresh = false)
    {
        lock (ListingLock)
        {
            if (forceRefresh || _listing is null)
            {
                _listing = ListInstalledGamesAsync();
            }

            return _listing;
        }
    }

    public static string GamesConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "lutris", "games");

    public static string BuildRunGameUri(int gameId) => $"lutris:rungameid/{gameId}";

    private static async Task<IReadOnlyList<LutrisGame>> ListInstalledGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = GameLauncherService.FindExecutableOnPath(ExecutableName);
        if (executablePath == null)
        {
            return [];
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            // -l list, -o installed only, -j JSON
            Arguments = "-loj",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return [];
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ListTimeoutMilliseconds);

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var games = ParseGameList(output);
            LaunchDiagnostics.Log(
                $"lutris -loj exit={process.ExitCode} stdoutBytes={output.Length} games={games.Count}");
            return games;
        }
        catch (OperationCanceledException)
        {
            LaunchDiagnostics.Log("Timed out while listing Lutris games.");
            return [];
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to list Lutris games", ex);
            return [];
        }
    }

    public static string? TryResolveInstallDirectory(string? slug)
        => ResolveInstallDirectory(GamesConfigDirectory, slug);

    /// <summary>
    /// Lutris keeps the prefix beside the game rather than above it, so it
    /// cannot be found by walking up from the install directory.
    /// </summary>
    public static string? TryResolveWinePrefix(string? slug)
        => ResolveWinePrefix(GamesConfigDirectory, slug);

    internal static string? ResolveWinePrefix(string gamesConfigDirectory, string? slug)
    {
        var prefix = ReadGameConfig(gamesConfigDirectory, slug, ExtractGamePrefixPath);
        return string.IsNullOrWhiteSpace(prefix) ? null : prefix;
    }

    /// <summary>
    /// The executable Lutris actually starts, which could be D2R.exe
    /// or D2RLoader.exe.
    /// </summary>
    public static string? TryResolveGameExePath(string? slug)
        => ReadGameConfig(GamesConfigDirectory, slug, ExtractGameExePath);

    /// <summary>
    /// Lowest pid whose command line references <paramref name="exePath"/>.
    /// Lutris runs the game behind a wrapper chain that all carries the exe path
    /// and exits with the session; the lowest pid is the outermost wrapper.
    /// </summary>
    internal static int? FindGameProcessId(string procRoot, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Directory.Exists(procRoot))
        {
            return null;
        }

        var fileName = Path.GetFileName(exePath);
        var ownPid = Environment.ProcessId;
        int? lowest = null;

        foreach (var directory in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == ownPid)
            {
                continue;
            }

            string commandLine;
            try
            {
                var commandLinePath = Path.Combine(directory, "cmdline");
                if (!File.Exists(commandLinePath))
                {
                    continue;
                }

                commandLine = File.ReadAllText(commandLinePath).Replace('\0', ' ');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (commandLine.Length == 0)
            {
                continue;
            }

            // Wine rewrites the path to a drive letter, leaving only the file name.
            if (commandLine.Contains(exePath, StringComparison.Ordinal)
                || commandLine.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (lowest is null || pid < lowest)
                {
                    lowest = pid;
                }
            }
        }

        return lowest;
    }

    /// <summary>
    /// Waits for a game session to appear and then end. False when it never
    /// appears within <paramref name="discoveryTimeout"/>.
    /// </summary>
    public static async Task<bool> WaitForGameSessionAsync(
        string? exePath,
        TimeSpan discoveryTimeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var deadline = DateTime.UtcNow + discoveryTimeout;
        int? pid = null;

        while (DateTime.UtcNow < deadline)
        {
            pid = FindGameProcessId(ProcRoot, exePath);
            if (pid is not null)
            {
                break;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        if (pid is null)
        {
            LaunchDiagnostics.Log($"No Lutris game process found for '{exePath}' within {discoveryTimeout}.");
            return false;
        }

        LaunchDiagnostics.Log($"Watching Lutris game session pid {pid}.");
        while (Directory.Exists(Path.Combine(ProcRoot, pid.Value.ToString())))
        {
            await Task.Delay(PollInterval, cancellationToken);
        }

        LaunchDiagnostics.Log($"Lutris game session pid {pid} exited.");
        return true;
    }

    internal static IReadOnlyList<LutrisGame> ParseGameList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var games = new List<LutrisGame>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                games.Add(new LutrisGame(
                    id.GetInt32(),
                    ReadString(element, "slug"),
                    ReadString(element, "name"),
                    ReadString(element, "runner"),
                    ReadString(element, "platform")));
            }

            return games;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string? ResolveInstallDirectory(string gamesConfigDirectory, string? slug)
    {
        var exePath = ReadGameConfig(gamesConfigDirectory, slug, ExtractGameExePath);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(exePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadGameConfig(
        string gamesConfigDirectory,
        string? slug,
        Func<string?, string?> selector)
    {
        var configPath = FindNewestConfigFile(gamesConfigDirectory, slug);
        if (configPath == null)
        {
            return null;
        }

        try
        {
            return selector(File.ReadAllText(configPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Failed to read Lutris config {configPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Newest <c>&lt;slug&gt;-&lt;timestamp&gt;.yml</c>. The timestamp must be numeric so a
    /// sibling slug sharing this one as a prefix is not mistaken for it.
    /// </summary>
    internal static string? FindNewestConfigFile(string gamesConfigDirectory, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)
            || slug.Contains('/')
            || slug.Contains('\\')
            || slug.Contains("..")
            || !Directory.Exists(gamesConfigDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(gamesConfigDirectory, $"{slug}-*.yml")
                .Select(path => (Path: path, Stamp: ParseConfigTimestamp(path, slug)))
                .Where(candidate => candidate.Stamp.HasValue)
                .OrderByDescending(candidate => candidate.Stamp!.Value)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Failed to enumerate Lutris configs in {gamesConfigDirectory}", ex);
            return null;
        }
    }

    internal static string? ExtractGameExePath(string? yaml) => ExtractGameValue(yaml, "exe");

    internal static string? ExtractGamePrefixPath(string? yaml) => ExtractGameValue(yaml, "prefix");

    /// <summary>
    /// Reads one key from the <c>game:</c> section. PyYAML wraps long plain
    /// scalars at 80 columns, breaking on spaces, so a value may continue on
    /// following more-indented lines; folded lines rejoin with a single space.
    /// </summary>
    private static string? ExtractGameValue(string? yaml, string key)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        var keyPrefix = $"{key}:";

        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var inGameSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = IndentOf(line);
            if (indent == 0)
            {
                inGameSection = line.TrimEnd().StartsWith("game:", StringComparison.Ordinal);
                continue;
            }

            if (!inGameSection)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = trimmed[keyPrefix.Length..].Trim();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var continuation = lines[j];
                if (string.IsNullOrWhiteSpace(continuation)
                    || IndentOf(continuation) <= indent
                    || IsKeyLine(continuation))
                {
                    break;
                }

                value = $"{value} {continuation.Trim()}";
            }

            value = StripQuotes(value);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static long? ParseConfigTimestamp(string path, string slug)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length <= slug.Length + 1)
        {
            return null;
        }

        var suffix = name[(slug.Length + 1)..];
        return long.TryParse(suffix, out var stamp) ? stamp : null;
    }

    private static int IndentOf(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return index;
    }

    private static bool IsKeyLine(string line)
    {
        var trimmed = line.TrimStart();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        return trimmed[..colon].All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-');
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2
            && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
