using System.Net;
using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class PatchNotesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launcher-patch-notes-" + Guid.NewGuid().ToString("N"));
    private const string Index = "<template slot='contents'><table><tr><th>Version</th></tr><tr><td><a href='/Patch_Notes/3-0-9'>3.0.9</a></td><td>May 5</td><td>Old</td><td>Changes</td></tr><tr><td><a href='/en/Patch_Notes/3-0-12'>3.0.12</a></td><td>September 18</td><td>Title &amp; notes</td><td>Highlights</td></tr></table></template>";
    private const string Article = "<template slot='contents'><h2><a class='toc-anchor' href='#skills'>#</a>Skills</h2><ul><li>Parent<ul><li>Child &amp; detail<br>continued</li></ul></li></ul><p>Go <a href='https://example.com/notes'>here</a><a href='javascript:alert(1)'>bad</a></p><table><tr><th>Name</th><th>Value</th></tr><tr><td>One</td><td>2</td></tr></table><script>bad script</script></template>";
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool Offline;
        public string ArticleHtml = Article;
        public string IndexHtml = Index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Offline) throw new HttpRequestException("Offline fixture");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("Patch_Notes") ? IndexHtml : ArticleHtml) });
        }
    }
    private PatchNotesService Service(Handler handler) => new(new HttpClient(handler), root);
    private static PatchRelease Release => new("3.0.12", "", "", "", "https://wiki.d2r-reimagined.com/en/Patch_Notes/3-0-12");

    [Fact]
    public void IndexSortsVersionsAndNormalizesWikiLinks()
    {
        var releases = PatchNotesParser.ParseIndex(Index);
        Assert.Equal(new[] { "3.0.12", "3.0.9" }, releases.Select(r => r.Version));
        Assert.Equal("Title & notes", releases[0].Title);
        Assert.Contains("/en/Patch_Notes/", releases[1].Url);
    }

    [Theory]
    [InlineData("3.0.6-7", "v3.0.7", true)]
    [InlineData("3.0.6-7", "3.0.8", false)]
    [InlineData("3.0.12", "v3.0.12", true)]
    [InlineData("3.0.12", "Not detected", false)]
    public void VersionMatchingSupportsInstalledAndGroupedVersions(string label, string installed, bool matches) =>
        Assert.Equal(matches, PatchNotesParser.MatchesVersion(label, installed));

    [Fact]
    public void ArticlePreservesNestedListsTablesAndSafeLinks()
    {
        var blocks = PatchNotesParser.ParseArticle(Article).Blocks;
        Assert.Equal("Skills", blocks[0].Text);
        Assert.Equal("Parent", blocks[1].Text);
        Assert.Equal("Child & detail continued", blocks[2].Text);
        Assert.Equal(1, blocks[2].Depth);
        Assert.Single(blocks[3].Inlines, p => p.Url != null);
        Assert.Equal("https://example.com/notes", blocks[3].Inlines.Single(p => p.Url != null).Url);
        Assert.Equal("2", string.Concat(blocks[4].Rows![1][1].Select(p => p.Text)));
        Assert.DoesNotContain(blocks, b => b.Text.Contains("bad script"));
    }

    [Fact]
    public void LinksStayInOrderWithoutRepeatingTheirLabels()
    {
        var article = PatchNotesParser.ParseArticle("<template slot='contents'><ul><li><a href='https://example.com/pictures'>Pictures</a></li><li>See <a href='https://example.com/a'>the <strong>first</strong> change</a>, then <a href='https://example.com/b'>the second</a>.</li></ul></template>");
        var pictures = article.Blocks[0].Inlines;
        Assert.Single(pictures);
        Assert.Equal("Pictures", pictures[0].Text);
        Assert.Equal("https://example.com/pictures", pictures[0].Url);
        var mixed = article.Blocks[1].Inlines;
        Assert.Equal("See the first change, then the second.", string.Concat(mixed.Select(p => p.Text)));
        Assert.Equal(new[] { "the first change", "the second" }, mixed.Where(p => p.Url != null).Select(p => p.Text));
    }

    [Fact]
    public void RepeatedLinkLabelsKeepTheirOwnDestinationsAndTablePosition()
    {
        var article = PatchNotesParser.ParseArticle("<template slot='contents'><table><tr><td>Read <a href='https://example.com/a'>notes</a> or <a href='https://example.com/b'>notes</a>.</td></tr></table></template>");
        var cell = article.Blocks[0].Rows![0][0];
        Assert.Equal("Read notes or notes.", string.Concat(cell.Select(p => p.Text)));
        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b" }, cell.Where(p => p.Url != null).Select(p => p.Url));
    }

    [Theory]
    [InlineData("https://example.com/en/Patch_Notes/3-0-12")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://wiki.d2r-reimagined.com/admin")]
    public async Task OnlyWikiPatchPagesCanBeFetched(string url)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(handler).GetArticleAsync(Release with { Url = url }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CacheSurvivesRestartAndOfflineRefresh()
    {
        var handler = new Handler();
        var service = Service(handler);
        await service.GetIndexAsync();
        var original = await service.GetArticleAsync(Release);
        Assert.Equal(2, handler.Calls);
        handler.Offline = true;
        var restarted = Service(handler);
        var index = await restarted.GetIndexAsync();
        var saved = await restarted.GetArticleAsync(Release);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(original.Value.Blocks.Count, saved.Value.Blocks.Count);
        Assert.Equal(2, index.Value.Count);
        var fallback = await restarted.GetArticleAsync(Release, refresh: true);
        Assert.Contains("Could not refresh", fallback.Status);
        Assert.Equal(original.SavedAt, fallback.SavedAt);
    }

    [Fact]
    public async Task MalformedWikiResponsePreservesLastGoodCache()
    {
        var handler = new Handler();
        var service = Service(handler);
        var original = await service.GetArticleAsync(Release);
        var disk = File.ReadAllText(Directory.GetFiles(root).Single());
        handler.ArticleHtml = "<html>maintenance</html>";
        var fallback = await service.GetArticleAsync(Release, true);
        Assert.Contains("saved copy", fallback.Status);
        Assert.Equal(original.SavedAt, fallback.SavedAt);
        Assert.Equal(disk, File.ReadAllText(Directory.GetFiles(root).Single()));
    }

    [Fact]
    public async Task UncachedArticleOfflineHasClearError()
    {
        await Assert.ThrowsAsync<IOException>(() => Service(new Handler { Offline = true }).GetArticleAsync(Release));
    }

    [Fact]
    public async Task CacheWriteFailureStillReturnsLiveNotes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "not a directory");
        var result = await Service(new Handler()).GetArticleAsync(Release);
        Assert.NotEmpty(result.Value.Blocks);
        Assert.Contains("could not save", result.Status);
    }

    [Fact]
    public async Task CorruptCacheIsReplacedFromNetwork()
    {
        var handler = new Handler();
        await Service(handler).GetIndexAsync();
        File.WriteAllText(Directory.GetFiles(root).Single(), "{broken");
        var result = await Service(handler).GetIndexAsync();
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task OldCacheAttemptsNetworkThenFallsBack()
    {
        var handler = new Handler();
        await Service(handler).GetIndexAsync();
        var path = Directory.GetFiles(root).Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["SavedAt"] = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(path, json.ToJsonString());
        handler.Offline = true;
        var result = await Service(handler).GetIndexAsync();
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Could not refresh", result.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        else if (File.Exists(root)) File.Delete(root);
    }
}
