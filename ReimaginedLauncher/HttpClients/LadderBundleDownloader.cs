using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.HttpClients;

internal sealed class LadderBundleDownloader(HttpClient client)
{
    internal const int ChunkBytes = 8 * 1024 * 1024;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    private const int Attempts = 4;

    internal async Task<byte[]> DownloadAsync(string downloadPath, long size, string sha256, string cachePath,
        IProgress<LadderBundleDownloadProgress>? progress, CancellationToken token)
    {
        if (size is <= 0 or > 512L * 1024 * 1024 || sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The ladder bundle has an invalid download size or hash.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var partsDirectory = cachePath + ".parts";
        Directory.CreateDirectory(partsDirectory);
        // The lock covers both partial files and atomic promotion to the archive.
        await using var cacheLock = new FileStream(Path.Combine(partsDirectory, "download.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length == size)
        {
            var cached = await File.ReadAllBytesAsync(cachePath, token);
            if (Matches(cached, sha256)) return cached;
        }

        var watch = Stopwatch.StartNew();
        long received = 0;
        long resumed = 0;
        void Report()
        {
            var bytes = Interlocked.Read(ref received);
            var speed = Math.Max(0, bytes - resumed) / Math.Max(.001, watch.Elapsed.TotalSeconds);
            progress?.Report(new(bytes, size, Math.Clamp(bytes * 100d / size, 0, 100), speed,
                speed > 0 && bytes < size ? TimeSpan.FromSeconds((size - bytes) / speed) : null));
        }

        var source = new Uri(client.BaseAddress!, downloadPath.TrimStart('/'));
        var paths = new List<string>();
        var wholePath = Path.Combine(partsDirectory, "whole.part");
        var chunkCount = (int)((size + ChunkBytes - 1) / ChunkBytes);
        for (var i = 0; i < chunkCount; i++) paths.Add(Path.Combine(partsDirectory, $"{i:D4}.part"));
        var hasChunks = paths.Select((path, i) => File.Exists(path)
            && new FileInfo(path).Length == Math.Min(ChunkBytes, size - (long)i * ChunkBytes)).All(complete => complete);
        var hasWhole = File.Exists(wholePath) && new FileInfo(wholePath).Length == size;
        List<string> assemblyPaths;
        if (hasChunks) assemblyPaths = paths;
        else if (hasWhole) assemblyPaths = [wholePath];
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, source);
            probeRequest.Headers.AcceptEncoding.ParseAdd("identity, gzip;q=0, deflate;q=0, br;q=0");
            probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            using var probe = await client.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            probe.EnsureSuccessStatusCode();
            if (probe.StatusCode == HttpStatusCode.PartialContent)
            {
                ValidateRange(probe, 0, 0, size);
                var effective = probe.RequestMessage?.RequestUri ?? source;
                probe.Dispose();
                assemblyPaths = paths;
                for (var i = 0; i < paths.Count; i++)
                {
                    var length = Math.Min(ChunkBytes, size - (long)i * ChunkBytes);
                    if (File.Exists(paths[i]) && new FileInfo(paths[i]).Length > length) File.Delete(paths[i]);
                    if (File.Exists(paths[i])) received += new FileInfo(paths[i]).Length;
                }
                resumed = received;
                Report();
                using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var transfer = Parallel.ForEachAsync(Enumerable.Range(0, paths.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = transferCancellation.Token },
                    async (i, cancellation) =>
                    {
                        var start = (long)i * ChunkBytes;
                        var end = Math.Min(size, start + ChunkBytes) - 1;
                        await DownloadPartAsync(effective, source, paths[i], start, end, size,
                            count => Interlocked.Add(ref received, count), cancellation);
                    });
                try
                {
                    while (!transfer.IsCompleted)
                    {
                        await Task.WhenAny(transfer, Task.Delay(250));
                        Report();
                    }
                    await transfer;
                }
                finally
                {
                    if (!transfer.IsCompleted)
                    {
                        await transferCancellation.CancelAsync();
                        try { await transfer; } catch { /* Preserve the progress callback failure. */ }
                    }
                }
            }
            else if (probe.StatusCode == HttpStatusCode.OK)
            {
                if (probe.Content.Headers.ContentLength is { } length && length != size)
                    throw new InvalidDataException("The ladder bundle changed size on the server.");
                assemblyPaths = [wholePath];
                // A server ignoring Range cannot resume; consume this response instead of probing twice.
                await using var output = new FileStream(wholePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var input = await probe.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (output.Length + read > size) throw new InvalidDataException("The ladder bundle exceeded its approved size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    received += read;
                    Report();
                }
                if (output.Length != size) throw new EndOfStreamException("The ladder bundle download was incomplete.");
            }
            else throw new InvalidDataException("The server returned an invalid bundle download response.");
        }

        token.ThrowIfCancellationRequested();
        var payload = new byte[size];
        var position = 0;
        foreach (var path in assemblyPaths)
        {
            await using var part = File.OpenRead(path);
            await part.ReadExactlyAsync(payload.AsMemory(position, checked((int)part.Length)), token);
            position += (int)part.Length;
        }
        if (position != size || !Matches(payload, sha256))
        {
            foreach (var path in paths.Append(wholePath)) File.Delete(path);
            throw new InvalidDataException("The downloaded ladder bundle failed its archive SHA-256 check. Retry to download a fresh copy.");
        }

        var temporary = cachePath + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, payload, token);
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
        foreach (var path in paths.Append(wholePath)) File.Delete(path);
        received = size;
        Report();
        return payload;
    }

    private async Task DownloadPartAsync(Uri source, Uri refreshSource, string path, long start, long end, long total,
        Action<int> report, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        output.Position = output.Length;
        var buffer = new byte[81920];
        for (var attempt = 1; start + output.Length <= end; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(RequestTimeout);
                var offset = start + output.Length;
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.AcceptEncoding.ParseAdd("identity, gzip;q=0, deflate;q=0, br;q=0");
                request.Headers.Range = new RangeHeaderValue(offset, end);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    source = refreshSource;
                response.EnsureSuccessStatusCode();
                ValidateRange(response, offset, end, total);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                while (start + output.Length <= end)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - start - output.Length + 1)), timeout.Token);
                    if (read == 0) throw new EndOfStreamException("The ladder bundle chunk download was interrupted.");
                    // Finish writing received bytes even if the request is canceled concurrently.
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    report(read);
                }
                await output.FlushAsync(CancellationToken.None);
            }
            catch (Exception exception) when (attempt < Attempts && !token.IsCancellationRequested
                && exception is not InvalidDataException
                && (exception is HttpRequestException or IOException or OperationCanceledException))
            {
                await output.FlushAsync(CancellationToken.None);
                await Task.Delay(RetryDelay * attempt, token);
            }
        }
        token.ThrowIfCancellationRequested();
    }

    private static bool Matches(byte[] payload, string hash) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(payload)), hash, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRange(HttpResponseMessage response, long start, long end, long total)
    {
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
            || range.From != start || range.To != end || range.Length != total
            || (response.Content.Headers.ContentLength is { } length && length != end - start + 1))
            throw new InvalidDataException("The server returned an invalid ladder bundle byte range.");
    }
}
