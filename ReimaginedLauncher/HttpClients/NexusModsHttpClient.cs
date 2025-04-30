using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients.Models;

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
            await Console.Error.WriteLineAsync($"Failed to fetch files: {response.StatusCode}");
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<NexusModsFileListResponse>(stream, SerializerOptions.CamelCase);
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
            await Console.Error.WriteLineAsync($"Failed to validate API key: {response.StatusCode}");
            return null;
        }
        
        return await JsonSerializer.DeserializeAsync<NexusModsValidateResponse>(await response.Content.ReadAsStreamAsync(), SerializerOptions.CamelCase);
    }

    private async Task FindAndSetApiKey()
    {
        _httpClient.DefaultRequestHeaders.Remove("apikey");
        _httpClient.DefaultRequestHeaders.Add("apikey", MainWindow.Settings.NexusModsSSOApiKey);
    }
}