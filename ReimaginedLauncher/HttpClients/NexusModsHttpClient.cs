using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public class NexusModsHttpClient
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.nexusmods.com/v1";
    private const string ApiKey = "YOUR_API_KEY";

    public NexusModsHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("apikey", ApiKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<NexusModsFileListResponse?> GetModFilesAsync(string gameName, int modId)
    {
        var url = $"{BaseUrl}/games/{gameName}/mods/{modId}/files.json";
        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync($"Failed to fetch files: {response.StatusCode}");
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<NexusModsFileListResponse>(stream, SerializerOptions.PropertyNameCaseInsensitive);
    }
}