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
        var client = Client(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[{\"id\":\"one\",\"name\":\"Starter\",\"revision\":3}]}") }; });
        var service = new AccountLootFilterService(client, clock); var user = Guid.NewGuid();
        service.UseAccount(user);
        var items = await service.ListAsync(user, false, default);
        Assert.Single(items);
        Assert.Equal(3, items[0].Revision);
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
        var client = Client(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"one\",\"name\":\"Strict\",\"revision\":4,\"document\":{\"version\":3,\"rules\":[]}}") });
        var details = await client.GetLootFilterAsync("one");
        Assert.Equal("Strict", details.Name);
        Assert.Equal(4, details.Revision);
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
    public async Task InvalidDocumentNeverChangesExistingFilter(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-test-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config", "filter"));
            var path = Path.Combine(root, "config", "filter", "Strict.json");
            await File.WriteAllTextAsync(path, "previous valid filter");
            var service = new AccountLootFilterService(Client(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"f1\",\"name\":\"Strict\",\"revision\":2,\"document\":{json}}}")
            }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.PrepareSyncAsync(root, [new AccountLootFilter("f1", "Strict", 2)], [new SyncedLootFilterEntry("f1", "Strict", 1, "Strict.json")], () => { }, default));
            Assert.Equal("previous valid filter", await File.ReadAllTextAsync(path));
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
    public async Task FilterSyncKeepsThreeBackupsBacksUpDeletedFilesAndExportsNoMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-test-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var legacyPath = Path.Combine(root, "config", "filter.json");
            await File.WriteAllTextAsync(legacyPath, "original custom filter");

            var revision = 0;
            var service = new AccountLootFilterService(Client(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"f1\",\"name\":\"Starter\",\"revision\":{revision},\"document\":{{\"version\":3,\"rules\":[{{\"show\":{{\"ruleName\":\"{revision}\"}}}}]}}}}")
            }));

            IReadOnlyList<SyncedLootFilterEntry>? prev = null;
            for (int i = 1; i <= 5; i++)
            {
                revision = i;
                var prepared = await service.PrepareSyncAsync(root, [new AccountLootFilter("f1", "Starter", i)], prev, () => { }, default);
                var result = AccountLootFilterService.CommitSync(root, prepared, () => { }, prev);
                Assert.Null(result.BackupWarning);
                prev = result.Synced;
            }

            var backupRoot = LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups");
            Assert.Equal(3, Directory.GetDirectories(backupRoot).Length);
            var targetPath = Path.Combine(root, "config", "filter", "Starter.json");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(targetPath));
            Assert.Equal(new[] { "version", "rules" }, saved.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("5", saved.RootElement.GetProperty("rules")[0].GetProperty("show").GetProperty("ruleName").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true); }
    }

    [Fact]
    public void BuildTargetPlanSanitizesUnsafeAndReservedDottedNamesAndDisambiguatesDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-plan-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        var plan = AccountLootFilterService.BuildTargetPlan(
        [
            new AccountLootFilter("id-1", "My: Strict / Filter?", 1),
            new AccountLootFilter("id-2", "my: strict / filter?", 2),
            new AccountLootFilter("id-3", "CON", 1),
            new AccountLootFilter("id-4", "   ...   ", 1),
            new AccountLootFilter("id-5", "CON.v2", 1),
            new AccountLootFilter("id-6", "COM1.strict", 1),
            new AccountLootFilter("id-7", "LPT0", 1),
            new AccountLootFilter("id-8", "CONIN$", 1),
            new AccountLootFilter("../bad.. ", "NUL.backup", 1),
            new AccountLootFilter("id-9", new string('a', 47) + "🔥", 1)
        ]);

        Assert.Equal("My-Strict-Filter.json", plan[0].FileName);
        Assert.Equal("my-strict-filter (2).json", plan[1].FileName);
        Assert.Equal("Filter-id-3.json", plan[2].FileName);
        Assert.Equal("Filter-id-4.json", plan[3].FileName);
        Assert.Equal("Filter-id-5.json", plan[4].FileName);
        Assert.Equal("Filter-id-6.json", plan[5].FileName);
        Assert.Equal("Filter-id-7.json", plan[6].FileName);
        Assert.Equal("Filter-id-8.json", plan[7].FileName);
        Assert.Equal("Filter-bad.json", plan[8].FileName);
        Assert.Equal(new string('a', 47) + ".json", plan[9].FileName);

        var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var entry in plan)
        {
            Assert.Equal(entry.FileName, strictUtf8.GetString(strictUtf8.GetBytes(entry.FileName)));
            var resolved = LocalPluginInstallService.SafePath(root, "config/filter/" + entry.FileName);
            Assert.EndsWith(entry.FileName, resolved, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SyncMirrorsWebsiteFiltersOneToOneSkipsUnchangedRevisionsAndDeletesRemovedFiltersWithBackups()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-sync-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config", "filter"));
            var legacyPath = Path.Combine(root, "config", "filter.json");
            var stalePath = Path.Combine(root, "config", "filter", "OldDeleted.json");
            await File.WriteAllTextAsync(legacyPath, "{\"version\":3,\"rules\":[{\"hide\":{\"ruleName\":\"legacy\"}}]}");
            await File.WriteAllTextAsync(stalePath, "{\"version\":3,\"rules\":[{\"hide\":{\"ruleName\":\"stale\"}}]}");

            var detailCalls = new List<string>();
            var client = Client(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/loot-filters/f1", StringComparison.Ordinal))
                {
                    detailCalls.Add("f1");
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"f1\",\"name\":\"Starter\",\"revision\":1,\"document\":{\"version\":3,\"rules\":[]}}")
                    };
                }
                if (path.EndsWith("/loot-filters/f2", StringComparison.Ordinal))
                {
                    detailCalls.Add("f2");
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"f2\",\"name\":\"Strict V2\",\"revision\":2,\"document\":{\"version\":3,\"rules\":[{\"hide\":{\"ruleName\":\"Strict\"}}]}}")
                    };
                }
                throw new InvalidOperationException("Unexpected request: " + path);
            });

            var service = new AccountLootFilterService(client);
            var remoteInitial = new[]
            {
                new AccountLootFilter("f1", "Starter", 1),
                new AccountLootFilter("f2", "Strict V2", 2)
            };

            Assert.False(AccountLootFilterService.IsFullySynced(root, remoteInitial, null));
            var prepared1 = await service.PrepareSyncAsync(root, remoteInitial, null, () => { }, default);
            var result1 = AccountLootFilterService.CommitSync(root, prepared1, () => { });

            Assert.Equal(["f1", "f2"], detailCalls);
            Assert.Equal(2, result1.UpdatedCount);
            Assert.Equal(2, result1.DeletedCount); // OldDeleted.json + legacy config/filter.json
            Assert.False(File.Exists(legacyPath));
            Assert.False(File.Exists(stalePath));
            Assert.True(File.Exists(Path.Combine(root, "config", "filter", "Starter.json")));
            Assert.True(File.Exists(Path.Combine(root, "config", "filter", "Strict V2.json")));
            Assert.True(AccountLootFilterService.IsFullySynced(root, remoteInitial, result1.Synced));

            // Verify both displaced local files were backed up before deletion.
            var backupParent = LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups");
            Assert.Single(Directory.GetFiles(backupParent, "filter.json", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(backupParent, "OldDeleted.json", SearchOption.AllDirectories));

            // Second sync with unchanged revisions should download nothing (0 detail calls)
            detailCalls.Clear();
            var prepared2 = await service.PrepareSyncAsync(root, remoteInitial, result1.Synced, () => { }, default);
            Assert.Empty(detailCalls);
            Assert.Empty(prepared2.DownloadedPayloadsById);

            // Third sync where f2 is deleted on the website and f1 is renamed without a revision bump
            var remoteAfterDeleteAndRename = new[]
            {
                new AccountLootFilter("f1", "Starter Renamed", 1)
            };
            Assert.False(AccountLootFilterService.IsFullySynced(root, remoteAfterDeleteAndRename, result1.Synced));
            var prepared3 = await service.PrepareSyncAsync(root, remoteAfterDeleteAndRename, result1.Synced, () => { }, default);
            Assert.Empty(detailCalls); // renamed filter reuses local Starter.json bytes without re-downloading
            var result3 = AccountLootFilterService.CommitSync(root, prepared3, () => { }, result1.Synced);

            Assert.True(File.Exists(Path.Combine(root, "config", "filter", "Starter Renamed.json")));
            Assert.False(File.Exists(Path.Combine(root, "config", "filter", "Starter.json")));
            Assert.False(File.Exists(Path.Combine(root, "config", "filter", "Strict V2.json")));
            Assert.True(AccountLootFilterService.IsFullySynced(root, remoteAfterDeleteAndRename, result3.Synced));

            // Fourth sync where all filters are deleted on the website removes the filter directory cleanly and backs up Starter Renamed.json
            var prepared4 = await service.PrepareSyncAsync(root, [], result3.Synced, () => { }, default);
            var result4 = AccountLootFilterService.CommitSync(root, prepared4, () => { }, result3.Synced);
            Assert.Empty(result4.Synced);
            Assert.False(Directory.Exists(Path.Combine(root, "config", "filter")));
            Assert.True(AccountLootFilterService.IsFullySynced(root, [], result4.Synced));
            Assert.Single(Directory.GetFiles(backupParent, "Starter Renamed.json", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true);
        }
    }

    [Fact]
    public async Task ZeroWebsiteFiltersWithoutPriorSyncLeavesCustomLocalFiltersUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-local-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config", "filter"));
            var legacyCustom = Path.Combine(root, "config", "filter.json");
            var profileCustom = Path.Combine(root, "config", "filter", "Handcrafted.json");
            await File.WriteAllTextAsync(legacyCustom, "custom filter.json");
            await File.WriteAllTextAsync(profileCustom, "custom profile");

            Assert.True(AccountLootFilterService.IsFullySynced(root, [], null, hadLegacyInstall: false));

            var service = new AccountLootFilterService(Client(_ => throw new InvalidOperationException("No HTTP expected")));
            var prepared = await service.PrepareSyncAsync(root, [], null, () => { }, default);
            var result = AccountLootFilterService.CommitSync(root, prepared, () => { }, null, hadLegacyInstall: false);

            Assert.Equal(0, result.DeletedCount);
            Assert.Equal("custom filter.json", await File.ReadAllTextAsync(legacyCustom));
            Assert.Equal("custom profile", await File.ReadAllTextAsync(profileCustom));

            // When hadLegacyInstall is true (user deleted a previously launcher-installed website filter), legacy file is backed up and removed.
            Assert.False(AccountLootFilterService.IsFullySynced(root, [], null, hadLegacyInstall: true));
            File.Delete(profileCustom);
            Directory.Delete(Path.Combine(root, "config", "filter"));
            var legacyResult = AccountLootFilterService.CommitSync(root, prepared, () => { }, null, hadLegacyInstall: true);
            Assert.Equal(1, legacyResult.DeletedCount);
            Assert.False(File.Exists(legacyCustom));
            var backupParent = LocalPluginInstallService.BackupDirectory(root, ".launcher-filter-backups");
            Assert.Single(Directory.GetFiles(backupParent, "filter.json", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true);
        }
    }

    [Fact]
    public async Task DisambiguatedShiftSwapAndCaseOnlyRenamePreserveExactFilterPayloadsAndCasing()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-filter-swap-" + Guid.NewGuid().ToString("N"), "mods", "Reimagined", "d2rloader");
        try
        {
            const string docA = "{\"version\":3,\"rules\":[{\"show\":{\"ruleName\":\"Rules-A\"}}]}";
            const string docB = "{\"version\":3,\"rules\":[{\"hide\":{\"ruleName\":\"Rules-B\"}}]}";
            var service = new AccountLootFilterService(Client(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/f1", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":\"f1\",\"name\":\"Strict\",\"revision\":1,\"document\":{docA}}}") };
                if (path.EndsWith("/f2", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":\"f2\",\"name\":\"Strict?\",\"revision\":1,\"document\":{docB}}}") };
                throw new InvalidOperationException("Unexpected: " + path);
            }));

            // Initial sync: f1 -> Strict.json (Rules-A), f2 -> Strict (2).json (Rules-B)
            var initial = new[] { new AccountLootFilter("f1", "Strict", 1), new AccountLootFilter("f2", "Strict?", 1) };
            var prep1 = await service.PrepareSyncAsync(root, initial, null, () => { }, default);
            var res1 = AccountLootFilterService.CommitSync(root, prep1, () => { });

            // Reorder (e.g. clicking 'Use' on f2 on website without revision bump): f2 -> Strict.json, f1 -> Strict (2).json
            var swapped = new[] { new AccountLootFilter("f2", "Strict?", 1), new AccountLootFilter("f1", "Strict", 1) };
            Assert.False(AccountLootFilterService.IsFullySynced(root, swapped, res1.Synced));
            var prepSwap = await service.PrepareSyncAsync(root, swapped, res1.Synced, () => { }, default);
            Assert.Empty(prepSwap.DownloadedPayloadsById);
            var resSwap = AccountLootFilterService.CommitSync(root, prepSwap, () => { }, res1.Synced);
            Assert.Contains("Rules-B", await File.ReadAllTextAsync(Path.Combine(root, "config", "filter", "Strict.json")));
            Assert.Contains("Rules-A", await File.ReadAllTextAsync(Path.Combine(root, "config", "filter", "Strict (2).json")));
            Assert.True(AccountLootFilterService.IsFullySynced(root, swapped, resSwap.Synced));

            // Delete f2 ("Strict.json"): f1 shifts from "Strict (2).json" -> "Strict.json" with Rules-A intact
            var afterDelete = new[] { new AccountLootFilter("f1", "Strict", 1) };
            var prepShift = await service.PrepareSyncAsync(root, afterDelete, resSwap.Synced, () => { }, default);
            Assert.Empty(prepShift.DownloadedPayloadsById);
            var resShift = AccountLootFilterService.CommitSync(root, prepShift, () => { }, resSwap.Synced);
            Assert.Contains("Rules-A", await File.ReadAllTextAsync(Path.Combine(root, "config", "filter", "Strict.json")));
            Assert.False(File.Exists(Path.Combine(root, "config", "filter", "Strict (2).json")));
            Assert.True(AccountLootFilterService.IsFullySynced(root, afterDelete, resShift.Synced));

            // Case-only rename: "Strict" -> "strict" updates actual directory-entry casing on disk
            var caseRenamed = new[] { new AccountLootFilter("f1", "strict", 1) };
            Assert.False(AccountLootFilterService.IsFullySynced(root, caseRenamed, resShift.Synced));
            var prepCase = await service.PrepareSyncAsync(root, caseRenamed, resShift.Synced, () => { }, default);
            var resCase = AccountLootFilterService.CommitSync(root, prepCase, () => { }, resShift.Synced);
            var actualFiles = Directory.GetFiles(Path.Combine(root, "config", "filter"), "*.json").Select(f => Path.GetFileName(f)).ToArray();
            Assert.Equal(new[] { "strict.json" }, actualFiles);
            Assert.True(AccountLootFilterService.IsFullySynced(root, caseRenamed, resCase.Synced));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), true);
        }
    }

    [Fact]
    public async Task MultiRootSyncAndPartialFailureKeepOnlineAndLadderRootsConsistent()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "launcher-multi-root-" + Guid.NewGuid().ToString("N"));
        var onlineRoot = Path.Combine(baseDir, "mods", "Reimagined", "d2rloader");
        var ladderRoot = Path.Combine(baseDir, "mods", "ReimaginedLadder", "d2rloader");
        try
        {
            Directory.CreateDirectory(onlineRoot);
            Directory.CreateDirectory(ladderRoot);

            var detailCalls = 0;
            var service = new AccountLootFilterService(Client(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/f1", StringComparison.Ordinal))
                {
                    detailCalls++;
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"f1\",\"name\":\"Main\",\"revision\":2,\"document\":{\"version\":3,\"rules\":[]}}") };
                }
                if (path.EndsWith("/f-deleted", StringComparison.Ordinal))
                    return new(HttpStatusCode.NotFound);
                if (path.EndsWith("/f-bad", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"f-bad\",\"name\":\"Broken\",\"revision\":2,\"document\":{\"version\":2,\"rules\":[]}}") };
                throw new InvalidOperationException("Unexpected: " + path);
            }));

            // Seed onlineRoot as already synced for f1 rev 2 and f-bad rev 1; ladderRoot is empty.
            Directory.CreateDirectory(Path.Combine(onlineRoot, "config", "filter"));
            var validJson = LootFilterValidation.Validate(JsonSerializer.Deserialize<JsonElement>("{\"version\":3,\"rules\":[]}"));
            await File.WriteAllTextAsync(Path.Combine(onlineRoot, "config", "filter", "Main.json"), validJson);
            await File.WriteAllTextAsync(Path.Combine(onlineRoot, "config", "filter", "Broken.json"), validJson);

            var prevByRoot = new Dictionary<string, IReadOnlyList<SyncedLootFilterEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                [onlineRoot] =
                [
                    new SyncedLootFilterEntry("f1", "Main", 2, "Main.json"),
                    new SyncedLootFilterEntry("f-bad", "Broken", 1, "Broken.json")
                ]
            };

            var remote = new[]
            {
                new AccountLootFilter("f1", "Main", 2),
                new AccountLootFilter("f-deleted", "Gone", 1),
                new AccountLootFilter("f-bad", "Broken", 2)
            };

            var prepared = await service.PrepareMultiRootSyncAsync([onlineRoot, ladderRoot], remote, prevByRoot, () => { }, default);
            Assert.Equal(1, detailCalls); // f1 downloaded once because ladderRoot lacked it
            Assert.Contains("Broken", prepared.Warning);

            var onlineResult = AccountLootFilterService.CommitSync(onlineRoot, prepared, () => { }, prevByRoot[onlineRoot]);
            var ladderResult = AccountLootFilterService.CommitSync(ladderRoot, prepared, () => { }, null);

            Assert.Equal(0, onlineResult.UpdatedCount); // onlineRoot already had identical Main.json and preserves prior Broken.json
            Assert.True(File.Exists(Path.Combine(onlineRoot, "config", "filter", "Broken.json")));
            Assert.Equal(1, ladderResult.UpdatedCount);
            Assert.True(File.Exists(Path.Combine(ladderRoot, "config", "filter", "Main.json")));
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, true);
        }
    }

    [Theory]
    [InlineData("pyre-lootfilter.dll", true)]
    [InlineData("PYRE-LOOTFILTER.DLL", true)]
    [InlineData("d2rl-pyre-lootfilter.dll", true)]
    [InlineData("unhoarder.dll", false)]
    [InlineData("UnHoarder.dll", false)]
    [InlineData("other-plugin.dll", false)]
    public void SupportedFilterPluginsRequirePyreLootFilterForProfileSync(string fileName, bool expected)
        => Assert.Equal(expected, AccountLootFilterService.IsSupportedFilterPlugin(fileName));

    [Theory]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, true)]
    [InlineData("unhoarder.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Ladder, true, false, true)]
    [InlineData("unhoarder.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Ladder, true, false, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Online, true, true, true)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Online, true, false, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Global, LaunchExperience.Ladder, true, true, false)]
    [InlineData("pyre-lootfilter.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, false, true, false)]
    [InlineData("other-plugin.dll", D2RLoaderExtensionScope.Reimagined, LaunchExperience.Online, true, true, false)]
    public void FilterPluginAvailabilityRequiresPyreLootFilterPerModeScope(
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
