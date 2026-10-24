using Avalonia.Media;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class OsAccentColorsTests
{
    [Theory]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#FFFF00", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#005A9E", "#FFFFFF")]
    public void BrightAndDarkSystemAccentsKeepReadableLabels(string accent, string expected)
        => Assert.Equal(Color.Parse(expected), OsAccentColors.GetForeground(Color.Parse(accent)));

    [Theory]
    [InlineData("#00000000", "#9E2D1E")]
    [InlineData("#FF000000", "#9E2D1E")]
    [InlineData("#FF101010", "#9E2D1E")]
    [InlineData("#FF005A9E", "#005A9E")]
    public void TransparentOrBlackSystemAccentsFallBackToLauncherAccent(string accent, string expected)
        => Assert.Equal(Color.Parse(expected), OsAccentColors.ResolveAccent(Color.Parse(accent)));
}
