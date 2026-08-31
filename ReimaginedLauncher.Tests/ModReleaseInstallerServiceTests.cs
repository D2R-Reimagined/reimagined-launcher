using ReimaginedLauncher.Utilities;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ModReleaseInstallerServiceTests : IDisposable
{
    private readonly string _installDirectory;

    public ModReleaseInstallerServiceTests()
    {
        _installDirectory = Path.Combine(
            Path.GetTempPath(),
            "reimagined-mod-release-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_installDirectory);
        File.WriteAllBytes(Path.Combine(_installDirectory, "D2R.exe"), [0x4D, 0x5A]);
    }

    /// <summary>
    /// The shape below is copied from the live releases API. Deserialization is
    /// what silently breaks when GitHub renames a field, and a wrong
    /// browser_download_url turns into a confusing runtime failure rather than a
    /// build error, so the mapping is pinned here.
    /// </summary>
    [Fact]
    public async Task TheRequiredVersionIsInstalledFromItsTaggedGitHubRelease()
    {
        var archive = CreateModArchive("3.0.10");
        var handler = new GitHubReleaseHandler(ReleasesJson("v3.0.10", "D2R.Reimagined.-.3.0.10.zip", archive), archive);
        var service = new ModReleaseInstallerService(new HttpClient(handler));

        await service.InstallAsync(_installDirectory, "3.0.10");

        Assert.Equal("3.0.10", ModReleaseInstallerService.ReadInstalledVersion(_installDirectory));
        Assert.Contains("/releases/download/v3.0.10/", handler.LastDownloadUrl);
    }

    [Fact]
    public async Task AVersionWithNoPublishedReleaseFailsWithAnActionableMessage()
    {
        var archive = CreateModArchive("3.0.10");
        var handler = new GitHubReleaseHandler(ReleasesJson("v3.0.10", "D2R.Reimagined.-.3.0.10.zip", archive), archive);
        var service = new ModReleaseInstallerService(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(_installDirectory, "3.0.11"));

        Assert.Contains("3.0.11", exception.Message);
    }

    [Fact]
    public async Task AnArchiveThatDoesNotMatchItsPublishedDigestIsRejected()
    {
        var archive = CreateModArchive("3.0.10");
        var json = ReleasesJson("v3.0.10", "D2R.Reimagined.-.3.0.10.zip", archive)
            .Replace(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), new string('a', 64));
        var service = new ModReleaseInstallerService(new HttpClient(new GitHubReleaseHandler(json, archive)));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.InstallAsync(_installDirectory, "3.0.10"));

        Assert.Null(ModReleaseInstallerService.ReadInstalledVersion(_installDirectory));
    }

    private static string ReleasesJson(string tag, string assetName, byte[] archive)
    {
        var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        return $$"""
        [
          {
            "tag_name": "{{tag}}",
            "assets": [
              {
                "name": "{{assetName}}",
                "size": {{archive.Length}},
                "digest": "sha256:{{digest}}",
                "content_type": "application/x-zip-compressed",
                "browser_download_url": "https://github.com/D2R-Reimagined/d2r-reimagined-mod/releases/download/{{tag}}/{{assetName}}"
              }
            ]
          }
        ]
        """;
    }

    private static byte[] CreateModArchive(string version)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("mods/Reimagined/Reimagined.mpq/modinfo.json");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(
                $$"""{"name":"Reimagined","version":"{{version}}","savepath":"Reimagined/"}"""));
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }

    private sealed class GitHubReleaseHandler(string releasesJson, byte[] archive) : HttpMessageHandler
    {
        public string LastDownloadUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/releases?", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releasesJson, Encoding.UTF8, "application/json")
                });
            }

            LastDownloadUrl = url;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
        }
    }
}
