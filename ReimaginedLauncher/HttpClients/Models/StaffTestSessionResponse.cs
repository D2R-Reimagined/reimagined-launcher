using System;

namespace ReimaginedLauncher.HttpClients.Models;

/// <summary>
/// A staff member's local d2rds test (POST staff-test/session): their own hidden
/// ladder, the key their local server signs in with, and the client's launch ticket.
/// </summary>
public sealed record StaffTestSessionResponse(
    Guid LadderId,
    string LadderName,
    string ServerId,
    string ServerKey,
    string ServerAddress,
    string LaunchTicket,
    DateTime ExpiresAtUtc);
