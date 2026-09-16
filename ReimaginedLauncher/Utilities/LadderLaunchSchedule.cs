using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

public sealed class LadderLaunchSchedule(
    IReadOnlyList<LadderResponse> ladders,
    IReadOnlyList<LadderResponse> liveLadders,
    DateTimeOffset serverTime)
{
    private readonly long _receivedAt = Stopwatch.GetTimestamp();
    public DateTimeOffset Now => serverTime + Stopwatch.GetElapsedTime(_receivedAt);
    public IReadOnlyList<LadderResponse> Available => ladders
        .Where(ladder => IsAvailable(ladder, Now)).OrderBy(ladder => ladder.StartDateUtc).ToArray();

    public bool IsLive(LadderResponse? ladder) => ladder is not null
        && liveLadders.Any(live => live.Id == ladder.Id
            && live.StartDateUtc == ladder.StartDateUtc && live.EndDateUtc == ladder.EndDateUtc
            && live.ActiveBundle?.Id == ladder.ActiveBundle?.Id
            && live.ActiveBundle?.Revision == ladder.ActiveBundle?.Revision)
        && ladder.StartDateUtc <= Now && ladder.EndDateUtc > Now;

    public static bool IsAvailable(LadderResponse ladder, DateTimeOffset now) =>
        ladder.StartDateUtc <= now.AddHours(1) && ladder.EndDateUtc > now;

    internal static TimeSpan RefreshInterval(bool ladderMode, bool failed, DateTimeOffset? start, DateTimeOffset now)
    {
        if (!ladderMode) return TimeSpan.FromMinutes(5);
        if (failed) return TimeSpan.FromSeconds(30);
        if (start is null) return TimeSpan.FromMinutes(5);
        var remaining = start.Value - now;
        return remaining.TotalSeconds is > 0 and < 30
            ? TimeSpan.FromSeconds(Math.Max(1, remaining.TotalSeconds)) : TimeSpan.FromSeconds(30);
    }

    public static string Countdown(DateTimeOffset start, DateTimeOffset now)
    {
        var remaining = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((start - now).TotalSeconds)));
        return remaining > TimeSpan.Zero
            ? $"Starts in {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : "Checking live status...";
    }
}
