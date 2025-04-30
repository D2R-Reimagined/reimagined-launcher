using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

// ReSharper disable once InconsistentNaming
public class NexusModsSSO
{
    private readonly Uri _socketUri = new("wss://sso.nexusmods.com");
    private ClientWebSocket _webSocket;
    private string _uuid;
    private string _token;
    private string _applicationSlug = "d2rrlauncher";
    public event Action<string> OnApiKeyReceived;

    public async Task ConnectAsync()
    {
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(_socketUri, CancellationToken.None);
        Console.WriteLine("Connected to Nexus Mods SSO");

        _uuid = Guid.NewGuid().ToString();
        var request = new
        {
            id = _uuid,
            token = _token,
            protocol = 2
        };

        var json = JsonSerializer.Serialize(request);
        await _webSocket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        var authUrl = $"https://www.nexusmods.com/sso?id={_uuid}&application={_applicationSlug}";
        OpenInBrowser(authUrl);

        _ = ReceiveMessagesAsync();
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error opening browser: " + ex.Message);
        }
    }

    private async Task ReceiveMessagesAsync()
    {
        var buffer = new byte[4096];
        while (_webSocket.State == WebSocketState.Open)
        {
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine("WebSocket closed");
                return;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var response = JsonSerializer.Deserialize<NexusSSOResponse>(message, SerializerOptions.CamelCase);

            if (response?.Success == true)
            {
                if (!string.IsNullOrEmpty(response.Data.ConnectionToken))
                {
                    _token = response.Data.ConnectionToken;
                    Console.WriteLine("Connection token received");
                }
                else if (!string.IsNullOrEmpty(response.Data.ApiKey))
                {
                    Console.WriteLine("API Key Received: " + response.Data.ApiKey);
                    OnApiKeyReceived?.Invoke(response.Data.ApiKey);
                }
            }
            else
            {
                Console.WriteLine("Error: " + response?.Error);
            }
        }
    }
}

public class NexusSSOResponse
{
    public bool Success { get; set; }
    public NexusSSOResponseData Data { get; set; } = new();
    public string Error { get; set; }
}

public class NexusSSOResponseData
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; }
    
    [JsonPropertyName("connection_token")]
    public string ConnectionToken { get; set; }
}