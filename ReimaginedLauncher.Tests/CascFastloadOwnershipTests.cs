using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class CascFastloadOwnershipTests : IDisposable
{
    private readonly string _installDirectory;
    private readonly string _modRoot;
    private readonly CascFastloadManifestService _manifestService;

    public CascFastloadOwnershipTests()
    {
        _installDirectory = Path.Combine(Path.GetTempPath(), "reimagined-casc-ownership-tests", Guid.NewGuid().ToString("N"));
        _modRoot = Path.Combine(_installDirectory, "mods", "Reimagined", "Reimagined.mpq");
        Directory.CreateDirectory(_modRoot);
        _manifestService = new CascFastloadManifestService(InstallationType.Steam, _installDirectory);
    }

    [Fact]
    public async Task ADeltaUpdateKeepsModBytesAndOnlyRefreshesTheCascFingerprint()
    {
        WriteFile(@"data\global\excel\armor.txt", "mod armor");
        WriteFile(@"data\global\excel\weapons.txt", "old vanilla weapons");
        await _manifestService.UpdateAsync(m =>
        {
            m.Files.Add(Entry(@"data\global\excel\armor.txt", "old vanilla armor", CascFastloadEntry.SourceTokens.CascAndMod));
            m.Files.Add(Entry(@"data\global\excel\weapons.txt", "old vanilla weapons", CascFastloadEntry.SourceTokens.Casc));
        });

        var native = new FakeCascNative(new Dictionary<string, string>
        {
            [@"data\global\excel\armor.txt"] = "new vanilla armor",
            [@"data\global\excel\weapons.txt"] = "new vanilla weapons"
        });
        var delta = new CascDeltaService(new CascExtractionService(native), _manifestService);
        var plan = delta.Plan(native.Entries, await _manifestService.LoadAsync(), _modRoot);
        Assert.Equal(2, plan.Updated.Count);

        using var storage = new SafeCascStorageHandle();
        await delta.ApplyAsync(storage, plan, null, _modRoot, new CascStorageProduct("d2r", 2));

        Assert.Equal("mod armor", ReadFile(@"data\global\excel\armor.txt"));
        Assert.Equal("new vanilla weapons", ReadFile(@"data\global\excel\weapons.txt"));
        Assert.DoesNotContain(@"data\global\excel\armor.txt", native.Extracted);

        var manifest = await _manifestService.LoadAsync();
        var armor = CascFastloadManifestService.FindEntry(manifest, @"data\global\excel\armor.txt")!;
        Assert.Equal(CascFastloadEntry.SourceTokens.CascAndMod, armor.Source);
        Assert.Equal(Md5("new vanilla armor"), armor.CKey);
        Assert.Equal(Md5("new vanilla armor"), armor.CascCKey);
        Assert.True(delta.Plan(native.Entries, manifest, _modRoot).IsNoOp);
    }

    [Fact]
    public void ModOnlyEntriesWithoutACascCounterpartAreNotPlannedForRemoval()
    {
        WriteFile(@"data\hd\global\ui\new.json", "mod only");
        var manifest = new CascFastloadManifest();
        manifest.Files.Add(new CascFastloadEntry
        {
            Path = @"data\hd\global\ui\new.json",
            Size = 8,
            Source = CascFastloadEntry.SourceTokens.Mod,
            ModMd5 = Md5("mod only")
        });

        var delta = new CascDeltaService(new CascExtractionService(new FakeCascNative(new())), _manifestService);

        Assert.True(delta.Plan([], manifest, _modRoot).IsNoOp);
    }

    [Fact]
    public async Task ResetCatchesSameLengthEditsInVanillaFilesAndReportsChangedModFiles()
    {
        WriteFile(@"data\global\excel\levels.txt", "a\t1\n");
        WriteFile(@"data\global\excel\monstats.txt", "vanilla");
        WriteFile(@"data\global\excel\armor.txt", "mod armor edited");
        await _manifestService.UpdateAsync(m =>
        {
            m.Files.Add(Entry(@"data\global\excel\levels.txt", "a\t0\n", CascFastloadEntry.SourceTokens.Casc));
            m.Files.Add(Entry(@"data\global\excel\monstats.txt", "vanilla", CascFastloadEntry.SourceTokens.Casc));
            var armor = Entry(@"data\global\excel\armor.txt", "vanilla armor", CascFastloadEntry.SourceTokens.CascAndMod);
            armor.ModMd5 = Md5("mod armor");
            m.Files.Add(armor);
        });

        var result = await new CascResetService(_manifestService).ResetAsync(_modRoot);

        Assert.Equal(1, result.MismatchedDeleted);
        Assert.Equal(1, result.OverlayMismatchesIgnored);
        Assert.False(File.Exists(FilePath(@"data\global\excel\levels.txt")));
        Assert.True(File.Exists(FilePath(@"data\global\excel\monstats.txt")));
        Assert.Equal("mod armor edited", ReadFile(@"data\global\excel\armor.txt"));
    }

    private static CascFastloadEntry Entry(string path, string vanillaContent, string source)
    {
        var ckey = Md5(vanillaContent);
        return new CascFastloadEntry
        {
            Path = path,
            CKey = ckey,
            CascCKey = ckey,
            Size = Encoding.UTF8.GetByteCount(vanillaContent),
            Source = source
        };
    }

    private static string Md5(string content) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content)));

    private string FilePath(string relative) => Path.Combine(_modRoot, relative);

    private void WriteFile(string relative, string content)
    {
        var path = FilePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadFile(string relative) => File.ReadAllText(FilePath(relative));

    public void Dispose()
    {
        if (File.Exists(_manifestService.ManifestPath))
        {
            File.Delete(_manifestService.ManifestPath);
        }

        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }

    private sealed class FakeCascNative(Dictionary<string, string> files) : ICascNative
    {
        public List<CascFileEntry> Entries { get; } = files
            .Select(file => new CascFileEntry(
                file.Key,
                MD5.HashData(Encoding.UTF8.GetBytes(file.Value)),
                [],
                (ulong)Encoding.UTF8.GetByteCount(file.Value),
                0,
                0,
                0))
            .ToList();

        public List<string> Extracted { get; } = [];

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public SafeCascStorageHandle? OpenStorage(string storagePath, uint localeMask = CascLocale.All) => null;

        public CascStorageProduct? GetStorageProduct(SafeCascStorageHandle storage) => null;

        public IEnumerable<CascFileEntry> EnumerateFiles(SafeCascStorageHandle storage) => Entries;

        public long ExtractTo(SafeCascStorageHandle storage, string cascPath, Stream destination, byte[]? buffer = null)
        {
            Extracted.Add(cascPath);
            var bytes = Encoding.UTF8.GetBytes(files[cascPath]);
            destination.Write(bytes);
            return bytes.Length;
        }
    }
}
