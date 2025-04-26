using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

// ReSharper disable once InconsistentNaming
public class NexusModsSSO
{
    private readonly Uri _socketUri = new("wss://sso.nexusmods.com");
    private ClientWebSocket _webSocket;
    private string _uuid;
    private string _token;
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

        var authUrl = $"https://www.nexusmods.com/sso?id={_uuid}&application=NOT-REAL-SLUG";
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
            var response = JsonSerializer.Deserialize<NexusSSOResponse>(message);

            if (response?.Success == true)
            {
                if (response.Data.TryGetValue("connection_token", out var connectionToken))
                {
                    _token = connectionToken?.ToString();
                    Console.WriteLine("Connection token received");
                }
                else if (response.Data.TryGetValue("api_key", out var apiKey))
                {
                    Console.WriteLine("API Key Received: " + apiKey);
                    OnApiKeyReceived?.Invoke(apiKey.ToString());
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
    public Dictionary<string, object> Data { get; set; } = new();
    public string Error { get; set; }
}