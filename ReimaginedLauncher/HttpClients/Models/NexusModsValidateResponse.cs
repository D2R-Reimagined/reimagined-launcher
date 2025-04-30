using System.Text.Json.Serialization;

namespace ReimaginedLauncher.HttpClients.Models;

public class NexusModsValidateResponse
{
    [JsonPropertyName("message")]
    public string Message { get; set; }
    
    [JsonPropertyName("user_id")]
    public int UserId { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("is_premium?")]
    public bool? IsPremiumQ { get; set; }

    [JsonPropertyName("is_supporter?")]
    public bool? IsSupporterQ { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("profile_url")]
    public string ProfileUrl { get; set; } = string.Empty;
}