using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class D2RLoaderInstallerServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-launcher-installer-tests-{Guid.NewGuid():N}");

    [Fact]
    public void InstallerMergesArchiveAndPreservesExistingUserConfiguration()
    {
        var archivePath = CreateArchive(
            ("D2RLoader.exe", "loader"),
            ("D2RCore.dll", "new-core"),
            ("d2rloader.mpq", "mpq"),
            ("d2rloader/config/d2rloader.toml", "default-config"),
            ("d2rloader/data/runtime.bin", "runtime"));
        var stagingDirectory = Path.Combine(_testDirectory, "staging");
        var installDirectory = Path.Combine(_testDirectory, "install");
        Directory.CreateDirectory(Path.Combine(installDirectory, "d2rloader", "config"));
        File.WriteAllText(Path.Combine(installDirectory, "D2RCore.dll"), "old-core");
        File.WriteAllText(
            Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml"),
            "custom-config");

        D2RLoaderInstallerService.ExtractArchive(archivePath, stagingDirectory);
        D2RLoaderInstallerService.InstallExtractedFiles(stagingDirectory, installDirectory);

        Assert.Equal("loader", File.ReadAllText(Path.Combine(installDirectory, "D2RLoader.exe")));
        Assert.Equal("new-core", File.ReadAllText(Path.Combine(installDirectory, "D2RCore.dll")));
        Assert.Equal(
            "custom-config",
            File.ReadAllText(Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml")));
        Assert.Equal(
            "runtime",
            File.ReadAllText(Path.Combine(installDirectory, "d2rloader", "data", "runtime.bin")));
    }

    [Fact]
    public void ExtractArchiveRejectsPathsOutsideTheStagingDirectory()
    {
        var archivePath = CreateArchive(
            ("D2RLoader.exe", "loader"),
            ("../outside.txt", "unsafe"));
        var stagingDirectory = Path.Combine(_testDirectory, "staging");

        var exception = Assert.Throws<InvalidDataException>(() =>
            D2RLoaderInstallerService.ExtractArchive(archivePath, stagingDirectory));

        Assert.Contains("unsafe path", exception.Message);
        Assert.False(File.Exists(Path.Combine(_testDirectory, "outside.txt")));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.1-beta", true)]
    [InlineData("1.2.1-alpha.2", "1.2.1-beta", true)]
    [InlineData("1.2.1-beta", "1.2.1-beta", false)]
    [InlineData("1.2.1", "1.2.1-beta", false)]
    [InlineData("1.2.1.0", "1.2.1", false)]
    [InlineData("1.2.1-beta+preview.1", "1.2.1-beta+preview.2", false)]
    public void UpdateComparisonUsesSemanticVersionPrecedence(
        string installed,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, D2RLoaderInstallerService.IsUpdateAvailable(installed, latest));
    }

    [Fact]
    public async Task UpdateCheckReadsPublishedVersionFromD2RLoaderEndpoint()
    {
        var installDirectory = Path.Combine(_testDirectory, "install");
        Directory.CreateDirectory(installDirectory);
        File.Copy(
            typeof(D2RLoaderInstallerServiceTests).Assembly.Location,
            Path.Combine(installDirectory, "D2RLoader.exe"));
        var handler = new StaticResponseHandler("{\"version\":\"999.0.0-beta\"}");
        var service = new D2RLoaderInstallerService(new HttpClient(handler));

        var result = await service.CheckForUpdateAsync(installDirectory);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("999.0.0-beta", result.LatestVersion);
        Assert.Equal(new Uri("https://d2rloader.net/api/v1/version"), handler.RequestUri);
    }

    [Fact]
    public async Task PinnedInstallReplacesANewerLoaderAndKeepsUserConfiguration()
    {
        var archivePath = CreateArchive(
            ("D2RLoader.exe", "pinned-loader"),
            ("D2RCore.dll", "pinned-core"),
            ("d2rloader.mpq", "pinned-mpq"),
            ("d2rloader/config/d2rloader.toml", "default-config"));
        var installDirectory = CreateInstallDirectory();
        File.WriteAllText(Path.Combine(installDirectory, "D2RLoader.exe"), "newer-loader");
        File.WriteAllText(Path.Combine(installDirectory, "D2RCore.dll"), "newer-core");
        File.WriteAllText(Path.Combine(installDirectory, "d2rloader.mpq"), "newer-mpq");
        Directory.CreateDirectory(Path.Combine(installDirectory, "d2rloader", "config"));
        File.WriteAllText(Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml"), "custom-config");
        var pin = Pin(archivePath, ("D2RLoader.exe", "pinned-loader"), ("D2RCore.dll", "pinned-core"), ("d2rloader.mpq", "pinned-mpq"));
        Assert.Equal(3, (await D2RLoaderInstallerService.GetPinnedMismatchesAsync(installDirectory, pin)).Count);

        await new D2RLoaderInstallerService(new HttpClient()).InstallPinnedAsync(
            installDirectory, pin, (path, _) => { File.Copy(archivePath, path); return Task.CompletedTask; });

        Assert.Empty(await D2RLoaderInstallerService.GetPinnedMismatchesAsync(installDirectory, pin));
        Assert.Equal("pinned-loader", File.ReadAllText(Path.Combine(installDirectory, "D2RLoader.exe")));
        Assert.Equal("custom-config", File.ReadAllText(Path.Combine(installDirectory, "d2rloader", "config", "d2rloader.toml")));
    }

    [Fact]
    public async Task PinnedInstallRefusesAnArchiveWhoseFilesDifferFromThePin()
    {
        var archivePath = CreateArchive(
            ("D2RLoader.exe", "other-loader"),
            ("D2RCore.dll", "pinned-core"),
            ("d2rloader.mpq", "pinned-mpq"));
        var installDirectory = CreateInstallDirectory();
        File.WriteAllText(Path.Combine(installDirectory, "D2RLoader.exe"), "current-loader");
        var pin = Pin(archivePath, ("D2RLoader.exe", "pinned-loader"), ("D2RCore.dll", "pinned-core"), ("d2rloader.mpq", "pinned-mpq"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new D2RLoaderInstallerService(new HttpClient()).InstallPinnedAsync(
                installDirectory, pin, (path, _) => { File.Copy(archivePath, path); return Task.CompletedTask; }));

        Assert.Contains("does not contain the files", exception.Message);
        Assert.Equal("current-loader", File.ReadAllText(Path.Combine(installDirectory, "D2RLoader.exe")));
    }

    [Theory]
    [InlineData("../D2RLoader.exe")]
    [InlineData("d2rloader/D2RLoader.exe")]
    [InlineData("D2RLoader2.exe")]
    public void PinsMustNameExactlyTheThreeRootFiles(string loaderPath)
    {
        var valid = Pin(CreateArchive(("x", "x")), ("D2RLoader.exe", "a"), ("D2RCore.dll", "b"), ("d2rloader.mpq", "c"));
        Assert.True(D2RLoaderInstallerService.IsValidPin(valid));
        var renamed = valid with { Files = [valid.Files[0] with { Path = loaderPath }, valid.Files[1], valid.Files[2]] };
        Assert.False(D2RLoaderInstallerService.IsValidPin(renamed));
        Assert.False(D2RLoaderInstallerService.IsValidPin(valid with { Files = valid.Files.Take(2).ToArray() }));
    }

    [Fact]
    public async Task PinnedReimaginedLoaderInstallPlacesAllFourFilesBesideD2RLoader()
    {
        var hostBytes = PackedHostBytes();
        var archivePath = CreateBinaryArchive(
            ("ReimaginedHost.exe", hostBytes),
            ("D2RRCore.dll", "MZcore"u8.ToArray()),
            ("ReimaginedHostRunner.dll", "MZrunner"u8.ToArray()),
            ("ReimaginedRuntime.dll", "MZruntime"u8.ToArray()));
        var installDirectory = CreateInstallDirectory();
        File.WriteAllText(Path.Combine(installDirectory, "D2RLoader.exe"), "existing-d2rloader");
        File.WriteAllText(Path.Combine(installDirectory, "D2RCore.dll"), "existing-d2rcore");
        var pin = ReimaginedPin(archivePath, hostBytes, "MZcore"u8.ToArray(), "MZrunner"u8.ToArray(), "MZruntime"u8.ToArray());

        Assert.True(D2RLoaderInstallerService.IsValidPin(pin, LadderLoaderType.ReimaginedLoader));
        Assert.False(D2RLoaderInstallerService.IsValidPin(pin, LadderLoaderType.D2RLoader));
        Assert.Equal(4, (await D2RLoaderInstallerService.GetPinnedMismatchesAsync(installDirectory, pin)).Count);

        await new D2RLoaderInstallerService(new HttpClient()).InstallPinnedAsync(
            installDirectory,
            pin,
            (path, _) => { File.Copy(archivePath, path); return Task.CompletedTask; },
            expectedKind: LadderLoaderType.ReimaginedLoader);

        Assert.Empty(await D2RLoaderInstallerService.GetPinnedMismatchesAsync(installDirectory, pin));
        Assert.Equal("existing-d2rloader", File.ReadAllText(Path.Combine(installDirectory, "D2RLoader.exe")));
        Assert.Equal("existing-d2rcore", File.ReadAllText(Path.Combine(installDirectory, "D2RCore.dll")));
        Assert.Null(LocalD2rdsRigService.ReimaginedLoaderProblem(installDirectory));
    }

    [Fact]
    public void ReimaginedLoaderPinRejectsUnpackedHostStubAndMissingFiles()
    {
        var stubBytes = "MZstub"u8.ToArray();
        var archivePath = CreateBinaryArchive(
            ("ReimaginedHost.exe", stubBytes),
            ("D2RRCore.dll", "MZcore"u8.ToArray()),
            ("ReimaginedHostRunner.dll", "MZrunner"u8.ToArray()),
            ("ReimaginedRuntime.dll", "MZruntime"u8.ToArray()));
        var stubPin = ReimaginedPin(archivePath, stubBytes, "MZcore"u8.ToArray(), "MZrunner"u8.ToArray(), "MZruntime"u8.ToArray());

        Assert.False(D2RLoaderInstallerService.IsValidPin(stubPin, LadderLoaderType.ReimaginedLoader));

        var hostBytes = PackedHostBytes();
        var validPin = ReimaginedPin(archivePath, hostBytes, "MZcore"u8.ToArray(), "MZrunner"u8.ToArray(), "MZruntime"u8.ToArray());
        Assert.False(D2RLoaderInstallerService.IsValidPin(validPin with { Files = validPin.Files.Take(3).ToArray() }, LadderLoaderType.ReimaginedLoader));
    }

    [Fact]
    public async Task LobbyBridgeConfigSwitchesBetweenBattleNetAndD2RLoaderModes()
    {
        var installDirectory = CreateInstallDirectory();
        var pluginsDir = Path.Combine(installDirectory, "mods", ModInstallationPaths.LadderModName, "d2rloader", "plugins");
        var configPath = Path.Combine(installDirectory, "mods", ModInstallationPaths.LadderModName, "d2rloader", "config", LobbyBridgeConfigService.ConfigFileName);
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, LobbyBridgeConfigService.PluginFileName), "dll");

        await LobbyBridgeConfigService.ConfigureForLadderAsync(installDirectory, LadderLoaderType.ReimaginedLoader);
        Assert.Contains("lobby_mode = \"battlenet\"", File.ReadAllText(configPath));

        await LobbyBridgeConfigService.ConfigureForLadderAsync(installDirectory, LadderLoaderType.D2RLoader);
        var updated = File.ReadAllText(configPath);
        Assert.Contains("lobby_mode = \"d2rloader\"", updated);
        Assert.DoesNotContain("lobby_mode = \"battlenet\"", updated);
    }

    [Fact]
    public void BuildReimaginedLoaderArgumentsFormatsWindowsAndLinuxForms()
    {
        Assert.Equal(
            "--game-dir \"C:\\Games\\D2R\" -- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments(@"C:\Games\D2R", "-mod ReimaginedLadder -txt", includeGameDir: true));
        Assert.Equal(
            "--game-dir \"C:\\Program Files (x86)\\Diablo II Resurrected\" -- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments(@"C:\Program Files (x86)\Diablo II Resurrected\", "-mod ReimaginedLadder -txt", includeGameDir: true));
        Assert.Equal(
            "--game-dir \"C:\\Games\\D2R\" -- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments(@"C:\Games\D2R\\", "-mod ReimaginedLadder -txt", includeGameDir: true));
        Assert.Equal(
            "--game-dir \"C:\\\\\" -- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments(@"C:\", "-mod ReimaginedLadder -txt", includeGameDir: true));
        Assert.Equal(
            "--game-dir \"/games/d2r\" -- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments("/games/d2r/", "-mod ReimaginedLadder -txt", includeGameDir: true));
        Assert.Equal(
            "-- -mod ReimaginedLadder -txt",
            GameLauncherService.BuildReimaginedLoaderArguments("/games/d2r", "-mod ReimaginedLadder -txt", includeGameDir: false));
    }

    private static byte[] PackedHostBytes()
    {
        var bytes = new byte[LocalD2rdsRigService.PackedHostMinimumBytes];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static LadderD2RLoaderResponse ReimaginedPin(
        string archivePath, byte[] host, byte[] core, byte[] runner, byte[] runtime) => new(
        "0.1.0-2026-11-23-abc1234",
        "ReimaginedLoader-0.1.0-2026-11-23-abc1234.zip",
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))),
        new FileInfo(archivePath).Length,
        [
            new("ReimaginedHost.exe", Convert.ToHexString(SHA256.HashData(host)), host.Length),
            new("D2RRCore.dll", Convert.ToHexString(SHA256.HashData(core)), core.Length),
            new("ReimaginedHostRunner.dll", Convert.ToHexString(SHA256.HashData(runner)), runner.Length),
            new("ReimaginedRuntime.dll", Convert.ToHexString(SHA256.HashData(runtime)), runtime.Length)
        ],
        DateTimeOffset.UtcNow,
        "/ladders/id/loader/download",
        LadderLoaderType.ReimaginedLoader);

    private string CreateBinaryArchive(params (string Path, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(_testDirectory);
        var archivePath = Path.Combine(_testDirectory, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return archivePath;
    }

    private string CreateInstallDirectory()
    {
        var installDirectory = Path.Combine(_testDirectory, "install");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "D2R.exe"), "game");
        return installDirectory;
    }

    private static LadderD2RLoaderResponse Pin(string archivePath, params (string Path, string Content)[] files) => new(
        "1.3.1-beta",
        "D2RLoader-1.3.1-beta.zip",
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))),
        new FileInfo(archivePath).Length,
        files.Select(file => new LadderD2RLoaderFileResponse(
            file.Path,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))),
            Encoding.UTF8.GetByteCount(file.Content))).ToArray(),
        DateTimeOffset.UtcNow,
        "/ladders/id/d2rloader/download");

    private string CreateArchive(params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_testDirectory);
        var archivePath = Path.Combine(_testDirectory, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return archivePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
