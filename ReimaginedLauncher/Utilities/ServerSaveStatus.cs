using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record ServerSaveMonitor(string StatusPath, string SessionId, Guid LadderId);

public sealed record ServerSaveConfirmation(
    string FileName, string CharacterName, long Version, string Sha256, DateTimeOffset LastSavedAtUtc);

public sealed record ServerSaveStatus(
    string SessionId, Guid LadderId, string State, string Message, string Code,
    string RecoveryNotice, DateTimeOffset UpdatedAtUtc, bool Running,
    IReadOnlyList<ServerSaveConfirmation> ConfirmedSaves)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ServerSaveStatus? Parse(string json, ServerSaveMonitor monitor)
    {
        try
        {
            var status = JsonSerializer.Deserialize<ServerSaveStatus>(json, JsonOptions);
            return status is not null
                   && status.SessionId == monitor.SessionId && status.LadderId == monitor.LadderId
                   && status.State is "checking" or "ready" or "pending" or "retrying" or "stopped"
                   && status.Message is { Length: <= 1024 } && status.RecoveryNotice is { Length: <= 1024 }
                   && status.UpdatedAtUtc != default && status.ConfirmedSaves is { Count: <= 64 }
                   && status.ConfirmedSaves.All(save => save is not null
                       && !string.IsNullOrWhiteSpace(save.FileName)
                       && save.Version > 0 && save.Sha256 is { Length: 64 } && save.Sha256.All(Uri.IsHexDigit)
                       && save.LastSavedAtUtc != default)
                ? status : null;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<ServerSaveStatus?> ReadAsync(ServerSaveMonitor monitor, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(monitor.StatusPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > 64 * 1024) return null;
            using var reader = new StreamReader(stream);
            return Parse(await reader.ReadToEndAsync(cancellationToken), monitor);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public ServerSaveStatusDisplay Describe(DateTimeOffset now, bool sessionEnded)
    {
        var stale = !sessionEnded && now - UpdatedAtUtc > TimeSpan.FromSeconds(45);
        var warning = stale || State is "retrying" or "stopped" || !string.IsNullOrEmpty(RecoveryNotice)
                      || sessionEnded && (Running || State is "pending" or "checking");
        var message = stale ? "Save status has not updated recently. Recent progress is not confirmed saved."
            : sessionEnded && (Running || State is "pending" or "checking") && State != "stopped"
                ? "The game closed without confirming its final save. Recovery may be needed on the next launch."
                : Message;
        var characters = ConfirmedSaves.Where(save => save.FileName.EndsWith(".d2s", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(save => save.LastSavedAtUtc).ToArray();
        var confirmed = characters.Length == 0 ? "No character save was confirmed during this session."
            : "Last confirmed server saves:\n" + string.Join("\n", characters.Select(save =>
                $"{Path.GetFileNameWithoutExtension(save.FileName)} — {save.LastSavedAtUtc.ToLocalTime():g} (v{save.Version})"));
        if (!string.IsNullOrEmpty(RecoveryNotice)) message += "\n" + RecoveryNotice;
        return new ServerSaveStatusDisplay(message, confirmed, warning, stale ? "status_stale" : State + ":" + Code + ":" + RecoveryNotice);
    }
}

public sealed record ServerSaveStatusDisplay(string Message, string Confirmed, bool Warning, string NotificationKey);
