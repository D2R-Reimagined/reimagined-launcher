using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients;

public class NexusModsHttpClient : INexusModsHttpClient
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.nexusmods.com/v1";

    public NexusModsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("apikey", MainWindow.Settings.NexusModsSSOApiKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<NexusModsFileListResponse?> GetModFilesAsync(string gameName, int modId)
    {
        await FindAndSetApiKey();
        var url = $"{BaseUrl}/games/{gameName}/mods/{modId}/files.json";
        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Notifications.SendNotification($"Failed to fetch files: {response.StatusCode}");
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<NexusModsFileListResponse>(stream, SerializerOptions.PropertyNameCaseInsensitive);
    }

    public async Task<(NexusModsDownloadLinkResponse? Link, HttpStatusCode StatusCode)> GenerateDownloadLink(
        string gameName,
        int modid,
        int fileId,
        string? key = null,
        long? expires = null)
    {
        await FindAndSetApiKey();
        var url = $"{BaseUrl}/games/{gameName}/mods/{modid}/files/{fileId}/download_link.json";
        if (!string.IsNullOrWhiteSpace(key) && expires.HasValue)
        {
            url += $"?key={System.Uri.EscapeDataString(key)}&expires={expires.Value}";
        }

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return (null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync();
        var downloadLinks = await JsonSerializer.DeserializeAsync<List<NexusModsDownloadLinkResponse>>(stream, SerializerOptions.PropertyNameCaseInsensitive);
        return (downloadLinks?.FirstOrDefault(link => !string.IsNullOrWhiteSpace(link.Uri)), response.StatusCode);
    }

    public async Task<NexusModsValidateResponse?> ValidateApiKeyAsync(string? apiKey = "")
    {
        await FindAndSetApiKey();
        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove("apikey");
            _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
        }

        var url = $"{BaseUrl}/users/validate.json";
        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Notifications.SendNotification($"Failed to validate API key: {response.StatusCode}");
            return null;
        }

        return await JsonSerializer.DeserializeAsync<NexusModsValidateResponse>(
            await response.Content.ReadAsStreamAsync(),
            SerializerOptions.PropertyNameCaseInsensitive);
    }

    private Task FindAndSetApiKey()
    {
        _httpClient.DefaultRequestHeaders.Remove("apikey");
        _httpClient.DefaultRequestHeaders.Add("apikey", MainWindow.Settings.NexusModsSSOApiKey);
        return Task.CompletedTask;
    }
}
