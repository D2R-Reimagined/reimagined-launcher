using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ReimaginedLauncher.Tests;

[CollectionDefinition("Ladder bundle signing", DisableParallelization = true)]
public sealed class LadderBundleSigningCollection;

[Collection("Ladder bundle signing")]
public sealed class LadderBundleServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string? _previousPublicKey;
    private readonly string _installDirectory;

    public LadderBundleServiceTests()
    {
        _previousPublicKey = LadderBundleService.TrustedKeyOverridePem;
        LadderBundleService.TrustedKeyOverridePem = _signingKey.ExportSubjectPublicKeyInfoPem();
        _installDirectory = Path.Combine(Path.GetTempPath(), "reimagined-ladder-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_installDirectory);
        File.WriteAllBytes(Path.Combine(_installDirectory, "D2RLoader.exe"), [0x4D, 0x5A]);
    }

    [Fact]
    public void VerifyArchiveAcceptsAnExactSignedManifestAndPayload()
    {
        var fixture = CreateBundle(("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()));

        var verified = LadderBundleService.VerifyArchive(fixture.Descriptor, fixture.Archive);

        Assert.Equal(fixture.Descriptor.Id, verified.Manifest.BundleId);
        Assert.Equal(fixture.Archive, verified.ArchiveBytes);
    }

    [Fact]
    public void VerifyArchiveRejectsPayloadThatDoesNotMatchTheSignedHash()
    {
        var fixture = CreateBundle(
            [("announcements", "d2rl-announcements.dll", "signed-code"u8.ToArray())],
            ["evil--code!"u8.ToArray()]);

        var exception = Assert.Throws<InvalidDataException>(
            () => LadderBundleService.VerifyArchive(fixture.Descriptor, fixture.Archive));

        Assert.Contains("failed its signed SHA-256 check", exception.Message);
    }

    [Fact]
    public void VerifyArchiveRejectsCompatibilityThatDiffersFromTheApiDescriptor()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        var changedDescriptor = fixture.Descriptor with
        {
            Compatibility = fixture.Descriptor.Compatibility with { RequiredModVersion = "unexpected" }
        };

        Assert.Throws<InvalidDataException>(
            () => LadderBundleService.VerifyArchive(changedDescriptor, fixture.Archive));
    }

    [Theory]
    [InlineData("1.2.0-beta+preview.3", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.1.9", "1.2.0", false)]
    [InlineData("2.0.0-rc.1", "1.2.0", true)]
    public void SemanticVersionDecorationDoesNotBlockTheCompatibilityCheck(
        string installed,
        string required,
        bool isSatisfied)
    {
        Assert.True(LadderBundleService.TryParseVersionCore(installed, out var current));
        Assert.True(LadderBundleService.TryParseVersionCore(required, out var minimum));
        Assert.Equal(isSatisfied, current >= minimum);
    }

    [Fact]
    public void VerifyArchiveRejectsAnEntryTheSignedManifestDoesNotDeclare()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        using var repacked = new MemoryStream();
        using (var source = new MemoryStream(fixture.Archive, writable: false))
        using (var original = new ZipArchive(source, ZipArchiveMode.Read))
        using (var smuggled = new ZipArchive(repacked, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                using var content = entry.Open();
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                WriteEntry(smuggled, entry.FullName, buffer.ToArray());
            }

            WriteEntry(smuggled, "payload/plugins/d2rl-cheat.dll", "cheat-code"u8.ToArray());
        }

        var archive = repacked.ToArray();
        var descriptor = fixture.Descriptor with
        {
            ArtifactSha256 = Convert.ToHexString(SHA256.HashData(archive)),
            ArtifactSizeBytes = archive.LongLength
        };

        Assert.Throws<InvalidDataException>(() => LadderBundleService.VerifyArchive(descriptor, archive));
    }

    [Fact]
    public void VerifyArchiveRejectsAManifestSignedByAnUntrustedKey()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trusted = LadderBundleService.TrustedKeyOverridePem;
        LadderBundleService.TrustedKeyOverridePem = attacker.ExportSubjectPublicKeyInfoPem();
        try
        {
            Assert.Throws<InvalidDataException>(
                () => LadderBundleService.VerifyArchive(fixture.Descriptor, fixture.Archive));
        }
        finally
        {
            LadderBundleService.TrustedKeyOverridePem = trusted;
        }
    }

    [Fact]
    public async Task InstallOrRepairWritesAndReverifiesEveryManagedFile()
    {
        var fixture = CreateBundle(
            ("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
        foreach (var file in fixture.Descriptor.Files)
        {
            Assert.True(File.Exists(Path.Combine(_installDirectory, file.TargetPath.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public async Task OptionalSelectionDownloadsOnceAndDeselectionBacksUpWithoutBundleRepair()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "required-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var bytes = "optional-code"u8.ToArray();
        var optional = new LadderAllowedExtensionResponse(Guid.NewGuid(), "Optional", "d2rl-optional.dll",
            Convert.ToHexString(SHA256.HashData(bytes)), D2RLoaderExtensionKind.Plugin, false, bytes.Length, "/download");
        HashSet<Guid> selected = [optional.Id];
        var downloads = 0;
        Task<byte[]> Download(LadderAllowedExtensionResponse _, CancellationToken token)
        {
            downloads++;
            return Task.FromResult(bytes);
        }
        var pending = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [optional], selectedExtensionIds: selected);
        Assert.False(pending.IsReady);
        Assert.False(pending.RequiresBundleRepair);
        await LadderOptionalExtensionService.SynchronizeAsync(_installDirectory, fixture.Descriptor, [optional], selected, Download);
        var ready = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [optional], selectedExtensionIds: selected);
        Assert.True(ready.IsReady, ready.Status);
        await LadderOptionalExtensionService.SynchronizeAsync(_installDirectory, fixture.Descriptor, [optional], selected, Download);
        Assert.Equal(1, downloads);
        selected.Clear();
        pending = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [optional], selectedExtensionIds: selected);
        Assert.False(pending.IsReady);
        Assert.False(pending.RequiresBundleRepair);
        await LadderOptionalExtensionService.SynchronizeAsync(_installDirectory, fixture.Descriptor, [optional], selected, Download);
        Assert.False(File.Exists(Path.Combine(_installDirectory, LadderOptionalExtensionService.TargetPath(optional))));
        Assert.Single(Directory.GetFiles(Path.Combine(_installDirectory, ".reimagined-launcher", "ladder-bundles", "optional-backups"), optional.FileName, SearchOption.AllDirectories));
        Assert.Equal(1, downloads);
        ready = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [optional], selectedExtensionIds: selected);
        Assert.True(ready.IsReady, ready.Status);
    }

    [Fact]
    public async Task OptionalHashChangeRequiresUpdateAndRevocationRemovesTheFile()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "required-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var bytes = "optional-code"u8.ToArray();
        var optional = new LadderAllowedExtensionResponse(Guid.NewGuid(), "Patch", "optional.json",
            Convert.ToHexString(SHA256.HashData(bytes)), D2RLoaderExtensionKind.Patch, false, bytes.Length, "/download");
        HashSet<Guid> selected = [optional.Id];
        await LadderOptionalExtensionService.SynchronizeAsync(_installDirectory, fixture.Descriptor, [optional], selected,
            (_, _) => Task.FromResult(bytes));
        var path = Path.Combine(_installDirectory, LadderOptionalExtensionService.TargetPath(optional));
        await File.WriteAllBytesAsync(path, "tampered-code"u8.ToArray());
        var pending = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [optional], selectedExtensionIds: selected);
        Assert.False(pending.IsReady);
        Assert.False(pending.RequiresBundleRepair);
        await Assert.ThrowsAsync<InvalidDataException>(() => LadderOptionalExtensionService.SynchronizeAsync(
            _installDirectory, fixture.Descriptor, [optional], selected, (_, _) => Task.FromResult("bad"u8.ToArray())));
        Assert.Equal("tampered-code", await File.ReadAllTextAsync(path));
        await LadderOptionalExtensionService.SynchronizeAsync(_installDirectory, fixture.Descriptor, [], selected,
            (_, _) => throw new InvalidOperationException("Revocation must not download"));
        Assert.False(File.Exists(path));
        Assert.True((await service.GetReadinessAsync(_installDirectory, fixture.Descriptor,
            allowedExtensions: [], selectedExtensionIds: selected)).IsReady);
    }

    [Fact]
    public async Task OptionalsCannotOverrideRequiredFilesOrWhitelistUnrelatedJson()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "required-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var file = fixture.Descriptor.Files[0];
        var optional = new LadderAllowedExtensionResponse(Guid.NewGuid(), "Override", file.FileName, file.Sha256,
            D2RLoaderExtensionKind.Plugin, false, file.SizeBytes, "/download");
        await Assert.ThrowsAsync<InvalidDataException>(() => LadderOptionalExtensionService.SynchronizeAsync(
            _installDirectory, fixture.Descriptor, [optional], new HashSet<Guid> { optional.Id },
            (_, _) => throw new InvalidOperationException("Must not download")));
        await File.WriteAllTextAsync(Path.Combine(_installDirectory, "mods", "Reimagined", "cheat.json"), "{}");
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor, allowedExtensions: []);
        Assert.False(readiness.IsReady);
        Assert.True(readiness.RequiresBundleRepair);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("file.dll:stream")]
    [InlineData("C:\\outside.dll")]
    [InlineData("wrong.json")]
    public void OptionalDownloadRejectsInvalidPluginMetadata(string name)
    {
        var optional = new LadderAllowedExtensionResponse(Guid.NewGuid(), "Invalid", name, new string('A', 64),
            D2RLoaderExtensionKind.Plugin, false, 10, "/download");
        Assert.False(LadderOptionalExtensionService.CanDownload(optional));
    }

    [Fact]
    public async Task InstallOrRepairManagesJsonAndTxtFilesAlongsidePlugins()
    {
        var fixture = CreateBundle(
            ("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()),
            ("modinfo", "modinfo.json", "{\"version\":\"3.0.11\"}"u8.ToArray()),
            ("levels", "levels.txt", "level-data"u8.ToArray()));
        var service = CreateService(fixture.Archive);

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
        Assert.Contains(fixture.Descriptor.Files, file => file.FileName == "modinfo.json");
        Assert.Contains(fixture.Descriptor.Files, file => file.FileName == "levels.txt");
    }

    [Fact]
    public async Task ReadinessRejectsEditedStateAndModifiedInstalledFiles()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var target = Path.Combine(
            _installDirectory,
            fixture.Descriptor.Files[0].TargetPath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(target, "bad-code"u8.ToArray());

        var modifiedReadiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(modifiedReadiness.IsReady);
        Assert.Contains(modifiedReadiness.Problems, problem => problem.Contains("modified after installation"));

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var statePath = Path.Combine(
            _installDirectory,
            ".reimagined-launcher",
            "ladder-bundles",
            "ladder-bundle-state.json");
        var state = JsonSerializer.Deserialize<InstalledLadderBundleState>(
            await File.ReadAllTextAsync(statePath),
            JsonOptions)!;
        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(
                state with { ManifestBase64 = Convert.ToBase64String("not-the-manifest"u8.ToArray()) },
                JsonOptions));

        var editedManifestReadiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(editedManifestReadiness.IsReady);
        Assert.Contains(editedManifestReadiness.Problems, problem => problem.Contains("manifest"));

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        state = JsonSerializer.Deserialize<InstalledLadderBundleState>(
            await File.ReadAllTextAsync(statePath),
            JsonOptions)!;
        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(state with { Files = [] }, JsonOptions));

        var editedStateReadiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(editedStateReadiness.IsReady);
        Assert.Contains(editedStateReadiness.Problems, problem => problem.Contains("state does not match"));
    }

    [Fact]
    public async Task ReadinessAcceptsOnlyLauncherManagedFilesWithASignedBaseline()
    {
        var original = "{\"type\":\"CharacterSelectPanel\"}"u8.ToArray();
        var files = new[]
        {
            ("layout", "characterselectpanelhd.json", original),
            ("announcements", "d2rl-announcements.dll", "plugin"u8.ToArray())
        };
        var targetPath = "mods/Reimagined/Reimagined.mpq/data/global/ui/layouts/characterselectpanelhd.json";
        var fixture = CreateBundle(
            files,
            files.Select(file => file.Item3).ToArray(),
            targetPaths: new Dictionary<string, string> { ["layout"] = targetPath });
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        var target = Path.Combine(_installDirectory, targetPath.Replace('/', Path.DirectorySeparatorChar));
        var baseline = LadderRuntimeFileService.RestoreOrCaptureBaseline(_installDirectory, target);
        await File.WriteAllTextAsync(target, "launcher-generated ladder banner");
        var config = Path.Combine(
            _installDirectory,
            "mods",
            "Reimagined",
            "d2rloader",
            "config",
            "server-saves.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "enabled = true");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(config)!, "announcements.toml"),
            "enabled = true");
        var logs = Path.Combine(
            _installDirectory,
            "mods",
            "Reimagined",
            "d2rloader",
            "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "announcements.log"), "runtime output");
        await File.WriteAllTextAsync(Path.Combine(logs, "server-saves.log"), "runtime output");

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);

        Assert.True(readiness.IsReady, readiness.Status);

        var unapprovedConfig = Path.Combine(Path.GetDirectoryName(config)!, "unapproved.toml");
        await File.WriteAllTextAsync(unapprovedConfig, "enabled = true");
        readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.Problems, problem => problem.Contains("unapproved.toml"));
        File.Delete(unapprovedConfig);

        await File.WriteAllTextAsync(baseline, "tampered baseline");
        readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.Problems, problem => problem.Contains("modified after installation"));
    }

    [Fact]
    public async Task InstallOrRepairReplacesTheWholeModTreeAndRemovesUndeclaredFiles()
    {
        var fixture = CreateBundle(
            ("announcements", "d2rl-announcements.dll", "new-announcement-code"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        var undeclared = Path.Combine(
            _installDirectory,
            "mods",
            "Reimagined",
            "unapproved.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(undeclared)!);
        await File.WriteAllTextAsync(undeclared, "local change");
        var service = CreateService(fixture.Archive);

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        Assert.False(File.Exists(undeclared));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public async Task ReadinessRejectsFilesOutsideTheSignedManifest()
    {
        var fixture = CreateBundle(("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var undeclared = Path.Combine(_installDirectory, "mods", "Reimagined", "local-change.txt");
        await File.WriteAllTextAsync(undeclared, "not signed");

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);

        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.Problems, problem => problem.Contains("is not declared"));
    }

    [Fact]
    public async Task LegacyPluginOnlyBundlesDoNotReplaceTheModTree()
    {
        var files = new[] { ("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()) };
        var fixture = CreateBundle(files, files.Select(file => file.Item3).ToArray(), schemaVersion: 1);
        var existingData = Path.Combine(
            _installDirectory,
            "mods",
            "Reimagined",
            "Reimagined.mpq",
            "data",
            "global",
            "excel",
            "levels.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingData)!);
        await File.WriteAllTextAsync(existingData, "existing mod data");
        var service = CreateService(fixture.Archive);

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        Assert.Equal("existing mod data", await File.ReadAllTextAsync(existingData));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    private LadderBundleService CreateService(byte[] archive)
    {
        // These fixtures declare no compatibility requirements, so the
        // prerequisite installers are never reached and never go near the
        // network. They exist here only to satisfy the constructor.
        var handler = new StaticResponseHandler(archive);
        return new LadderBundleService(
            new ReimaginedApiHttpClient(new HttpClient(handler)),
            new D2RLoaderInstallerService(new HttpClient(handler)),
            new ModReleaseInstallerService(new HttpClient(handler)));
    }

    private BundleFixture CreateBundle(params (string Id, string FileName, byte[] Content)[] files)
        => CreateBundle(files, files.Select(file => file.Content).ToArray());

    private BundleFixture CreateBundle(
        (string Id, string FileName, byte[] Content)[] signedFiles,
        byte[][] archivedContents,
        int schemaVersion = 2,
        IReadOnlyDictionary<string, string>? targetPaths = null)
    {
        var bundleId = Guid.NewGuid();
        var ladderId = Guid.NewGuid();
        var compatibility = new LadderBundleCompatibility("0.0.0", "0.0.0", "", "", "", "*");
        var manifestFiles = signedFiles.Select(file =>
        {
            var isPlugin = file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var targetPath = targetPaths?.GetValueOrDefault(file.Id)
                             ?? (isPlugin
                                 ? $"mods/Reimagined/d2rloader/plugins/{file.FileName}"
                                 : $"mods/Reimagined/Reimagined.mpq/data/test/{file.FileName}");
            return new LadderBundleManifestFile(
                $"payload/{targetPath}",
                targetPath,
                file.FileName,
                file.Content.LongLength,
                Convert.ToHexString(SHA256.HashData(file.Content)),
                isPlugin ? Guid.NewGuid() : Guid.Empty,
                isPlugin ? file.Id : string.Empty,
                isPlugin ? file.Id : string.Empty,
                isPlugin ? "0.1.0" : string.Empty,
                isPlugin ? D2RLoaderExtensionKind.Plugin : null,
                true);
        }).ToArray();
        var plugins = manifestFiles.Where(file => file.Kind == D2RLoaderExtensionKind.Plugin).Select(file => new LadderBundlePlugin(
            file.PluginId,
            file.Name,
            file.FileName,
            file.TargetPath,
            file.SizeBytes,
            file.Sha256)).ToArray();
        var manifest = new LadderBundleManifest(
            schemaVersion,
            bundleId,
            ladderId,
            1,
            DateTimeOffset.UtcNow,
            compatibility,
            manifestFiles,
            schemaVersion >= 2 ? plugins : null);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var signature = Convert.ToBase64String(_signingKey.SignData(manifestBytes, HashAlgorithmName.SHA256));

        byte[] archive;
        using (var output = new MemoryStream())
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "manifest.json", manifestBytes);
                WriteEntry(zip, "manifest.sig", Encoding.ASCII.GetBytes(signature));
                for (var index = 0; index < manifestFiles.Length; index++)
                {
                    WriteEntry(zip, manifestFiles[index].ArchivePath, archivedContents[index]);
                }
            }
            archive = output.ToArray();
        }

        var descriptor = new LadderBundleResponse(
            bundleId,
            ladderId,
            1,
            schemaVersion,
            "Ready",
            Convert.ToHexString(SHA256.HashData(archive)),
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            signature,
            "test-key",
            archive.LongLength,
            compatibility,
            manifestFiles,
            plugins,
            manifest.CreatedAtUtc,
            manifest.CreatedAtUtc,
            null,
            $"/ladder-bundles/{bundleId}/download");
        return new BundleFixture(descriptor, archive);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void Dispose()
    {
        LadderBundleService.TrustedKeyOverridePem = _previousPublicKey;
        _signingKey.Dispose();
        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }

    private sealed record BundleFixture(LadderBundleResponse Descriptor, byte[] Archive);

    private sealed class StaticResponseHandler(byte[] response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response)
            });
        }
    }
}
