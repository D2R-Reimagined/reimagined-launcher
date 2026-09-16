using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ReimaginedLauncher.HttpClients;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LadderBundleDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ladder-resume-tests", Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_directory, "archive.zip");
    private static readonly byte[] Payload = Enumerable.Range(0, LadderBundleDownloader.ChunkBytes + 4096).Select(i => (byte)(i % 251)).ToArray();
    private static string Hash => Convert.ToHexString(SHA256.HashData(Payload));

    private Task<byte[]> Download(Handler handler, CancellationToken token = default, string? hash = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/"), Timeout = Timeout.InfiniteTimeSpan };
        return new LadderBundleDownloader(client) { RetryDelay = TimeSpan.Zero, RequestTimeout = TimeSpan.FromMilliseconds(200) }
            .DownloadAsync("bundle/download", Payload.Length, hash ?? Hash, CachePath, null, token);
    }

    [Fact]
    public async Task FailureRetainsBytesAndNewDownloaderResumesWithoutFetchingCompletedChunk()
    {
        var broken = new Handler { BreakFirstChunk = true };
        await Assert.ThrowsAsync<IOException>(() => Download(broken));
        Assert.Equal(4096, new FileInfo(Path.Combine(CachePath + ".parts", "0000.part")).Length);
        var savedSecondChunk = File.Exists(Path.Combine(CachePath + ".parts", "0001.part"))
            && new FileInfo(Path.Combine(CachePath + ".parts", "0001.part")).Length == 4096;
        var recovered = new Handler();
        Assert.Equal(Payload, await Download(recovered));
        Assert.Contains(recovered.Ranges, range => range.Start == 4096);
        if (savedSecondChunk) Assert.DoesNotContain(recovered.Ranges, range => range.Start == LadderBundleDownloader.ChunkBytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(CachePath));
        Assert.Empty(Directory.GetFiles(CachePath + ".parts", "*.part"));
        var offline = new Handler { FailAll = true };
        Assert.Equal(Payload, await Download(offline));
        Assert.Empty(offline.Ranges);
    }

    [Fact]
    public async Task RequestTimeoutRetriesWithoutUserRetry()
    {
        var handler = new Handler { TimeoutOnce = true };
        Assert.Equal(Payload, await Download(handler));
        Assert.True(handler.FirstChunkRequests >= 2);
    }

    [Fact]
    public async Task ExpiredCdnUrlRefreshesThroughApiWithoutRestartingDownload()
    {
        var handler = new Handler { ExpireCdnUrl = true };
        Assert.Equal(Payload, await Download(handler));
        Assert.Contains(handler.Hosts, host => host == "cdn.example.com");
        Assert.True(handler.Hosts.Count(host => host == "api.example.com") > 1);
    }

    [Fact]
    public async Task CancellationRetainsWrittenBytesAndResumesOnNextAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler { CancelAfterRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(handler, cancellation.Token));
        Assert.Equal(1024, new FileInfo(Path.Combine(CachePath + ".parts", "0000.part")).Length);
        var recovered = new Handler();
        Assert.Equal(Payload, await Download(recovered));
        Assert.Contains(recovered.Ranges, range => range.Start == 1024);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidRangeResponsesNeverPromoteAnArchive(bool ignored)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(new Handler { IgnoreChunkRange = ignored, BadContentRange = !ignored }));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task CorruptChunksAreDiscardedBeforeAFreshRetry()
    {
        Directory.CreateDirectory(CachePath + ".parts");
        await File.WriteAllBytesAsync(Path.Combine(CachePath + ".parts", "0000.part"), new byte[LadderBundleDownloader.ChunkBytes]);
        await File.WriteAllBytesAsync(Path.Combine(CachePath + ".parts", "0001.part"), new byte[4096]);
        var offline = new Handler { FailAll = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(offline));
        Assert.Empty(offline.Ranges);
        Assert.Empty(Directory.GetFiles(CachePath + ".parts", "*.part"));
        Assert.Equal(Payload, await Download(new Handler()));
    }

    [Fact]
    public async Task ServerWithoutRangesUsesProbeBodyAndReusesCompletedArchive()
    {
        var handler = new Handler { NoRanges = true };
        Assert.Equal(Payload, await Download(handler));
        Assert.Single(handler.Ranges);
        Assert.Equal(Payload, await Download(new Handler { FailAll = true }));
    }

    [Fact]
    public async Task DescriptorHashAndSizeRemainAuthoritative()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(new Handler(), hash: new string('F', 64)));
        Assert.False(File.Exists(CachePath));
        Assert.Empty(Directory.GetFiles(CachePath + ".parts", "*.part"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(new Handler { BadSize = true }));
    }

    [Fact]
    public async Task ExistingCacheCorruptionAndConcurrentWriterAreHandled()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(CachePath, new byte[Payload.Length]);
        Assert.Equal(Payload, await Download(new Handler()));
        using (File.Open(Path.Combine(CachePath + ".parts", "download.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => Download(new Handler()));
        Assert.Equal(Payload, await Download(new Handler { FailAll = true }));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool BreakFirstChunk, FailAll, TimeoutOnce, IgnoreChunkRange, BadContentRange, NoRanges, BadSize, ExpireCdnUrl;
        public CancellationTokenSource? CancelAfterRead;
        public int FirstChunkRequests;
        public ConcurrentBag<(long Start, long End)> Ranges { get; } = [];
        public ConcurrentBag<string> Hosts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = range.From!.Value;
            var end = range.To!.Value;
            Ranges.Add((start, end));
            Hosts.Add(request.RequestUri!.Host);
            if (FailAll) throw new HttpRequestException("offline");
            var probe = start == 0 && end == 0;
            if (ExpireCdnUrl && request.RequestUri.Host == "cdn.example.com")
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (!probe && start < LadderBundleDownloader.ChunkBytes)
            {
                var attempt = Interlocked.Increment(ref FirstChunkRequests);
                if (TimeoutOnce && attempt == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var full = NoRanges || (!probe && IgnoreChunkRange);
            var contentStart = full ? 0 : (int)start;
            var length = full ? Payload.Length : (int)(end - start + 1);
            Stream stream = new MemoryStream(Payload, contentStart, length, writable: false);
            if (!probe && start < LadderBundleDownloader.ChunkBytes && (BreakFirstChunk || CancelAfterRead is not null))
                stream = new InterruptedStream(stream, CancelAfterRead);
            var response = new HttpResponseMessage(full ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = probe && ExpireCdnUrl ? new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/bundle") : request,
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentLength = length;
            if (!full) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                !probe && BadContentRange ? start + 1 : start,
                !probe && BadContentRange ? end + 1 : end, BadSize ? Payload.Length + 1 : Payload.Length);
            return response;
        }
    }

    private sealed class InterruptedStream(Stream inner, CancellationTokenSource? cancel) : Stream
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_read) throw new IOException("connection dropped");
            _read = true;
            var read = await inner.ReadAsync(buffer[..Math.Min(1024, buffer.Length)], token);
            cancel?.Cancel();
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
