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
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"conditions\":{\"rarity\":\"cursed\"}}}]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"conditions\":{\"tier\":\"mythic\"}}}]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"conditions\":{\"tier\":[\"normal\",\"exceptional\",\"elite\",\"normal\"]}}}]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"tooltip\":{\"outlineColor\":\"RGBA(255,255,255,1)\"}}}]}")]
    [InlineData("{\"version\":3,\"rules\":[{\"show\":{\"minimapIcon\":{\"shape\":\"pentagon\",\"borderColor\":\"RGBA(255,255,255,1)\",\"fillColor\":\"RGBA(255,0,0,1)\"}}}]}")]
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
    public void ValidationAcceptsWebsiteStrictPresetEquipmentTiersTooltipBordersAndAllMinimapShapes()
    {
        var strict = JsonSerializer.Deserialize<JsonElement>("""{"version":3,"rules":[{"show":{"ruleName":"Drop-only unique and set bases","conditions":{"rarity":["unique","set"],"code":["cm1","cjw","7gd","7fb","7gs","7wa","obb","pae","paf","ned","nee","bae","uul","uth","utp","upl","uhn","urs","aqv","cqv","ame","amf","amb","drb","drf","7ar","7cs","7wb","hgl","ghm","ful","hbt","hbl"]},"tooltip":{"backgroundColor":"RGBA(28, 12, 44, 0.92)","borderColor":"RGBA(210, 120, 255, 1)","textColor":"RGBA(240, 200, 255, 1)"},"minimapIcon":{"shape":"star","size":24,"borderColor":"RGBA(255, 255, 255, 1)","fillColor":"RGBA(210, 120, 255, 1)"},"dropSound":"Filter01"}},{"show":{"ruleName":"Uniques and sets","conditions":{"rarity":["unique","set"]},"tooltip":{"borderColor":"RGBA(215, 175, 85, 1)"},"minimapIcon":{"shape":"diamond","size":20,"borderColor":"RGBA(255, 255, 255, 1)","fillColor":"RGBA(215, 175, 85, 1)"},"dropSound":"Filter01"}},{"show":{"ruleName":"Power orbs and Gem Clusters","conditions":{"code":["ka3","1ka","oos","1os","ooc","1oc","ooa","1oa","ooi","1oi","ooe","1oe","oor","1or","1gc"]},"tooltip":{"backgroundColor":"RGBA(15, 28, 48, 0.92)","borderColor":"RGBA(110, 200, 255, 1)","textColor":"RGBA(185, 230, 255, 1)"},"minimapIcon":{"shape":"square","size":20,"borderColor":"RGBA(255, 255, 255, 1)","fillColor":"RGBA(110, 200, 255, 1)"},"dropSound":"Filter03"}},{"show":{"ruleName":"Rare and crafted equipment","conditions":{"rarity":["rare","crafted"],"tier":["normal","exceptional","elite"]}}},{"hide":{"ruleName":"Hide ordinary equipment through magic","conditions":{"itemType":["weap","armo"],"tier":"normal","rarity":["inferior","normal","superior","magic"],"sockets":{"eq":0},"ethereal":false}}}]}""");
        var stored = LootFilterValidation.Validate(strict);
        Assert.Contains("borderColor", stored);
        foreach (var shape in new[] { "circle", "square", "diamond", "triangle", "triangleDown", "star", "spark", "kite", "hexagon", "cross", "chevron", "bullseye" })
            LootFilterValidation.Validate(JsonSerializer.SerializeToElement(new { version = 3, rules = new[] { new { show = new { minimapIcon = new { shape, borderColor = "RGBA(255,255,255,1)", fillColor = "RGBA(0,0,0,0.5)" } } } } }));
        foreach (var rarity in new[] { "inferior", "normal", "superior", "magic", "set", "rare", "unique", "crafted" })
            LootFilterValidation.Validate(JsonSerializer.SerializeToElement(new { version = 3, rules = new[] { new { show = new { conditions = new { rarity } } } } }));
        foreach (var tier in new[] { "normal", "exceptional", "elite" })
            LootFilterValidation.Validate(JsonSerializer.SerializeToElement(new { version = 3, rules = new[] { new { show = new { conditions = new { tier } } } } }));
        foreach (var tooltip in new object[]
        {
            new { backgroundColor = "RGBA(0, 0, 0, 0.85)" },
            new { borderColor = "RGBA(225, 205, 255, 1)" },
            new { textColor = "RGBA(255, 128, 0, 1)" },
            new { backgroundColor = "#1C0C2CEB", borderColor = "#D278FF", textColor = "#F0C8FFFF" },
            new { backgroundColor = "RGBA(0, 0, 0, 0.85)", borderColor = "RGBA(225, 205, 255, 1)", textColor = "RGBA(255, 128, 0, 1)" }
        })
        {
            LootFilterValidation.Validate(JsonSerializer.SerializeToElement(new { version = 3, rules = new[] { new { show = new { tooltip } } } }));
        }
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
    [Theory]
    [InlineData("pyre-lootfilter.dll", true)]
    [InlineData("PYRE-LOOTFILTER.DLL", true)]
    [InlineData("d2rl-pyre-lootfilter.dll", true)]
    [InlineData("unhoarder.dll", true)]
    [InlineData("UnHoarder.dll", true)]
    [InlineData("other-plugin.dll", false)]
    public void SupportedFilterPluginsIncludePyreLootFilterAndUnhoarder(string fileName, bool expected)
        => Assert.Equal(expected, AccountLootFilterService.IsSupportedFilterPlugin(fileName));
    [Theory]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, true)]
    [InlineData("unhoarder.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, true)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Ladder, true, false, true)]
    [InlineData("unhoarder.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Ladder, true, false, true)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Online, true, true, true)]
    [InlineData("unhoarder.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Online, true, true, true)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Online, true, false, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Ladder, true, true, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, false, true, false)]
    [InlineData("other-plugin.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, false)]
    public void FilterPluginAvailabilityAcceptsPyreLootFilterOrUnhoarderPerModeScope(
        string fileName,
        D2RLoaderExtensionScope scope,
        LaunchExperience experience,
        bool isEnabled,
        bool allowGlobal,
        bool expected)
    {
        var inventory = new D2RLoaderInventory
        {
            InstallDirectory = "C:/D2R",
            LoaderPath = "C:/D2R/D2RLoader.exe",
            GlobalRoot = "C:/D2R/d2rloader",
            ModRoot = "C:/D2R/mods/Reimagined/d2rloader",
            IsInstalled = true,
            AllowGlobalExtensions = allowGlobal,
            AllowModExtensions = true,
            Extensions =
            [
                new D2RLoaderExtensionInfo
                {
                    Name = fileName,
                    FileName = fileName,
                    FilePath = "C:/D2R/mods/Reimagined/d2rloader/plugins/" + fileName,
                    Kind = D2RLoaderExtensionKind.Plugin,
                    Scope = scope,
                    IsEnabled = isEnabled
                }
            ]
        };
        Assert.Equal(expected, AccountLootFilterService.IsFilterPluginAvailable(inventory, experience));
    }
}
