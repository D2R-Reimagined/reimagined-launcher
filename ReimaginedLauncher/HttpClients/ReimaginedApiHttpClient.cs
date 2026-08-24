using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.HttpClients;

public sealed class ReimaginedApiHttpClient
{
    private const string ApiBaseAddressEnvironmentVariable = "D2R_REIMAGINED_API_BASE_URL";
#if DEBUG
    private static readonly Uri DefaultApiBaseAddress = new("http://localhost:5000/");
#else
    private static readonly Uri DefaultApiBaseAddress = new("https://api.d2r-reimagined.com/");
#endif
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _httpClient;

    public ReimaginedApiHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = ResolveApiBaseAddress();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ReimaginedLauncher/1.0");
        LaunchDiagnostics.Log($"Reimagined API base address: {_httpClient.BaseAddress}");
    }

    public async Task<IReadOnlyList<LadderResponse>> GetActiveLaddersAsync(
        CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<List<LadderResponse>>(
            "ladders/active",
            JsonOptions,
            cancellationToken) ?? [];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static Uri ResolveApiBaseAddress()
    {
        var configuredAddress = Environment.GetEnvironmentVariable(ApiBaseAddressEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            return DefaultApiBaseAddress;
        }

        configuredAddress = configuredAddress.Trim().TrimEnd('/') + "/";
        if (Uri.TryCreate(configuredAddress, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            return address;
        }

        LaunchDiagnostics.Log(
            $"Ignoring invalid {ApiBaseAddressEnvironmentVariable} value and using {DefaultApiBaseAddress}.");
        return DefaultApiBaseAddress;
    }
}
