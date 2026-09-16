using System;
using System.Collections.Generic;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed record LadderScheduleResponse(DateTimeOffset ServerTimeUtc, IReadOnlyList<LadderScheduleEntry> Ladders);
public sealed record LadderScheduleEntry(Guid Id, string Name, DateTimeOffset StartDateUtc, DateTimeOffset EndDateUtc,
    string PolicyVersion, bool IsLive);
