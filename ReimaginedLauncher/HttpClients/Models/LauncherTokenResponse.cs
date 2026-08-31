using System;

namespace ReimaginedLauncher.HttpClients.Models;

public sealed record LauncherTokenResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshExpiresAtUtc,
    ReimaginedUserResponse User);
