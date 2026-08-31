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
        Assert.Equal("announcement-code"u8.ToArray(), verified.Files[fixture.Descriptor.Files[0].TargetPath]);
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
            "mods",
            "Reimagined",
            "d2rloader",
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
    public async Task FailedInstallRestoresPreviouslyInstalledFiles()
    {
        var fixture = CreateBundle(
            ("announcements", "d2rl-announcements.dll", "new-announcement-code"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        var firstTarget = Path.Combine(
            _installDirectory,
            fixture.Descriptor.Files[0].TargetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(firstTarget)!);
        await File.WriteAllBytesAsync(firstTarget, "player-original"u8.ToArray());
        var blockedTarget = Path.Combine(
            _installDirectory,
            fixture.Descriptor.Files[1].TargetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(blockedTarget);
        var service = CreateService(fixture.Archive);

        await Assert.ThrowsAnyAsync<IOException>(
            () => service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor));

        Assert.Equal("player-original"u8.ToArray(), await File.ReadAllBytesAsync(firstTarget));
        Assert.True(Directory.Exists(blockedTarget));
        Assert.False(File.Exists(Path.Combine(
            _installDirectory,
            "mods",
            "Reimagined",
            "d2rloader",
            "ladder-bundle-state.json")));
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
        byte[][] archivedContents)
    {
        var bundleId = Guid.NewGuid();
        var ladderId = Guid.NewGuid();
        var compatibility = new LadderBundleCompatibility("0.0.0", "0.0.0", "", "", "", "*");
        var manifestFiles = signedFiles.Select((file, index) => new LadderBundleManifestFile(
            Guid.NewGuid(),
            file.Id,
            file.Id,
            "0.1.0",
            D2RLoaderExtensionKind.Plugin,
            true,
            $"payload/plugins/{file.FileName}",
            $"mods/Reimagined/d2rloader/plugins/{file.FileName}",
            file.FileName,
            file.Content.LongLength,
            Convert.ToHexString(SHA256.HashData(file.Content)))).ToArray();
        var manifest = new LadderBundleManifest(
            1,
            bundleId,
            ladderId,
            1,
            DateTimeOffset.UtcNow,
            "test-source-commit",
            compatibility,
            manifestFiles);
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
            "Ready",
            Convert.ToHexString(SHA256.HashData(archive)),
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            signature,
            "test-key",
            "test-source-commit",
            archive.LongLength,
            compatibility,
            manifestFiles,
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
