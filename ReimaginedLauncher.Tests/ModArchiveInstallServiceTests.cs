using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ModArchiveInstallServiceTests : IDisposable
{
    private const string ModInfo = """{"name":"Reimagined","version":"3.0.11","savepath":"Reimagined/"}""";

    private readonly string _root;
    private readonly string _installDirectory;
    private readonly string _modRoot;
    private readonly CascFastloadManifestService _manifestService;

    public ModArchiveInstallServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reimagined-mod-archive-tests", Guid.NewGuid().ToString("N"));
        _installDirectory = Path.Combine(_root, "game");
        _modRoot = Path.Combine(_installDirectory, "mods", "Reimagined", "Reimagined.mpq");
        Directory.CreateDirectory(_modRoot);
        _manifestService = new CascFastloadManifestService(InstallationType.BattleNet, _installDirectory);
    }

    [Fact]
    public async Task ASameLengthTsvEditIsWrittenAndDroppedFilesAreRemovedWithoutCasc()
    {
        WriteModFile(@"data\global\excel\armor.txt", "a\t0\n");
        WriteModFile(@"data\global\excel\dropped.txt", "old");
        WriteModFile(@"..\d2rloader\config.json", "user config");

        var zip = CreateArchive(new()
        {
            ["mods/Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo,
            ["mods/Reimagined/Reimagined.mpq/data/global/excel/armor.txt"] = "a\t1\n",
            ["mods/Reimagined/Reimagined.mpq/data/global/excel/added.txt"] = "new",
            ["mods/Reimagined/d2rloader/config.json"] = "shipped config"
        });

        await ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory);

        Assert.Equal("a\t1\n", ReadModFile(@"data\global\excel\armor.txt"));
        Assert.Equal("new", ReadModFile(@"data\global\excel\added.txt"));
        Assert.False(File.Exists(ModPath(@"data\global\excel\dropped.txt")));
        Assert.Equal("user config", ReadModFile(@"..\d2rloader\config.json"));
        Assert.False(_manifestService.Exists);
    }

    [Fact]
    public async Task CascVanillaFilesSurviveAndOnlyArchivePathsBecomeModOwned()
    {
        // vanilla.txt: untouched CASC file.
        // overridden.txt: the old mod's copy over a CASC file that the new version no longer ships.
        // patched.txt: a CASC file the new version overrides.
        WriteModFile(@"data\global\excel\vanilla.txt", "vanilla");
        WriteModFile(@"data\global\excel\overridden.txt", "old mod bytes");
        WriteModFile(@"data\global\excel\patched.txt", "vanilla patched");
        await SeedManifestAsync(
            CascEntry(@"data\global\excel\vanilla.txt", "vanilla", CascFastloadEntry.SourceTokens.Casc),
            CascEntry(@"data\global\excel\overridden.txt", "vanilla overridden", CascFastloadEntry.SourceTokens.CascAndMod),
            CascEntry(@"data\global\excel\patched.txt", "vanilla patched", CascFastloadEntry.SourceTokens.Casc));

        var zip = CreateArchive(new()
        {
            ["mods/Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo,
            ["mods/Reimagined/Reimagined.mpq/data/global/excel/patched.txt"] = "mod patched",
            ["mods/Reimagined/Reimagined.mpq/data/hd/global/ui/new.json"] = "mod only"
        });

        await ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory);

        var manifest = await _manifestService.LoadAsync();
        Assert.Equal("vanilla", ReadModFile(@"data\global\excel\vanilla.txt"));
        Assert.Equal(CascFastloadEntry.SourceTokens.Casc, Entry(manifest, @"data\global\excel\vanilla.txt").Source);

        Assert.False(File.Exists(ModPath(@"data\global\excel\overridden.txt")));
        Assert.Equal(CascFastloadEntry.SourceTokens.Casc, Entry(manifest, @"data\global\excel\overridden.txt").Source);

        Assert.Equal("mod patched", ReadModFile(@"data\global\excel\patched.txt"));
        var patched = Entry(manifest, @"data\global\excel\patched.txt");
        Assert.Equal(CascFastloadEntry.SourceTokens.CascAndMod, patched.Source);
        Assert.Equal(Md5("mod patched"), patched.ModMd5);
        Assert.Equal(Md5("vanilla patched"), patched.CascCKey);
        Assert.Equal("3.0.11", patched.ModVersion);

        var modOnly = Entry(manifest, @"data\hd\global\ui\new.json");
        Assert.Equal(CascFastloadEntry.SourceTokens.Mod, modOnly.Source);
        Assert.Equal(Md5("mod only"), modOnly.ModMd5);
        Assert.Null(modOnly.CascCKey);
    }

    [Fact]
    public async Task AWrappedReleaseArchiveInstallsOnlyItsModFolder()
    {
        var zip = CreateArchive(new()
        {
            ["D2R Reimagined 3.0.11/README.txt"] = "readme",
            ["D2R Reimagined 3.0.11/mods/Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo
        });

        await ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory);

        Assert.Equal(ModInfo, ReadModFile("modinfo.json"));
        Assert.False(Directory.Exists(Path.Combine(_installDirectory, "D2R Reimagined 3.0.11")));
    }

    [Fact]
    public async Task AnArchiveWithoutTheModFolderIsRejectedBeforeAnythingChanges()
    {
        WriteModFile(@"data\global\excel\armor.txt", "keep");
        var zip = CreateArchive(new() { ["Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory));

        Assert.Equal("keep", ReadModFile(@"data\global\excel\armor.txt"));
    }

    [Fact]
    public async Task ACaseOnlyRenameInTheArchiveLeavesExactlyTheShippedFile()
    {
        WriteModFile(@"data\global\excel\Armor.txt", "old");

        var zip = CreateArchive(new()
        {
            ["mods/Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo,
            ["mods/Reimagined/Reimagined.mpq/data/global/excel/armor.txt"] = "new"
        });

        await ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory);

        // Windows: one case-insensitive file overwritten in place. Linux: the stale "Armor.txt" is removed.
        var file = Assert.Single(Directory.GetFiles(ModPath(@"data\global\excel")));
        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public async Task AnOrphanPathThatEscapesTheModRootIsNeverDeleted()
    {
        WriteModFile(@"..\d2rloader\config.json", "user config");
        var orphans = new CascOrphanRecoveryService(new CascExtractionService(new NativeCascLib()), _manifestService);

        var result = await orphans.ReconcileRemovedPathsAsync(
            [@"..\d2rloader\config.json", @"data\..\..\d2rloader\config.json"],
            storage: null,
            destinationRoot: _modRoot);

        Assert.Equal(2, result.Failed);
        Assert.Equal("user config", ReadModFile(@"..\d2rloader\config.json"));
    }

    [LinuxFact]
    public async Task ALinuxFileNameContainingBackslashesIsNotTreatedAsANestedPath()
    {
        WriteModFile(@"..\d2rloader\config.json", "user config");
        // On Linux this is a single file directly inside Reimagined.mpq, not a path into d2rloader.
        var literal = Path.Combine(_modRoot, @"..\d2rloader\config.json");
        File.WriteAllText(literal, "stray");

        var zip = CreateArchive(new() { ["mods/Reimagined/Reimagined.mpq/modinfo.json"] = ModInfo });

        await ModArchiveInstallService.InstallAsync(zip, InstallationType.BattleNet, _installDirectory);

        Assert.Equal("user config", ReadModFile(@"..\d2rloader\config.json"));
        Assert.Equal("stray", File.ReadAllText(literal));
    }

    [Theory]
    [InlineData("mods/Reimagined/modinfo.json", "")]
    [InlineData("wrap/mods/Reimagined/modinfo.json", "wrap/")]
    [InlineData("mods/ReimaginedLadder/modinfo.json", null)]
    [InlineData("othermods/Reimagined/modinfo.json", null)]
    public void TheArchiveRootIsTheFolderHoldingModsReimagined(string entry, string? expected)
    {
        Assert.Equal(expected, ModArchiveInstallService.FindArchiveRoot([entry]));
    }

    private async Task SeedManifestAsync(params CascFastloadEntry[] entries)
    {
        await _manifestService.UpdateAsync(manifest => manifest.Files.AddRange(entries));
    }

    private static CascFastloadEntry CascEntry(string path, string vanillaContent, string source)
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

    private static CascFastloadEntry Entry(CascFastloadManifest manifest, string path) =>
        CascFastloadManifestService.FindEntry(manifest, path)
        ?? throw new Xunit.Sdk.XunitException($"No manifest entry for {path}.");

    private static string Md5(string content) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content)));

    // Test paths are written CASC-style (backslashes); map them to real nested directories on every OS.
    private string ModPath(string relative) =>
        Path.GetFullPath(Path.Combine(_modRoot, relative.Replace('\\', Path.DirectorySeparatorChar)));

    private void WriteModFile(string relative, string content)
    {
        var path = ModPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadModFile(string relative) => File.ReadAllText(ModPath(relative));

    private string CreateArchive(Dictionary<string, string> entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    public void Dispose()
    {
        if (File.Exists(_manifestService.ManifestPath))
        {
            File.Delete(_manifestService.ManifestPath);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
