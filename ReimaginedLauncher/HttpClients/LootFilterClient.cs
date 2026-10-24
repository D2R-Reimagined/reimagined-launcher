using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace ReimaginedLauncher.HttpClients;

public sealed record AccountLootFilter(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record AccountLootFilterDetails(string Id, string Name, JsonElement Document);
internal sealed class LootFilterRateLimitException(DateTimeOffset retryAt) : HttpRequestException("The server limited filter requests. Please wait before trying again.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
public sealed partial class ReimaginedApiHttpClient
{
    internal async Task<IReadOnlyList<AccountLootFilter>> ListLootFiltersAsync(CancellationToken token = default)
    {
        using var json = await GetLootFilterJsonAsync("loot-filters", token);
        var result = new List<AccountLootFilter>();
        foreach (var entry in json.RootElement.GetProperty("items").EnumerateArray())
        {
            result.Add(new AccountLootFilter(entry.GetProperty("id").GetString()!, entry.GetProperty("name").GetString()!));
            if (result.Count > 1000) throw new InvalidDataException("The account filter list is too large.");
        }
        return result;
    }
    internal async Task<AccountLootFilterDetails> GetLootFilterAsync(string id, CancellationToken token = default)
    {
        using var json = await GetLootFilterJsonAsync("loot-filters/" + Uri.EscapeDataString(id), token);
        var root = json.RootElement;
        var returnedId = root.GetProperty("id").GetString();
        if (returnedId != id) throw new InvalidDataException("The downloaded filter does not match the selection.");
        return new AccountLootFilterDetails(id, root.GetProperty("name").GetString()!, root.GetProperty("document").Clone());
    }
    private async Task<JsonDocument> GetLootFilterJsonAsync(string path, CancellationToken token)
    {
        using var timeout = CreateRequestTimeout(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        await AuthorizeAsync(request, timeout.Token);
        if (request.Headers.Authorization is null) throw new InvalidOperationException("Sign in to access your filters.");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new LootFilterRateLimitException(response.Headers.RetryAfter?.Date
                ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1)));
        response.EnsureSuccessStatusCode();
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The filter response is too large.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (data.Length + count > limit) throw new InvalidDataException("The filter response is too large.");
            data.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(data.ToArray());
    }
}
