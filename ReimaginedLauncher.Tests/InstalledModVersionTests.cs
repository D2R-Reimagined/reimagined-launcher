using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class InstalledModVersionTests
{
    [Theory]
    [InlineData("3.0.11", "3.0.10", "3.0.11")]
    [InlineData("3.0.10", "3.0.11", "3.0.10")]
    [InlineData(null, "3.0.10", "3.0.10")]
    [InlineData("", "3.0.10", "3.0.10")]
    [InlineData("Unknown", "3.0.10", "3.0.10")]
    [InlineData(null, null, "Unknown")]
    public void InstalledMetadataTakesPrecedenceOverUiText(string? metadata, string? panel, string expected)
    {
        Assert.Equal(expected, MainWindow.SelectInstalledModVersion(metadata, panel));
    }
}
