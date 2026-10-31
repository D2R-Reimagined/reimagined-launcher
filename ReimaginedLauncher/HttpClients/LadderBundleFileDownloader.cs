using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.HttpClients;

/// <summary>
/// One file of a ladder bundle, fetched on its own: gzip bytes named by the
/// SHA-256 of what they inflate to. Nothing reaches <c>destination</c> unless it
/// inflates to exactly the size and hash the signed manifest lists.
/// </summary>
internal sealed class LadderBundleFileDownloader(HttpClient client, string? accessToken = null)
{
    internal const long MaxManifestBytes = 8L * 1024 * 1024;
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const int Attempts = 4;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal async Task DownloadAsync(string url, string sha256, long size, string destination,
        Action<long>? progress, CancellationToken token)
    {
        if (size is < 0 or > MaxFileBytes || sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The ladder bundle file has an invalid size or hash.");
        var source = Uri.TryCreate(url, UriKind.Absolute, out var absolute)
                     && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? absolute
            : new Uri(client.BaseAddress!, url.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        for (var attempt = 1; ; attempt++)
        {
            long reported = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                // The token is for the API only, never for the storage a link points at.
                if (accessToken is not null && client.BaseAddress is { } api
                    && source.Scheme == api.Scheme && source.Host == api.Host && source.Port == api.Port)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var inflated = new GZipStream(body, CompressionMode.Decompress))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await inflated.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        if (output.Length + read > size)
                            throw new InvalidDataException("A ladder bundle file exceeded its signed size.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                        reported += read;
                        progress?.Invoke(read);
                    }

                    if (output.Length != size
                        || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A downloaded ladder bundle file failed its signed SHA-256 check.");
                }

                File.Move(partial, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < Attempts && !token.IsCancellationRequested
                && exception is not InvalidDataException
                && exception is HttpRequestException or IOException or OperationCanceledException)
            {
                progress?.Invoke(-reported);
                await Task.Delay(RetryDelay * attempt, token);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }
    }
}
