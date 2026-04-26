using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ReimaginedLauncher.HttpClients;

/// <summary>
/// Shared GET helper for the Cloudflare Worker proxy. Honours the
/// Retry-After header on a single 429/503 response (up to <see cref="MaxRetryAfter"/>)
/// so transient rate-limits don't surface as immediate failures.
/// </summary>
internal static class ProxyHttpHelper
{
    // Maximum time we will voluntarily wait for a 429/503 Retry-After before
    // giving up; anything longer is reported as a failure to the caller.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public static async Task<HttpResponseMessage> GetWithRateLimitAsync(HttpClient client, string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if ((int)response.StatusCode == 429 ||
                (response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                 response.Headers.RetryAfter != null))
            {
                var delay = GetRetryAfterDelay(response);
                var statusCode = (int)response.StatusCode;
                response.Dispose();

                if (attempt == 0 && delay > TimeSpan.Zero && delay <= MaxRetryAfter)
                {
                    await Task.Delay(delay);
                    continue;
                }

                throw new HttpRequestException(
                    $"Proxy rate-limited request to {url} (status {statusCode}, Retry-After {delay.TotalSeconds:F0}s).");
            }

            return response;
        }

        // Unreachable: the loop always returns or throws.
        throw new HttpRequestException($"Failed to fetch {url} after retry.");
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return TimeSpan.FromSeconds(5);
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(5);
    }
}
