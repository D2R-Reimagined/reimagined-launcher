using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Syncs the launch options with a Lutris game's <c>args</c>, the only way in
/// since the <c>lutris:rungameid</c> URI carries no arguments. Only the flags
/// below are added or removed; every other token keeps its place and order.
/// <c>-mod</c> and <c>-txt</c> are left out on purpose: D2RLoader picks the mod
/// through <see cref="D2RLoaderService.SetDefaultMod"/> instead.
/// </summary>
public static class LutrisArgumentsService
{
    private const string EnableRespecFlag = "-enablerespec";
    private const string ResetOfflineMapsFlag = "-resetofflinemaps";
    private const string NoRumbleFlag = "-norumble";
    private const string ForceDesktopFlag = "-forcedesktop";
    private const string NoSoundFlag = "-nosound";
    private const string PlayersFlag = "-players";
    private const string SeedFlag = "-seed";

    private static readonly string[] ToggleFlags =
    [
        EnableRespecFlag, ResetOfflineMapsFlag, NoRumbleFlag, ForceDesktopFlag, NoSoundFlag
    ];

    private static readonly string[] ValueFlags = [PlayersFlag, SeedFlag];

    /// <summary>
    /// Adopts the flags already in the Lutris config, so existing ones are not
    /// silently dropped by the next launch.
    /// </summary>
    public static void ImportInto(InstallationProfile profile, string? args)
    {
        var tokens = Tokenize(args);

        profile.EnableRespec = HasFlag(tokens, EnableRespecFlag);
        profile.ResetOfflineMaps = HasFlag(tokens, ResetOfflineMapsFlag);
        profile.NoRumble = HasFlag(tokens, NoRumbleFlag);
        profile.ForceDesktop = HasFlag(tokens, ForceDesktopFlag);
        profile.NoSound = HasFlag(tokens, NoSoundFlag);

        var players = ReadValue(tokens, PlayersFlag);
        profile.PlayersCount = players is >= 2 and <= 8 ? (int)players.Value : null;

        var seed = ReadValue(tokens, SeedFlag);
        profile.CustomMapSeedEnabled = seed.HasValue;
        profile.CustomMapSeed = seed ?? 0;
    }

    /// <summary>
    /// Rewrites the game's <c>args</c> to match the profile. False only when
    /// the config could not be read or written.
    /// </summary>
    public static bool ApplyToGameConfig(string? slug, InstallationProfile profile)
        => ApplyToGameConfig(LutrisService.GamesConfigDirectory, slug, profile);

    internal static bool ApplyToGameConfig(
        string gamesConfigDirectory,
        string? slug,
        InstallationProfile profile)
    {
        var configPath = LutrisService.FindNewestConfigFile(gamesConfigDirectory, slug);
        if (configPath == null)
        {
            LaunchDiagnostics.Log($"No Lutris config found for '{slug}'; launch options were not applied.");
            return false;
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Failed to read Lutris config {configPath}", ex);
            return false;
        }

        var currentArgs = LutrisService.ExtractGameArgs(yaml);
        var updatedArgs = BuildArgs(currentArgs, profile);
        if (string.Equals(currentArgs ?? string.Empty, updatedArgs, StringComparison.Ordinal))
        {
            LaunchDiagnostics.Log($"Lutris args already match the launch options: '{updatedArgs}'.");
            return true;
        }

        var updatedYaml = UpdateArgsLine(yaml, updatedArgs);
        if (updatedYaml == null)
        {
            LaunchDiagnostics.Log($"Lutris config {configPath} has no game section; launch options were not applied.");
            return false;
        }

        try
        {
            // Same-directory rename, so a failed write cannot truncate the config.
            var temporaryPath = configPath + ".launcher-tmp";
            File.WriteAllText(temporaryPath, updatedYaml);
            File.Move(temporaryPath, configPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Failed to write Lutris config {configPath}", ex);
            return false;
        }

        // The old value is only recoverable from this line, as nothing is backed up.
        LaunchDiagnostics.Log($"Lutris args '{currentArgs ?? string.Empty}' -> '{updatedArgs}' in {configPath}.");
        return true;
    }

    /// <summary>
    /// Drops the managed flags, keeps the rest in order, then appends the ones
    /// the profile enables.
    /// </summary>
    internal static string BuildArgs(string? existingArgs, InstallationProfile profile)
    {
        var foreign = StripManagedFlags(Tokenize(existingArgs));
        var managed = new List<string>();

        if (profile.EnableRespec) managed.Add(EnableRespecFlag);
        if (profile.ResetOfflineMaps) managed.Add(ResetOfflineMapsFlag);

        if (profile.PlayersCount is >= 2 and <= 8)
        {
            managed.Add(PlayersFlag);
            managed.Add(profile.PlayersCount.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (profile.NoRumble) managed.Add(NoRumbleFlag);
        if (profile.ForceDesktop) managed.Add(ForceDesktopFlag);
        if (profile.NoSound) managed.Add(NoSoundFlag);

        if (profile.CustomMapSeedEnabled)
        {
            managed.Add(SeedFlag);
            managed.Add(profile.CustomMapSeed.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" ", foreign.Concat(managed));
    }

    internal static IReadOnlyList<string> Tokenize(string? args)
        => string.IsNullOrWhiteSpace(args)
            ? []
            : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static List<string> StripManagedFlags(IReadOnlyList<string> tokens)
    {
        var kept = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (ToggleFlags.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ValueFlags.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                // Drop the value with the flag it belongs to.
                if (i + 1 < tokens.Count && IsNumeric(tokens[i + 1]))
                {
                    i++;
                }

                continue;
            }

            kept.Add(token);
        }

        return kept;
    }

    private static bool HasFlag(IReadOnlyList<string> tokens, string flag)
        => tokens.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static uint? ReadValue(IReadOnlyList<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (string.Equals(tokens[i], flag, StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsNumeric(string token)
        => uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Replaces the <c>args</c> line in the <c>game:</c> section, leaving every
    /// other line byte-identical. Null when there is no <c>game:</c> section.
    /// </summary>
    internal static string? UpdateArgsLine(string yaml, string args)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        var gameLine = lines.FindIndex(line =>
            LutrisService.IndentOf(line) == 0 && line.TrimEnd().StartsWith("game:", StringComparison.Ordinal));

        if (gameLine < 0)
        {
            return null;
        }

        var argsLine = -1;
        var argsIndent = 0;
        var blockIndent = 0;

        for (var i = gameLine + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = LutrisService.IndentOf(line);
            if (indent == 0)
            {
                break;
            }

            if (blockIndent == 0)
            {
                blockIndent = indent;
            }

            if (indent == blockIndent && line.TrimStart().StartsWith("args:", StringComparison.Ordinal))
            {
                argsLine = i;
                argsIndent = indent;
                break;
            }
        }

        if (blockIndent == 0)
        {
            blockIndent = 2;
        }

        if (argsLine < 0)
        {
            // PyYAML sorts keys, and "args" sorts before the other game keys.
            if (args.Length > 0)
            {
                lines.Insert(gameLine + 1, FormatArgsLine(blockIndent, args));
            }

            return string.Join(newline, lines);
        }

        // A long value may have been folded onto following, more-indented lines.
        var lastLine = argsLine;
        for (var i = argsLine + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)
                || LutrisService.IndentOf(line) <= argsIndent
                || LutrisService.IsKeyLine(line))
            {
                break;
            }

            lastLine = i;
        }

        lines.RemoveRange(argsLine, lastLine - argsLine + 1);

        if (args.Length > 0)
        {
            lines.Insert(argsLine, FormatArgsLine(argsIndent, args));
        }

        return string.Join(newline, lines);
    }

    private static string FormatArgsLine(int indent, string args)
        => $"{new string(' ', indent)}args: '{args.Replace("'", "''", StringComparison.Ordinal)}'";
}
