using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

/// <summary>The secrets one game session's plugins need, and nothing else.</summary>
public sealed record GameSessionSecrets(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string? LadderLaunchTicket = null,
    string? StatusSessionId = null);

/// <summary>
/// Keeps the game session's secrets out of the mod folder.
/// </summary>
/// <remarks>
/// <para>D2RLoader can copy a host's mod folder to a joining player, so anything
/// under <c>mods/</c> must be safe to give away. The plugins' own TOML configs
/// live there, so the launcher writes <c>access_token</c>,
/// <c>ladder_launch_ticket</c> and <c>status_session_id</c> as empty strings in
/// them and puts the real values in
/// <c>&lt;install&gt;/reimagined-secrets/session.toml</c> instead - the D2R
/// install root, next to D2R.exe, never under <c>mods/</c>.</para>
///
/// <para>The file is rewritten before every Online/Ladder launch, deleted when
/// the game exits, on sign-out and at launcher startup when no game is running.
/// Plugins ignore it once <c>expires_at_unix</c> has passed, so a file that
/// outlives its session (a crash, a killed launcher) goes inert on its own.</para>
/// </remarks>
public static class GameSessionSecretsService
{
    public const string DirectoryName = "reimagined-secrets";
    public const string FileName = "session.toml";

    /// <summary>The keys that must never hold a real value inside mods/.</summary>
    internal static readonly IReadOnlyList<string> SecretKeys =
        ["access_token", "ladder_launch_ticket", "status_session_id"];

    /// <summary>
    /// Our plugins whose configs have ever carried a secret. lobby-bridge never
    /// had one written by the launcher, but it accepts overrides of the
    /// server-saves keys in its own config, so it is scrubbed too.
    /// </summary>
    internal static readonly IReadOnlyList<string> ScrubbedPluginIds =
    [
        AnnouncementsConfigService.PluginId,
        ChatRelayConfigService.PluginId,
        GlobalChatConfigService.PluginId,
        HardcoreDeathsConfigService.PluginId,
        ReimaginedFeedbackConfigService.PluginId,
        ServerSavesConfigService.PluginId,
        SupporterPortalsConfigService.PluginId,
        TradeNotificationsConfigService.PluginId,
        TradePriceCheckConfigService.PluginId,
        "lobby-bridge"
    ];

    private const string Header =
        "# Reimagined launcher - secrets for the running game session.\n"
        + "# Rewritten before every launch and deleted when the game exits. Never share\n"
        + "# this file. It lives outside mods/ so D2RLoader never hands it to other players.\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string? GetPath(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : Path.Combine(normalized, DirectoryName, FileName);
    }

    /// <summary>
    /// The file's exact content. Values are TOML basic strings; a value with a
    /// control character is refused rather than escaped, because a newline in a
    /// flat-line file is a way to smuggle in another key.
    /// </summary>
    internal static string Format(GameSessionSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var builder = new StringBuilder(Header);
        builder.Append("access_token = ").Append(QuoteValue(secrets.AccessToken, "access_token")).Append('\n');
        builder.Append("expires_at_unix = ")
            .Append(secrets.ExpiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        builder.Append("ladder_launch_ticket = ")
            .Append(QuoteValue(secrets.LadderLaunchTicket ?? string.Empty, "ladder_launch_ticket"))
            .Append('\n');
        builder.Append("status_session_id = ")
            .Append(QuoteValue(secrets.StatusSessionId ?? string.Empty, "status_session_id"))
            .Append('\n');
        return builder.ToString();
    }

    private static string QuoteValue(string value, string key)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"{key} contains a control character and cannot be written to the session file.");
        }

        return D2RLoaderPluginPackage.Quote(value);
    }

    /// <summary>
    /// Writes the session file atomically (temp file in the same folder, then a
    /// replacing move), restricted to the current user where the platform allows.
    /// Returns false on failure; the reason is in the launch log.
    /// </summary>
    public static async Task<bool> WriteAsync(
        string? installDirectory,
        GameSessionSecrets secrets,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(installDirectory);
        if (path is null)
        {
            LaunchDiagnostics.Log("session secrets: no install directory; the session file was not written.");
            return false;
        }

        string content;
        try
        {
            content = Format(secrets);
        }
        catch (ArgumentException exception)
        {
            LaunchDiagnostics.Log($"session secrets: {exception.Message}");
            return false;
        }

        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = Utf8NoBom.GetBytes(content);
            await using (var stream = CreateRestrictedFile(temporary))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
            LaunchDiagnostics.Log($"session secrets: wrote {path} (expires {secrets.ExpiresAtUtc:u}).");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"session secrets: could not write {path}: {exception.Message}");
            TryDeleteFile(temporary);
            return false;
        }
    }

    /// <summary>
    /// Deletes the session file and any temp file a failed write left behind.
    /// A file that is already gone is success.
    /// </summary>
    public static bool Delete(string? installDirectory)
    {
        var path = GetPath(installDirectory);
        if (path is null)
        {
            return true;
        }

        var deleted = TryDeleteFile(path);
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                foreach (var leftover in Directory.EnumerateFiles(directory, $"{FileName}.*.tmp"))
                {
                    TryDeleteFile(leftover);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"session secrets: could not clean {path}'s folder: {exception.Message}");
        }

        return deleted;
    }

    /// <summary>
    /// Blanks every secret key in every one of our plugins' configs, in both
    /// mod folders, the global loader folder and the launcher's stashed copies
    /// of the normal mod (see EnumerateConfigDirectories) - including plugins that are
    /// currently disabled. Only rewrites keys that are present, and only files
    /// that actually change. Returns how many files were rewritten.
    /// </summary>
    public static async Task<int> ScrubPluginConfigsAsync(
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 0;
        }

        var scrubbed = 0;
        foreach (var directory in EnumerateConfigDirectories(normalized))
        {
            foreach (var pluginId in ScrubbedPluginIds)
            {
                var path = Path.Combine(directory, $"{pluginId}.toml");
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var existing = await File.ReadAllTextAsync(path, cancellationToken);
                    var updated = ScrubSecrets(existing);
                    if (string.Equals(existing, updated, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await WriteReplacingAsync(path, updated, cancellationToken);
                    scrubbed++;
                    LaunchDiagnostics.Log($"session secrets: blanked secrets left in {path}.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LaunchDiagnostics.Log($"session secrets: could not scrub {path}: {exception.Message}");
                }
            }
        }

        return scrubbed;
    }

    /// <summary>
    /// Every config folder that can hold our plugins' configs: both mod folders,
    /// the global loader folder, and the launcher's own copies of the normal mod
    /// under .reimagined-launcher - the stash that switching back from a ladder
    /// copies into mods/Reimagined, and the transaction backups each such swap
    /// leaves behind. Those copies were taken while the launcher still wrote
    /// tokens into mods/, and the stash would otherwise put them right back.
    /// </summary>
    private static IEnumerable<string> EnumerateConfigDirectories(string installDirectory)
    {
        yield return Path.Combine(installDirectory, "mods", ModInstallationPaths.NormalModName, "d2rloader", "config");
        yield return Path.Combine(installDirectory, "mods", ModInstallationPaths.LadderModName, "d2rloader", "config");
        yield return Path.Combine(installDirectory, "d2rloader", "config");

        var management = Path.Combine(installDirectory, ".reimagined-launcher");
        yield return Path.Combine(NormalModInstallationService.NormalModRoot(installDirectory), "d2rloader", "config");

        var transactions = Path.Combine(management, "mod-backups");
        string[] transactionDirectories;
        try
        {
            transactionDirectories = Directory.Exists(transactions)
                ? Directory.GetDirectories(transactions)
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"session secrets: could not list {transactions}: {exception.Message}");
            transactionDirectories = [];
        }

        foreach (var transaction in transactionDirectories)
        {
            yield return Path.Combine(transaction, "previous", "d2rloader", "config");
            yield return Path.Combine(transaction, "staged", "d2rloader", "config");
        }
    }

    /// <summary>
    /// Rewrites every assignment of a secret key to an empty string, keeping
    /// every other line - comments, other settings, line endings - as it was.
    /// Keys that are not there are not added.
    /// </summary>
    internal static string ScrubSecrets(string toml)
    {
        var lines = toml.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var key in SecretKeys)
            {
                if (!D2RLoaderPluginPackage.IsAssignmentOf(lines[index], key))
                {
                    continue;
                }

                var trailingReturn = lines[index].EndsWith('\r') ? "\r" : string.Empty;
                lines[index] = $"{key} = {D2RLoaderPluginPackage.BlankSecret}{trailingReturn}";
                break;
            }
        }

        return string.Join("\n", lines);
    }

    private static async Task WriteReplacingAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, Utf8NoBom, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> readable only by the current user where
    /// that can be arranged. Failing to restrict it is logged, never fatal: the
    /// file still leaves mods/, which is the part that matters.
    /// </summary>
    private static FileStream CreateRestrictedFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return CreateWithCurrentUserOnlyAcl(path);
            }
            catch (Exception exception) when (exception is not IOException)
            {
                LaunchDiagnostics.Log($"session secrets: could not restrict the file to the current user ({exception.Message}); writing it with inherited permissions.");
                TryDeleteFile(path);
            }
        }
        else
        {
            try
            {
                return new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            }
            catch (Exception exception) when (exception is not IOException)
            {
                LaunchDiagnostics.Log($"session secrets: could not restrict the file to the current user ({exception.Message}); writing it with default permissions.");
                TryDeleteFile(path);
            }
        }

        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWithCurrentUserOnlyAcl(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("the current user has no SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Synchronize,
            FileShare.None,
            4096,
            FileOptions.None,
            security);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.Log($"session secrets: could not delete {path}: {exception.Message}");
            return false;
        }
    }
}
