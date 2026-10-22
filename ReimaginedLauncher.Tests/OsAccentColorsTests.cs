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
}
