using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LutrisServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-lutris-tests-{Guid.NewGuid():N}");

    private const string GameListJson = """
    [
      {
        "id": 35,
        "slug": "battlenet",
        "name": "Battle.net",
        "runner": "wine",
        "platform": "Windows",
        "year": 1996,
        "directory": "/home/player/Battle.net",
        "playtime": "1 day, 9:33:45.229751",
        "playtimeSeconds": 120825.229751,
        "lastplayed": "2026-07-20 20:57:33",
        "coverPath": null
      },
      {
        "id": 145,
        "slug": "diablo-ii-resurrected",
        "name": "Diablo II Resurrected",
        "runner": "wine",
        "platform": "Windows",
        "year": null,
        "directory": null,
        "playtime": "0:11:14.591577",
        "playtimeSeconds": 674.591577,
        "lastplayed": "2026-09-01 20:10:01",
        "coverPath": null
      }
    ]
    """;

    [Fact]
    public void ParseGameListReadsIdSlugAndName()
    {
        var games = LutrisService.ParseGameList(GameListJson);

        Assert.Equal(2, games.Count);
        var d2r = games.Single(game => game.Id == 145);
        Assert.Equal("diablo-ii-resurrected", d2r.Slug);
        Assert.Equal("Diablo II Resurrected", d2r.Name);
        Assert.Equal("wine", d2r.Runner);
        Assert.Equal("Windows", d2r.Platform);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"slug\":\"no-id\"}]")]
    public void ParseGameListReturnsEmptyForUnusableOutput(string json)
    {
        Assert.Empty(LutrisService.ParseGameList(json));
    }

    [Theory]
    [InlineData(1, "lutris:rungameid/1")]
    [InlineData(145, "lutris:rungameid/145")]
    public void BuildRunGameUriUsesTheNumericId(int gameId, string expected)
    {
        Assert.Equal(expected, LutrisService.BuildRunGameUri(gameId));
    }

    [Fact]
    public void ExtractGameExePathReadsPlainScalar()
    {
        const string yaml = """
        game:
          args: --offline
          desktop_integration: false
          exe: /home/player/Games/D2R/D2RLoader.exe
          prefix: /home/player/Games/D2R/pfx
        system:
          env:
            DXVK_HUD: compiler
        """;

        Assert.Equal("/home/player/Games/D2R/D2RLoader.exe", LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ExtractGameExePathJoinsFoldedContinuationLines()
    {
        // PyYAML wraps plain scalars at 80 columns, breaking only on spaces. The
        // stock install folder is "Diablo II Resurrected", so a long prefix path
        // hits this routinely; a naive line read would truncate the directory.
        const string yaml = """
        game:
          exe: /home/player/storage/SteamLibrary/steamapps/common/Diablo II Resurrected Special
            Edition/D2RLoader.exe
          prefix: /home/player/pfx
        """;

        Assert.Equal(
            "/home/player/storage/SteamLibrary/steamapps/common/Diablo II Resurrected Special Edition/D2RLoader.exe",
            LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ExtractGameExePathStopsAtTheNextKey()
    {
        const string yaml = """
        game:
          exe: /home/player/D2R/D2RLoader.exe
          prefix: /home/player/pfx
        """;

        Assert.DoesNotContain("pfx", LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ExtractGameExePathStripsQuotes()
    {
        const string yaml = """
        game:
          exe: '/home/player/Games/D2R: Special/D2RLoader.exe'
        """;

        Assert.Equal("/home/player/Games/D2R: Special/D2RLoader.exe", LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ExtractGameExePathIgnoresExeOutsideTheGameSection()
    {
        // Only game.exe is the launch target; a runner-level exe must not win.
        const string yaml = """
        wine:
          exe: /usr/bin/wine
        game:
          exe: /home/player/D2R/D2RLoader.exe
        """;

        Assert.Equal("/home/player/D2R/D2RLoader.exe", LutrisService.ExtractGameExePath(yaml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("game:\n  args: --offline\n")]
    [InlineData("game:\n  exe:\n")]
    [InlineData("game:\n  exe: ''\n")]
    [InlineData("system:\n  env:\n    A: b\n")]
    public void ExtractGameExePathReturnsNullWhenAbsent(string yaml)
    {
        Assert.Null(LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ResolveInstallDirectoryUsesTheNewestConfigForTheSlug()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        var installDirectory = Path.Combine(_testDirectory, "D2R");
        Directory.CreateDirectory(installDirectory);

        File.WriteAllText(
            Path.Combine(gamesDirectory, "diablo-ii-resurrected-1000000000.yml"),
            "game:\n  exe: /stale/D2RLoader.exe\n");
        File.WriteAllText(
            Path.Combine(gamesDirectory, "diablo-ii-resurrected-1788284282.yml"),
            $"game:\n  exe: {Path.Combine(installDirectory, "D2RLoader.exe")}\n");

        var resolved = LutrisService.ResolveInstallDirectory(gamesDirectory, "diablo-ii-resurrected");

        Assert.Equal(installDirectory, resolved?.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ResolveInstallDirectoryDoesNotMatchASlugPrefix()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        File.WriteAllText(
            Path.Combine(gamesDirectory, "diablo-ii-resurrected-ladder-1788284282.yml"),
            "game:\n  exe: /other/D2RLoader.exe\n");

        Assert.Null(LutrisService.ResolveInstallDirectory(gamesDirectory, "diablo-ii-resurrected"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc")]
    [InlineData("no-such-slug")]
    public void ResolveInstallDirectoryReturnsNullForUnusableSlugs(string? slug)
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);

        Assert.Null(LutrisService.ResolveInstallDirectory(gamesDirectory, slug));
    }

    [Fact]
    public void ExtractGamePrefixPathReadsThePrefixNotTheExe()
    {
        const string yaml = """
        game:
          args: --offline
          exe: /home/player/Games/D2R/D2RLoader.exe
          prefix: /home/player/Games/D2R/pfx
        """;

        Assert.Equal("/home/player/Games/D2R/pfx", LutrisService.ExtractGamePrefixPath(yaml));
        Assert.Equal("/home/player/Games/D2R/D2RLoader.exe", LutrisService.ExtractGameExePath(yaml));
    }

    [Fact]
    public void ExtractGamePrefixPathJoinsFoldedContinuationLines()
    {
        const string yaml = """
        game:
          exe: /home/player/D2R/D2RLoader.exe
          prefix: /home/player/storage/SteamLibrary/steamapps/common/Diablo II Resurrected Special
            Edition/pfx
        """;

        Assert.Equal(
            "/home/player/storage/SteamLibrary/steamapps/common/Diablo II Resurrected Special Edition/pfx",
            LutrisService.ExtractGamePrefixPath(yaml));
    }

    [Fact]
    public void ExtractGamePrefixPathReturnsNullWhenAbsent()
    {
        Assert.Null(LutrisService.ExtractGamePrefixPath("game:\n  exe: /home/player/D2R/D2RLoader.exe\n"));
    }

    [Fact]
    public void ResolveWinePrefixReadsTheConfiguredPrefixVerbatim()
    {
        // The prefix sits beside the game, not above it, which is exactly why it
        // cannot be found by walking up from the install directory.
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        File.WriteAllText(
            Path.Combine(gamesDirectory, "diablo-ii-resurrected-1788284282.yml"),
            "game:\n  exe: /home/player/Games/D2R/D2RLoader.exe\n  prefix: /home/player/Games/D2R/pfx\n");

        Assert.Equal(
            "/home/player/Games/D2R/pfx",
            LutrisService.ResolveWinePrefix(gamesDirectory, "diablo-ii-resurrected"));
    }

    [Fact]
    public void ResolveWinePrefixReturnsNullWhenTheConfigHasNoPrefix()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        File.WriteAllText(
            Path.Combine(gamesDirectory, "diablo-ii-resurrected-1788284282.yml"),
            "game:\n  exe: /home/player/Games/D2R/D2RLoader.exe\n");

        Assert.Null(LutrisService.ResolveWinePrefix(gamesDirectory, "diablo-ii-resurrected"));
    }

    // Real process chain observed for a Lutris launch. D2R.exe appears nowhere:
    // Lutris runs D2RLoader.exe, and wine rewrites the path to a drive letter.
    private static readonly (int Pid, string CommandLine)[] LutrisProcessChain =
    [
        (1777909, "python3 /usr/share/lutris/bin/lutris-wrapper Diablo II Resurrected 0 0 game-performance mangohud /usr/bin/umu-run /home/player/Games/D2R/D2RLoader.exe"),
        (1777937, "/usr/bin/python3 /usr/bin/umu-run /home/player/Games/D2R/D2RLoader.exe --offline"),
        (1777944, "python3 .../proton waitforexitandrun /home/player/Games/D2R/D2RLoader.exe --offline"),
        (1778084, @"X:\Games\D2R\D2RLoader.exe --offline -mod Reimagined"),
        (1000, "/usr/bin/lutris lutris:rungameid/145"),
        (1001, "/usr/lib/firefox/firefox")
    ];

    private string WriteFakeProc()
    {
        var procRoot = Path.Combine(_testDirectory, "proc");
        foreach (var (pid, commandLine) in LutrisProcessChain)
        {
            var directory = Path.Combine(procRoot, pid.ToString());
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "cmdline"),
                commandLine.Replace(' ', '\0'));
        }

        return procRoot;
    }

    [Fact]
    public void FindGameProcessIdPicksTheOutermostWrapperInTheChain()
    {
        var procRoot = WriteFakeProc();

        var pid = LutrisService.FindGameProcessId(procRoot, "/home/player/Games/D2R/D2RLoader.exe");

        // The lowest matching pid is the lutris-wrapper, whose lifetime is the
        // whole session - not the lutris client (1000), which never matches.
        Assert.Equal(1777909, pid);
    }

    [Fact]
    public void FindGameProcessIdMatchesTheWineDriveLetterPathByFileName()
    {
        var procRoot = Path.Combine(_testDirectory, "proc");
        Directory.CreateDirectory(Path.Combine(procRoot, "4242"));
        File.WriteAllText(
            Path.Combine(procRoot, "4242", "cmdline"),
            @"X:\Games\D2R\D2RLoader.exe --offline".Replace(' ', '\0'));

        Assert.Equal(4242, LutrisService.FindGameProcessId(procRoot, "/home/player/Games/D2R/D2RLoader.exe"));
    }

    [Fact]
    public void FindGameProcessIdDoesNotMatchTheLutrisClientItself()
    {
        // Watching the lutris process is what made the launcher hang in the tray:
        // it can outlive the game or exit immediately after handing off.
        var procRoot = Path.Combine(_testDirectory, "proc");
        Directory.CreateDirectory(Path.Combine(procRoot, "1000"));
        File.WriteAllText(
            Path.Combine(procRoot, "1000", "cmdline"),
            "/usr/bin/lutris lutris:rungameid/145".Replace(' ', '\0'));

        Assert.Null(LutrisService.FindGameProcessId(procRoot, "/home/player/Games/D2R/D2RLoader.exe"));
    }

    [Fact]
    public void FindGameProcessIdDoesNotConfuseD2RLoaderWithD2R()
    {
        // "D2RLoader.exe" does not contain "D2R.exe" - the mismatch that stopped
        // the watcher finding anything at all.
        var procRoot = WriteFakeProc();

        Assert.Null(LutrisService.FindGameProcessId(procRoot, "/home/player/Games/D2R/D2R.exe"));
    }

    [Fact]
    public void FindGameProcessIdIgnoresUnrelatedProcesses()
    {
        var procRoot = WriteFakeProc();

        Assert.Null(LutrisService.FindGameProcessId(procRoot, "/home/player/Games/Barony/Barony.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindGameProcessIdReturnsNullWithoutAnExePath(string? exePath)
    {
        Assert.Null(LutrisService.FindGameProcessId(WriteFakeProc(), exePath));
    }

    [Fact]
    public void FindGameProcessIdToleratesAMissingProcRoot()
    {
        Assert.Null(LutrisService.FindGameProcessId(
            Path.Combine(_testDirectory, "absent"),
            "/home/player/Games/D2R/D2RLoader.exe"));
    }

    [Fact]
    public async Task WaitForGameSessionGivesUpRatherThanHangingWhenNothingAppears()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var watched = await LutrisService.WaitForGameSessionAsync(
            "/home/player/Games/D2R/NeverRunning.exe",
            TimeSpan.FromSeconds(2));

        Assert.False(watched);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task WaitForGameSessionReturnsFalseWithoutAnExePath()
    {
        Assert.False(await LutrisService.WaitForGameSessionAsync(null, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ResolveInstallDirectoryReturnsNullWhenTheDirectoryIsMissing()
    {
        Assert.Null(LutrisService.ResolveInstallDirectory(
            Path.Combine(_testDirectory, "absent"),
            "diablo-ii-resurrected"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
