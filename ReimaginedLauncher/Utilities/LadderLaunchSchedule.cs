using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

/// <param name="previewLadders">
/// Ladders the API lets this admin play before they start. Nothing played on
/// one is kept: the API answers its saves without storing them.
/// </param>
public sealed class LadderLaunchSchedule(
    IReadOnlyList<LadderResponse> ladders,
    IReadOnlyList<LadderResponse> liveLadders,
    DateTimeOffset serverTime,
    IReadOnlyList<LadderResponse>? previewLadders = null)
{
    public static readonly TimeSpan PreloadWindow = TimeSpan.FromHours(2);
    private readonly long _receivedAt = Stopwatch.GetTimestamp();
    public DateTimeOffset Now => serverTime + Stopwatch.GetElapsedTime(_receivedAt);
    public IReadOnlyList<LadderResponse> Available => ladders
        .Where(ladder => IsAvailable(ladder, Now) || IsPreview(ladder)).OrderBy(ladder => ladder.StartDateUtc).ToArray();

    public bool IsLive(LadderResponse? ladder) => ladder is not null
        && liveLadders.Any(live => Matches(live, ladder))
        && ladder.StartDateUtc <= Now && ladder.EndDateUtc > Now;

    /// <summary>An admin preview: playable now, before its start, with nothing saved.</summary>
    public bool IsPreview(LadderResponse? ladder) => ladder is not null
        && previewLadders?.Any(preview => Matches(preview, ladder)) == true
        && ladder.StartDateUtc > Now && ladder.EndDateUtc > Now;

    public bool CanPlay(LadderResponse? ladder) => IsLive(ladder) || IsPreview(ladder);

    private static bool Matches(LadderResponse listed, LadderResponse ladder) => listed.Id == ladder.Id
        && listed.StartDateUtc == ladder.StartDateUtc && listed.EndDateUtc == ladder.EndDateUtc
        && listed.ActiveBundle?.Id == ladder.ActiveBundle?.Id
        && listed.ActiveBundle?.Revision == ladder.ActiveBundle?.Revision;

    public static bool IsAvailable(LadderResponse ladder, DateTimeOffset now) =>
        ladder.StartDateUtc <= now + PreloadWindow && ladder.EndDateUtc > now;

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
