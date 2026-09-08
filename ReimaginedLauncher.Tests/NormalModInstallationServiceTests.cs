using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

[Collection("Ladder bundle signing")]
public sealed class NormalModInstallationServiceTests : IDisposable
{
    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        "reimagined-normal-mod-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LeavingLadderRestoresTheCompleteNexusFolder()
    {
        WriteMod("NexusSaves", ("normal-only.txt", "normal"));
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);
        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);

        ReplaceActiveMod("NexusSaves-Test-Ladder-f2faf859", ("ladder-only.txt", "ladder"));
        Directory.CreateDirectory(Path.GetDirectoryName(NormalModInstallationService.BundleStatePath(_installDirectory))!);
        File.WriteAllText(NormalModInstallationService.BundleStatePath(_installDirectory), "{}");

        NormalModInstallationService.Restore(_installDirectory);

        var active = ActiveModRoot();
        Assert.Equal("normal", File.ReadAllText(Path.Combine(active, "normal-only.txt")));
        Assert.False(File.Exists(Path.Combine(active, "ladder-only.txt")));
        Assert.Contains("NexusSaves/", File.ReadAllText(Path.Combine(active, "Reimagined.mpq", "modinfo.json")));
        Assert.False(NormalModInstallationService.HasLadderInstallation(_installDirectory));
    }

    [Fact]
    public void ARedirectedLegacyInstallIsNeverCapturedAsNormal()
    {
        WriteMod("NexusSaves-Test-Ladder-f2faf859");

        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);

        var exception = Assert.Throws<InvalidDataException>(
            () => NormalModInstallationService.Restore(_installDirectory));
        Assert.Equal(NormalModInstallationService.RecoveryMessage, exception.Message);
        Assert.False(Directory.Exists(NormalModInstallationService.NormalModRoot(_installDirectory)));
    }

    [Fact]
    public async Task RuntimeBaselineCannotOverrideTheNexusSavePath()
    {
        WriteMod("NexusSaves");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);
        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);
        ReplaceActiveMod("NexusSaves-Test-Ladder-f2faf859");

        var modInfo = Path.Combine(ActiveModRoot(), "Reimagined.mpq", "modinfo.json");
        var baseline = LadderRuntimeFileService.GetBaselinePath(_installDirectory, modInfo);
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "{\"savepath\":\"Wrong-Ladder-aaaaaaaa/\"}");

        Assert.True(await LadderSaveDirectoryService.RestoreAsync(_installDirectory));
        Assert.Contains("NexusSaves/", File.ReadAllText(modInfo));
    }

    private string ActiveModRoot() => Path.Combine(_installDirectory, "mods", "Reimagined");

    [Fact]
    public async Task NexusUpdateRefreshesBothLayoutsWithoutChangingLadderBaselines()
    {
        WriteMod("NexusSaves");
        var layouts = new[] { "characterselectpanelhd.json", "controller/characterselectpanelhd.json" }
            .Select(name => Path.Combine(ActiveModRoot(), "Reimagined.mpq", "data", "global", "ui", "layouts", name))
            .ToArray();
        foreach (var layout in layouts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layout)!);
            File.WriteAllText(layout, "D2R Reimagined v3.0.10");
            LadderRuntimeFileService.RestoreOrCaptureBaseline(_installDirectory, layout);
            File.WriteAllText(layout, "D2R Reimagined v3.0.11");
        }

        var ladderLayout = layouts[0].Replace("mods" + Path.DirectorySeparatorChar + "Reimagined",
            "mods" + Path.DirectorySeparatorChar + "ReimaginedLadder");
        Directory.CreateDirectory(Path.GetDirectoryName(ladderLayout)!);
        File.WriteAllText(ladderLayout, "ladder baseline");
        var ladderBaseline = LadderRuntimeFileService.RestoreOrCaptureBaseline(_installDirectory, ladderLayout);

        NormalModInstallationService.RecordNexusInstallation(_installDirectory);
        await LadderCharacterSelectService.PrepareAsync(layouts, null, _installDirectory);

        foreach (var layout in layouts)
            Assert.Equal("D2R Reimagined v3.0.11", File.ReadAllText(layout));
        Assert.Equal("ladder baseline", File.ReadAllText(ladderBaseline));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"savepath\":42}")]
    public void InvalidNormalMetadataBlocksRecovery(string invalidMetadata)
    {
        WriteMod("NexusSaves");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);
        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);
        var normalModInfo = NormalModInstallationService.FindModInfo(
            NormalModInstallationService.NormalModRoot(_installDirectory))!;
        File.WriteAllText(normalModInfo, invalidMetadata);

        Assert.Throws<InvalidDataException>(() => NormalModInstallationService.Restore(_installDirectory));
        Assert.True(NormalModInstallationService.HasLadderInstallation(_installDirectory));
    }

    [Fact]
    public void ATrackedLegacyBundleCannotBecomeTheNormalInstallation()
    {
        WriteMod("LooksLikeNormal");
        var state = NormalModInstallationService.BundleStatePath(_installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");

        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);

        Assert.False(Directory.Exists(NormalModInstallationService.NormalModRoot(_installDirectory)));
        Assert.Throws<InvalidDataException>(() => NormalModInstallationService.Restore(_installDirectory));
        Assert.True(NormalModInstallationService.RequiresRecovery(_installDirectory));

        ReplaceActiveMod("NexusSaves");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);

        Assert.False(NormalModInstallationService.RequiresRecovery(_installDirectory));
    }

    [Fact]
    public async Task NexusReinstallReplacesNormalSavePathWithoutClearingIsolatedLadderBaselines()
    {
        WriteMod("OldNexusSaves");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);
        NormalModInstallationService.PreserveBeforeLadderInstall(_installDirectory);
        var modInfo = Path.Combine(ActiveModRoot(), "Reimagined.mpq", "modinfo.json");
        LadderRuntimeFileService.RestoreOrCaptureBaseline(_installDirectory, modInfo);

        ReplaceActiveMod("NewNexusSaves");
        NormalModInstallationService.RecordNexusInstallation(_installDirectory);

        Assert.NotNull(LadderRuntimeFileService.TryGetExistingBaselinePath(_installDirectory, modInfo));
        Assert.False(NormalModInstallationService.HasLadderInstallation(_installDirectory));
        Assert.True(await LadderSaveDirectoryService.RestoreAsync(_installDirectory));
        Assert.Contains("NewNexusSaves/", File.ReadAllText(modInfo));
    }

    [Fact]
    public async Task LegacySignedBaselineIsNotARecoverySource()
    {
        WriteMod("Wrong-Ladder-f2faf859");
        var modInfo = Path.Combine(ActiveModRoot(), "Reimagined.mpq", "modinfo.json");
        var baseline = LadderRuntimeFileService.GetBaselinePath(_installDirectory, modInfo);
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "{\"savepath\":\"AlsoWrong/\"}");

        Assert.False(await LadderSaveDirectoryService.RestoreAsync(_installDirectory));
        Assert.Contains("Wrong-Ladder-f2faf859/", File.ReadAllText(modInfo));
    }

    private void WriteMod(string savePath, params (string Name, string Contents)[] files)
    {
        Directory.CreateDirectory(Path.Combine(ActiveModRoot(), "Reimagined.mpq"));
        File.WriteAllText(
            Path.Combine(ActiveModRoot(), "Reimagined.mpq", "modinfo.json"),
            $"{{\"name\":\"Reimagined\",\"savepath\":\"{savePath}/\"}}");
        foreach (var file in files) File.WriteAllText(Path.Combine(ActiveModRoot(), file.Name), file.Contents);
    }

    private void ReplaceActiveMod(string savePath, params (string Name, string Contents)[] files)
    {
        Directory.Delete(ActiveModRoot(), recursive: true);
        WriteMod(savePath, files);
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory)) Directory.Delete(_installDirectory, recursive: true);
    }
}
