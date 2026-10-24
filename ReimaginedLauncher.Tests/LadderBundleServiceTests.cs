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
    public async Task FailureAfterDownloadReusesCachedPackageWithANewService()
    {
        var fixture = CreateBundle(("maps", "d2rl-maps.dll", "map-code"u8.ToArray()));
        await Assert.ThrowsAsync<IOException>(() => CreateService(fixture.Archive).InstallOrRepairAsync(
            _installDirectory, fixture.Descriptor, new FailBeforeInstallProgress()));
        Assert.True(LadderBundleService.HasCachedPackage(_installDirectory, fixture.Descriptor));
        // An empty HTTP response makes an accidental network download fail verification.
        var retry = CreateService([]);
        await retry.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.True((await retry.GetReadinessAsync(_installDirectory, fixture.Descriptor)).IsReady);
    }

    private sealed class FailBeforeInstallProgress : IProgress<LadderBundleProgress>
    {
        public void Report(LadderBundleProgress value)
        {
            if (value.Message == "Installing ladder package...") throw new IOException("Simulated install failure");
        }
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
    public async Task ModeRoundTripsKeepBothInstallationsAndReuseTheSignedDownload()
    {
        const string relativeModInfo = "mods/Reimagined/Reimagined.mpq/modinfo.json";
        var modInfo = Path.Combine(_installDirectory, relativeModInfo.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(modInfo)!);
        await File.WriteAllTextAsync(modInfo, "{\"savepath\":\"NexusNormal/\",\"version\":\"normal\"}");
        var normalOnly = Path.Combine(Path.GetDirectoryName(modInfo)!, "normal-only.txt");
        await File.WriteAllTextAsync(normalOnly, "normal");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);

        var signedFiles = new[]
        {
            ("modinfo", "modinfo.json", "{\"savepath\":\"Wrong-Ladder-f2faf859/\",\"version\":\"ladder\"}"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "ladder-only-code"u8.ToArray())
        };
        var fixture = CreateBundle(signedFiles, signedFiles.Select(file => file.Item3).ToArray(),
            targetPaths: new Dictionary<string, string> { ["modinfo"] = relativeModInfo });
        using (var normalLock = File.Open(modInfo, FileMode.Open, FileAccess.Read, FileShare.Read))
            await CreateService(fixture.Archive).InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.Contains("normal", await File.ReadAllTextAsync(modInfo));
        Assert.False(Directory.Exists(NormalModInstallationService.NormalModRoot(_installDirectory)));
        var ladderModInfo = ModInstallationPaths.FindLadderModInfo(_installDirectory)!;
        var savedGames = Path.Combine(_installDirectory, "Saved Games");
        var prepared = await LadderSaveDirectoryService.PrepareAsync(
            _installDirectory, fixture.Descriptor.LadderId, "First Name", savedGames);
        Assert.Null(prepared.ErrorMessage);
        Assert.Contains(Path.GetFileName(prepared.DirectoryPath) + "/", await File.ReadAllTextAsync(ladderModInfo));

        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(_installDirectory));
        Assert.Contains("NexusNormal/", await File.ReadAllTextAsync(modInfo));
        Assert.Contains("ladder", await File.ReadAllTextAsync(ladderModInfo));
        var service = CreateService("not a valid download"u8.ToArray());
        Assert.True((await service.GetReadinessAsync(_installDirectory, fixture.Descriptor)).IsReady);

        NormalModInstallationService.Restore(_installDirectory);
        Assert.True(File.Exists(normalOnly));
        Assert.Contains("normal", await File.ReadAllTextAsync(modInfo));
        Assert.False(File.Exists(Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader", "plugins", "d2rl-maps.dll")));

        await File.WriteAllTextAsync(normalOnly, "user edit after returning to normal");
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.True((await service.GetReadinessAsync(_installDirectory, fixture.Descriptor)).IsReady);
        var preparedAgain = await LadderSaveDirectoryService.PrepareAsync(
            _installDirectory, fixture.Descriptor.LadderId, "Renamed Ladder", savedGames);
        Assert.Null(preparedAgain.ErrorMessage);
        Assert.Equal(prepared.DirectoryPath, preparedAgain.DirectoryPath);
        Assert.True((await service.GetReadinessAsync(_installDirectory, fixture.Descriptor)).IsReady);
        NormalModInstallationService.Restore(_installDirectory);
        Assert.Equal("user edit after returning to normal", await File.ReadAllTextAsync(normalOnly));
        Assert.Contains("NexusNormal/", await File.ReadAllTextAsync(modInfo));
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
            Assert.True(File.Exists(Path.Combine(_installDirectory, ModInstallationPaths.ToLadderPath(file.TargetPath).Replace('/', Path.DirectorySeparatorChar))));
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
        Assert.False(File.Exists(Path.Combine(_installDirectory, ModInstallationPaths.ToLadderPath(LadderOptionalExtensionService.TargetPath(optional)))));
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
        var path = Path.Combine(_installDirectory, ModInstallationPaths.ToLadderPath(LadderOptionalExtensionService.TargetPath(optional)));
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
        await File.WriteAllTextAsync(Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "cheat.json"), "{}");
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
            ModInstallationPaths.ToLadderPath(fixture.Descriptor.Files[0].TargetPath).Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(target, "bad-code"u8.ToArray());

        var modifiedReadiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(modifiedReadiness.IsReady);
        Assert.Contains(modifiedReadiness.Problems, problem => problem.Contains("modified after installation"));

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var statePath = Path.Combine(
            _installDirectory,
            ".reimagined-launcher",
            "ladder-bundles",
            "isolated-ladder-bundle-state.json");
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
            ("announcements", "d2rl-announcements.dll", "plugin"u8.ToArray()),
            ("celestialrayone-left-click-auras", "d2rl-celestialrayone-left-click-auras.dll", "plugin"u8.ToArray())
        };
        var targetPath = "mods/Reimagined/Reimagined.mpq/data/global/ui/layouts/characterselectpanelhd.json";
        var fixture = CreateBundle(
            files,
            files.Select(file => file.Item3).ToArray(),
            targetPaths: new Dictionary<string, string> { ["layout"] = targetPath });
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        var target = Path.Combine(_installDirectory, ModInstallationPaths.ToLadderPath(targetPath).Replace('/', Path.DirectorySeparatorChar));
        var baseline = LadderRuntimeFileService.RestoreOrCaptureBaseline(_installDirectory, target);
        await File.WriteAllTextAsync(target, "launcher-generated ladder banner");
        var config = Path.Combine(
            _installDirectory,
            "mods",
            "ReimaginedLadder",
            "d2rloader",
            "config",
            "server-saves.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "enabled = true");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(config)!, "controller-qol-updates.toml"),
            "enabled = true");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(config)!, "announcements.toml"),
            "enabled = true");
        var logs = Path.Combine(
            _installDirectory,
            "mods",
            "ReimaginedLadder",
            "d2rloader",
            "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "announcements.log"), "runtime output");
        await File.WriteAllTextAsync(Path.Combine(logs, "server-saves.log"), "runtime output");

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);

        Assert.True(readiness.IsReady, readiness.Status);

        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(config)!, "celestialrayone.left-click-auras.toml"),
            "enabled = true");
        readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);

        // Plugins write configs the bundle does not ship on launch, some under
        // their own folders (Controller QOL's per-character files): the player's.
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(config)!, "item-roll-ranges.toml"), "enabled = true");
        var perCharacter = Path.Combine(Path.GetDirectoryName(config)!, "controller-qol-skills", "620816185bbf5972.overrides.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(perCharacter)!);
        await File.WriteAllTextAsync(perCharacter, "slot = 1");
        readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);

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
            "ReimaginedLadder",
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
    public async Task InstallOrRepairKeepsPlayerConfigsTheBundleDoesNotShip()
    {
        var fixture = CreateConfigBundle();
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var config = LadderConfigPath("announcements.toml");
        await File.WriteAllTextAsync(config, "player setting");
        var log = Path.Combine(ModInstallationPaths.LadderModRoot(_installDirectory), "d2rloader", "logs", "announcements.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, "runtime output");
        var perCharacter = LadderConfigPath(Path.Combine("controller-qol-skills", "620816185bbf5972.catalog.toml"));
        Directory.CreateDirectory(Path.GetDirectoryName(perCharacter)!);
        await File.WriteAllTextAsync(perCharacter, "written on launch");

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        Assert.Equal("player setting", await File.ReadAllTextAsync(config));
        Assert.Equal("runtime output", await File.ReadAllTextAsync(log));
        Assert.Equal("written on launch", await File.ReadAllTextAsync(perCharacter));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public async Task InstallOrRepairReplacesConfigsTheBundleShips()
    {
        var fixture = CreateConfigBundle();
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var config = LadderConfigPath("maps.toml");
        await File.WriteAllTextAsync(config, "player edit of a ladder rule");

        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        Assert.Equal("ladder rule = true", await File.ReadAllTextAsync(config));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public async Task InstallOrRepairDeletesBackupsBeyondTheRetainedCount()
    {
        var fixture = CreateConfigBundle();
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var backups = Path.Combine(_installDirectory, ".reimagined-launcher", "ladder-bundles", "backups");
        var old = Enumerable.Range(0, 5).Select(index =>
        {
            var path = Path.Combine(backups, $"2026010{index + 1}000000-0000000{index}");
            Directory.CreateDirectory(Path.Combine(path, "Reimagined"));
            Directory.SetLastWriteTimeUtc(path, new DateTime(2026, 1, index + 1, 0, 0, 0, DateTimeKind.Utc));
            return path;
        }).ToArray();

        // This install backs up the first one; that backup is the newest and must survive.
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);

        var remaining = Directory.GetDirectories(backups);
        Assert.Equal(LadderBundleService.RetainedBackupCount, remaining.Length);
        Assert.Contains(remaining, path => File.Exists(Path.Combine(path, "Reimagined", "d2rloader", "config", "maps.toml")));
        Assert.Contains(old[4], remaining);
        Assert.All(old[..4], path => Assert.False(Directory.Exists(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerFilterIsNotHashEnforcedAndSurvivesRepair(bool simulateDifferentExistingPackage)
    {
        var files = new[]
        {
            ("maps-config", "maps.toml", "ladder rule = true"u8.ToArray()),
            ("filter-config", "filter.json", "{\"version\":3,\"rules\":[]}"u8.ToArray())
        };
        var fixture = CreateBundle(files, files.Select(file => file.Item3).ToArray(), targetPaths: new Dictionary<string, string>
        {
            ["maps-config"] = "mods/Reimagined/d2rloader/config/maps.toml",
            ["filter-config"] = "mods/Reimagined/d2rloader/config/filter.json"
        });
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var filter = LadderConfigPath("filter.json");
        const string custom = "{\"version\":3,\"rules\":[{\"hide\":{\"ruleName\":\"My strict filter\"}}]}";
        await File.WriteAllTextAsync(filter, custom);
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
        await File.WriteAllTextAsync(LadderConfigPath("maps.toml"), "tampered rules");
        Assert.False((await service.GetReadinessAsync(_installDirectory, fixture.Descriptor)).IsReady);
        if (simulateDifferentExistingPackage)
        {
            var state = Path.Combine(_installDirectory, ".reimagined-launcher", "ladder-bundles", "isolated-ladder-bundle-state.json");
            // An absent installed-state record exercises replacing an unrecognized previous package.
            if (File.Exists(state)) File.Delete(state);
        }
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.Equal(custom, await File.ReadAllTextAsync(filter));
        Assert.Equal("ladder rule = true", await File.ReadAllTextAsync(LadderConfigPath("maps.toml")));
        readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public async Task PackageUpdateKeepsThePlayersFilterInsteadOfTheNewDefault()
    {
        var targets = new Dictionary<string, string>
        {
            ["maps-config"] = "mods/Reimagined/d2rloader/config/maps.toml",
            ["filter-config"] = "mods/Reimagined/d2rloader/config/filter.json"
        };
        var first = new[] { ("maps-config", "maps.toml", "first rules"u8.ToArray()), ("filter-config", "filter.json", "first default"u8.ToArray()) };
        var original = CreateBundle(first, first.Select(file => file.Item3).ToArray(), targetPaths: targets);
        await CreateService(original.Archive).InstallOrRepairAsync(_installDirectory, original.Descriptor);
        await File.WriteAllTextAsync(LadderConfigPath("filter.json"), "player filter");
        var second = new[] { ("maps-config", "maps.toml", "updated rules"u8.ToArray()), ("filter-config", "filter.json", "updated default"u8.ToArray()) };
        var update = CreateBundle(second, second.Select(file => file.Item3).ToArray(), targetPaths: targets);
        var service = CreateService(update.Archive);
        await service.InstallOrRepairAsync(_installDirectory, update.Descriptor);
        Assert.Equal("player filter", await File.ReadAllTextAsync(LadderConfigPath("filter.json")));
        Assert.Equal("updated rules", await File.ReadAllTextAsync(LadderConfigPath("maps.toml")));
        var readiness = await service.GetReadinessAsync(_installDirectory, update.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public void DownloadedFilterPayloadStillRequiresItsSignedHash()
    {
        var files = new[] { ("filter-config", "filter.json", "signed filter"u8.ToArray()) };
        var fixture = CreateBundle(files, ["tampered archive filter"u8.ToArray()], targetPaths: new Dictionary<string, string>
        {
            ["filter-config"] = "mods/Reimagined/d2rloader/config/filter.json"
        });
        Assert.Throws<InvalidDataException>(() => LadderBundleService.VerifyArchive(fixture.Descriptor, fixture.Archive));
    }

    [Theory]
    [InlineData("mods/Reimagined/d2rloader/config/filter.json", true)]
    [InlineData("mods/ReimaginedLadder/d2rloader/config/filter.json", true)]
    [InlineData("mods/Reimagined/d2rloader/config/maps.toml", false)]
    [InlineData("mods/Reimagined/d2rloader/config/subfolder/filter.json", false)]
    [InlineData("mods/Reimagined/d2rloader/plugins/filter.json", false)]
    public void PlayerFilterExceptionHasAnExactModScopedPath(string path, bool expected)
        => Assert.Equal(expected, LadderRuntimeFileService.IsPlayerFilterPath(path));

    [Fact]
    public async Task InstallOrRepairWorksWhileTheModFolderIsOpenOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new[] { ("maps-config", "maps.toml", "ladder rule = true"u8.ToArray()),
            ("mod-data", "levels.txt", "mod data"u8.ToArray()) };
        var fixture = CreateBundle(files, files.Select(file => file.Item3).ToArray(), targetPaths: new Dictionary<string, string>
        { ["maps-config"] = "mods/Reimagined/d2rloader/config/maps.toml" });
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var mod = ModInstallationPaths.LadderModRoot(_installDirectory);
        using var handle = OpenFolderWithoutDeleteSharing(mod);
        Assert.False(handle.IsInvalid, "Could not reproduce an open-folder handle.");
        await File.WriteAllTextAsync(LadderConfigPath("maps.toml"), "old package content");
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.Equal("ladder rule = true", await File.ReadAllTextAsync(LadderConfigPath("maps.toml")));
        Assert.True(File.Exists(Path.Combine(mod, "ReimaginedLadder.mpq", "data", "test", "levels.txt")));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    [Fact]
    public async Task LockedPackageFileRollsBackEarlierChangesWhileTheFolderIsOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new[]
        {
            ("maps-config", "maps.toml", "old rules"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "old DLL"u8.ToArray())
        };
        var targets = new Dictionary<string, string> { ["maps-config"] = "mods/Reimagined/d2rloader/config/maps.toml" };
        var original = CreateBundle(files, files.Select(file => file.Item3).ToArray(), targetPaths: targets);
        await CreateService(original.Archive).InstallOrRepairAsync(_installDirectory, original.Descriptor);
        var mod = ModInstallationPaths.LadderModRoot(_installDirectory);
        var replacement = new[]
        {
            ("maps-config", "maps.toml", "new rules"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "new DLL"u8.ToArray())
        };
        var update = CreateBundle(replacement, replacement.Select(file => file.Item3).ToArray(), targetPaths: targets);
        using var directoryHandle = OpenFolderWithoutDeleteSharing(mod);
        using var lockedFile = new FileStream(Path.Combine(mod, "d2rloader", "plugins", "d2rl-maps.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.False(directoryHandle.IsInvalid);
        var failure = await Assert.ThrowsAsync<IOException>(() => CreateService(update.Archive).InstallOrRepairAsync(_installDirectory, update.Descriptor));
        Assert.Contains("previous files were restored", failure.Message);
        Assert.Equal("old rules", await File.ReadAllTextAsync(LadderConfigPath("maps.toml")));
        var readiness = await CreateService(original.Archive).GetReadinessAsync(_installDirectory, original.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_installDirectory, ".reimagined-launcher", "ladder-bundles", "backups"), "maps.toml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OpenSubfolderAllowsUpdateAndRemovalOfOldUndeclaredFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CreateConfigBundle();
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var folder = Path.Combine(ModInstallationPaths.LadderModRoot(_installDirectory), "obsolete");
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "stale.txt"); await File.WriteAllTextAsync(stale, "stale");
        using var directoryHandle = OpenFolderWithoutDeleteSharing(folder);
        Assert.False(directoryHandle.IsInvalid);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        Assert.False(File.Exists(stale));
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.True(readiness.IsReady, readiness.Status);
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenFolderWithoutDeleteSharing(string path)
        => CreateFile(path, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    private BundleFixture CreateConfigBundle()
    {
        var files = new[]
        {
            ("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()),
            ("maps", "d2rl-maps.dll", "map-code"u8.ToArray()),
            ("maps-config", "maps.toml", "ladder rule = true"u8.ToArray())
        };
        return CreateBundle(
            files,
            files.Select(file => file.Item3).ToArray(),
            targetPaths: new Dictionary<string, string> { ["maps-config"] = "mods/Reimagined/d2rloader/config/maps.toml" });
    }

    private string LadderConfigPath(string fileName)
        => Path.Combine(ModInstallationPaths.LadderModRoot(_installDirectory), "d2rloader", "config", fileName);

    [Fact]
    public async Task ReadinessRejectsFilesOutsideTheSignedManifest()
    {
        var fixture = CreateBundle(("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()));
        var service = CreateService(fixture.Archive);
        await service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor);
        var undeclared = Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "local-change.txt");
        await File.WriteAllTextAsync(undeclared, "not signed");

        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);

        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.Problems, problem => problem.Contains("is not declared"));
    }

    [Fact]
    public async Task LegacyPluginOnlyBundlesCannotUseLocalModFiles()
    {
        var files = new[] { ("announcements", "d2rl-announcements.dll", "announcement-code"u8.ToArray()) };
        var fixture = CreateBundle(files, files.Select(file => file.Item3).ToArray(), schemaVersion: 1);
        var service = CreateService(fixture.Archive);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallOrRepairAsync(_installDirectory, fixture.Descriptor));
        Assert.Equal(LadderBundleService.LegacyBundleMessage, exception.Message);
        var readiness = await service.GetReadinessAsync(_installDirectory, fixture.Descriptor);
        Assert.False(readiness.IsReady);
        Assert.False(readiness.CanRepair);
        Assert.False(Directory.Exists(ModInstallationPaths.LadderModRoot(_installDirectory)));
    }

    private LadderBundleService CreateService(byte[] archive)
    {
        // These fixtures declare no compatibility requirements, so the
        // prerequisite installers are never reached and never go near the
        // network. They exist here only to satisfy the constructor.
        var handler = new StaticResponseHandler(archive);
        return new LadderBundleService(
            new ReimaginedApiHttpClient(new HttpClient(handler)),
            new D2RLoaderInstallerService(new HttpClient(handler)));
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
