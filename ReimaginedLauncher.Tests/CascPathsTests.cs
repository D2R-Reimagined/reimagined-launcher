using ReimaginedLauncher.Utilities.Casc;
using System.IO;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class CascPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "reimagined-casc-paths", "Reimagined.mpq");

    [Theory]
    [InlineData(@"data\global\excel\armor.txt")]
    [InlineData(@"data:data\global\excel\armor.txt")]
    [InlineData("data/global/excel/armor.txt")]
    public void WindowsStyleCascPathsResolveToNestedNativeDirectories(string cascPath)
    {
        var expected = Path.GetFullPath(Path.Combine(Root, "data", "global", "excel", "armor.txt"));

        Assert.Equal(expected, CascPaths.ResolveUnderRoot(Root, cascPath));
    }

    [Theory]
    [InlineData(@"..\d2rloader\config.json")]
    [InlineData(@"data\..\..\d2rloader\config.json")]
    [InlineData("data/../../outside.txt")]
    [InlineData(@"\rooted.txt")]
    [InlineData("/rooted.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("")]
    public void PathsThatAreRootedOrClimbOutOfTheRootDoNotResolve(string cascPath)
    {
        Assert.False(CascPaths.IsSafeRelative(cascPath));
        Assert.Null(CascPaths.ResolveUnderRoot(Root, cascPath));
    }

    [Fact]
    public void NativeRelativePathsRoundTripToTheCanonicalManifestForm()
    {
        var native = Path.Combine("data", "hd", "global", "ui", "panel.json");

        Assert.Equal(@"data\hd\global\ui\panel.json", CascPaths.FromNativeRelative(native));
        Assert.Equal(native, CascPaths.ToNativeRelative(@"data\hd\global\ui\panel.json"));
        Assert.Equal(@"data\hd\global\ui\panel.json", CascPaths.FromArchiveRelative("data/hd/global/ui/panel.json"));
    }

    [LinuxFact]
    public void ALinuxFileNameContainingABackslashHasNoCanonicalForm()
    {
        Assert.Null(CascPaths.FromNativeRelative(@"..\d2rloader\config.json"));
        Assert.Null(CascPaths.FromNativeRelative(Path.Combine("data", @"global\excel\armor.txt")));
    }

    [Fact]
    public void ContainmentRespectsSegmentBoundariesAndTheHostCaseSensitivity()
    {
        var root = Path.GetFullPath(Root);

        Assert.True(CascPaths.IsUnder(root, Path.Combine(root, "data")));
        Assert.False(CascPaths.IsUnder(root, root));
        Assert.False(CascPaths.IsUnder(root, root + "2" + Path.DirectorySeparatorChar + "data"));
        Assert.True(CascPaths.IsSameOrUnder(root, root + Path.DirectorySeparatorChar));
        Assert.Equal(OperatingSystem.IsWindows(), CascPaths.IsUnder(root, Path.Combine(root.ToUpperInvariant(), "data")));
    }
}
