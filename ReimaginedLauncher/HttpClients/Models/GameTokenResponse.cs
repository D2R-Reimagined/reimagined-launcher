using System;

namespace ReimaginedLauncher.HttpClients.Models;

/// <summary>
/// A narrow, short-lived token minted for one game session's plugins, as
/// returned by POST auth/launcher/game-token.
/// </summary>
public sealed record GameTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
