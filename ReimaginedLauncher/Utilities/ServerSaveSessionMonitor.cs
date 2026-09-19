using System;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

internal static class ServerSaveSessionMonitor
{
    internal static async Task WaitForInactiveAsync(
        Func<Task<ServerSaveStatus?>> readStatus,
        Func<DateTimeOffset> now,
        Func<Task> delay)
    {
        var lastSeen = now();
        while (true)
        {
            var status = await readStatus();
            if (status is { Running: false }) return;
            if (status is not null) lastSeen = status.UpdatedAtUtc;
            if (now() - lastSeen > TimeSpan.FromSeconds(45)) return;
            await delay();
        }
    }

    internal static ServerSaveStatusDisplay DescribeAfterWatch(
        ServerSaveStatus? status, DateTimeOffset now, bool gameExitConfirmed)
    {
        if (gameExitConfirmed || status is { Running: false })
            return status?.Describe(now, sessionEnded: true)
                   ?? new ServerSaveStatusDisplay(
                       "The game closed without a confirmed save status. Check your server save before continuing.",
                       "No final save acknowledgement is available.", true, "status_missing");

        var display = status?.Describe(now, sessionEnded: false);
        if (status is { State: "stopped" } && display is not null) return display;
        return new ServerSaveStatusDisplay(
            "The launcher lost track of the game session. Recent progress is not confirmed saved.",
            display?.Confirmed ?? "No save confirmation is available.",
            true, "session_tracking_lost");
    }
}
