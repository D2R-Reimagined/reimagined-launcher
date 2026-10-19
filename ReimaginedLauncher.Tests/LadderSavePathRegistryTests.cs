using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LadderSavePathRegistryTests : IDisposable
{
    private readonly string _mods = Path.Combine(Path.GetTempPath(), $"ladder-registry-tests-{Guid.NewGuid():N}");
    private static readonly Guid Ladder = Guid.Parse("d76acaf4-2ca7-4406-98f9-e549ca04944a");

    [Fact]
    public void FirstChoicePersistsAcrossRenameEvenBeforeFolderCreation()
    {
        var first = LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "First Name");
        Assert.Equal("ReimaginedThree-First-Name-" + Ladder.ToString("N"), first);
        Assert.Equal(first, LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "New Name"));
    }

    [Fact]
    public void ExistingNestedLegacyFolderKeepsItsOriginalNameAndBase()
    {
        var relative = Path.Combine("CustomBase", "Old-Name-d76acaf4");
        Directory.CreateDirectory(Path.Combine(_mods, relative));
        Assert.Equal(relative, LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "New Name"));
    }

    [Fact]
    public void MultipleLegacyMatchesDoNotCreateARegistryOrMergeCharacters()
    {
        Directory.CreateDirectory(Path.Combine(_mods, "Old-d76acaf4"));
        Directory.CreateDirectory(Path.Combine(_mods, "Renamed-d76acaf4"));
        var error = Assert.Throws<IOException>(() => LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "Test"));
        Assert.Contains("Multiple existing save folders", error.Message);
        Assert.False(File.Exists(LadderSavePathRegistry.RegistryPath(_mods)));
    }

    [Fact]
    public void LaddersWithTheSameNameAndShortIdCannotShareRegisteredFolders()
    {
        Directory.CreateDirectory(Path.Combine(_mods, "Legacy-d76acaf4"));
        var first = LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "Same Name");
        var other = Guid.Parse("d76acaf4-1111-2222-3333-444444444444");
        var second = LadderSavePathRegistry.GetOrCreate(_mods, other, "Same Name");
        Assert.Equal("Legacy-d76acaf4", first);
        Assert.EndsWith(other.ToString("N"), second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("../Outside-d76acaf4")]
    [InlineData("/Outside-d76acaf4")]
    [InlineData("C:/Outside-d76acaf4")]
    [InlineData("ReimaginedThree")]
    [InlineData("Wrong-Ladder-aaaaaaaa")]
    public void InvalidStoredPathsFailWithoutReplacingTheRegistry(string value)
    {
        var file = LadderSavePathRegistry.RegistryPath(_mods);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var content = JsonSerializer.Serialize(new Dictionary<Guid, string> { [Ladder] = value });
        File.WriteAllText(file, content);
        Assert.Throws<IOException>(() => LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "Test"));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public void CorruptRegistryIsNotSilentlyReset()
    {
        var file = LadderSavePathRegistry.RegistryPath(_mods);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "broken");
        Assert.Throws<JsonException>(() => LadderSavePathRegistry.GetOrCreate(_mods, Ladder, "Test"));
        Assert.Equal("broken", File.ReadAllText(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_mods)) Directory.Delete(_mods, recursive: true);
    }
}
