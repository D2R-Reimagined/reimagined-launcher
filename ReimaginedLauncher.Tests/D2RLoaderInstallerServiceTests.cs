using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
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
