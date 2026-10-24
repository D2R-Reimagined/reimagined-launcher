using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record PatchNotesResult<T>(T Value, DateTimeOffset SavedAt, string Status);

public sealed class PatchNotesService
{
    private sealed record CachedPage(string Url, string Html, DateTimeOffset SavedAt);
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, CachedPage> _memory = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaxBytes = 4 * 1024 * 1024;

    public PatchNotesService(HttpClient client) : this(client, Path.Combine(SettingsManager.AppDirectoryPath, "cache", "patch-notes")) { }
    internal PatchNotesService(HttpClient client, string cacheDirectory)
    {
        _client = client;
        _cacheDirectory = cacheDirectory;
    }

    public Task<PatchNotesResult<List<PatchRelease>>> GetIndexAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        GetAsync(PatchNotesParser.IndexUrl, PatchNotesParser.ParseIndex, refresh, cancellationToken);

    public Task<PatchNotesResult<PatchArticle>> GetArticleAsync(PatchRelease release, bool refresh = false, CancellationToken cancellationToken = default) =>
        GetAsync(release.Url, PatchNotesParser.ParseArticle, refresh, cancellationToken);

    private async Task<PatchNotesResult<T>> GetAsync<T>(string url, Func<string, T> parse, bool refresh, CancellationToken cancellationToken)
    {
        if (!PatchNotesParser.IsWikiPage(url)) throw new InvalidDataException("This release is available on its source website.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
            _memory.TryGetValue(url, out var cached);
            if (cached == null)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes * 3)
                    {
                        var candidate = JsonSerializer.Deserialize<CachedPage>(await File.ReadAllTextAsync(path, cancellationToken));
                        if (candidate?.Url == url && candidate.Html.Length <= MaxBytes)
                        {
                            _ = parse(candidate.Html);
                            cached = candidate;
                            _memory[url] = cached;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or NullReferenceException) { }
            }
            if (!refresh && cached != null && DateTimeOffset.UtcNow - cached.SavedAt is var age && age >= TimeSpan.Zero && age < TimeSpan.FromHours(6))
                return new(parse(cached.Html), cached.SavedAt, "Saved copy");

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (!PatchNotesParser.IsWikiPage(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url)) throw new InvalidDataException("Unexpected wiki redirect.");
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Wiki page is too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (bytes.Length + read > MaxBytes) throw new InvalidDataException("Wiki page is too large.");
                    bytes.Write(buffer, 0, read);
                }
                var html = Encoding.UTF8.GetString(bytes.ToArray());
                var value = parse(html);
                var page = new CachedPage(url, html, DateTimeOffset.UtcNow);
                var status = "Updated";
                _memory[url] = page;
                try
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    var temporary = path + ".tmp";
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(page), cancellationToken);
                    File.Move(temporary, path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    status = "Updated · could not save for offline reading";
                }
                return new(value, page.SavedAt, status);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cached != null) return new(parse(cached.Html), cached.SavedAt, "Could not refresh · showing saved copy");
                throw new IOException("Patch notes are unavailable. Connect to the internet and try Refresh; opened articles are saved for offline reading.", ex);
            }
        }
        finally { _gate.Release(); }
    }
}
