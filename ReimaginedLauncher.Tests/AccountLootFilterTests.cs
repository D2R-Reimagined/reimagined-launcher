using System.Net;
using System.Net.Http;
using System.Text.Json;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class AccountLootFilterTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("account-token", request.Headers.Authorization?.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(handle(request));
        }
    }
    private static ReimaginedApiHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handle) =>
        new(new HttpClient(new Handler(handle))) { AccessTokenProvider = _ => Task.FromResult<string?>("account-token") };
    [Fact]
    public async Task ListIsCachedAndFiveRequestsPerRollingMinuteAreEnforced()
    {
        var clock = new Clock(); int calls = 0;
        var client = Client(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[{\"id\":\"one\",\"name\":\"Starter\"}]}") }; });
        var service = new AccountLootFilterService(client, clock); var user = Guid.NewGuid();
        service.UseAccount(user);
        Assert.Single(await service.ListAsync(user, false, default));
        service.UseAccount(user);
        await service.ListAsync(user, false, default); Assert.Equal(1, calls);
        for (int i = 0; i < 4; i++) await service.ListAsync(user, true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(user, true, default));
        Assert.Equal(5, calls); clock.Now += TimeSpan.FromSeconds(61);
        await service.ListAsync(user, true, default); Assert.Equal(6, calls);
        await service.ListAsync(Guid.NewGuid(), false, default); Assert.Equal(7, calls);
    }
    [Fact]
    public async Task ServerRetryAfterOverridesLocalRefreshAllowance()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(3);
        var service = new AccountLootFilterService(Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(until);
            return response;
        }));
        var user = Guid.NewGuid();
        await Assert.ThrowsAsync<LootFilterRateLimitException>(() => service.ListAsync(user, true, default));
        Assert.True(service.Wait(user) > TimeSpan.FromMinutes(2));
    }
    [Fact]
    public async Task DetailsReturnOnlyDocumentAndWrongIdIsRefused()
    {
        var client = Client(request => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"one\",\"name\":\"Strict\",\"document\":{\"version\":3,\"rules\":[]}}") });
        var details = await client.GetLootFilterAsync("one");
        Assert.Equal("Strict", details.Name);
        Assert.Equal("{\"version\":3,\"rules\":[]}", details.Document.GetRawText());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLootFilterAsync("two"));
    }
    [Theory]
    [InlineData("{\"version\":2,\"rules\":[]}")]
    [InlineData("{\"version\":3,\"version\":3,\"rules\":[]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{},\"hide\":{}}]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"conditions\":{\"itemLevel\":{\"gte\":100}}}}]}")]
    public void InvalidDocumentNeverChangesExistingFilter(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-test-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config")); var path = Path.Combine(root, "config", "filter.json");
            File.WriteAllText(path, "previous valid filter"); using var doc = JsonDocument.Parse(json);
            Assert.Throws<InvalidDataException>(() => AccountLootFilterService.Install(root, doc.RootElement, () => { }));
            Assert.Equal("previous valid filter", File.ReadAllText(path));
            Assert.False(Directory.Exists(LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true); }
    }
    [Fact]
    public void FilterInstallKeepsThreeBackupsAndExportsNoMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-test-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config")); var path = Path.Combine(root, "config", "filter.json");
            File.WriteAllText(path, "original custom filter");
            for (int i = 0; i < 5; i++)
            {
                using var doc = JsonDocument.Parse("{\"version\":3,\"rules\":[{\"show\":{\"ruleName\":\"" + i + "\"}}]}");
                AccountLootFilterService.Install(root, doc.RootElement, () => { });
                Assert.Null(AccountLootFilterService.TrimBackups(root));
            }
            Assert.Equal(3, Directory.GetFiles(LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups"), "filter.json", SearchOption.AllDirectories).Length);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(new[] { "version", "rules" }, saved.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("4", saved.RootElement.GetProperty("rules")[0].GetProperty("show").GetProperty("ruleName").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true); }
    }
}
