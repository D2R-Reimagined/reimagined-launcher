using System.Text;
using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ConfigEditSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launcher-editor-" + Guid.NewGuid().ToString("N"));
    private string FileWith(string text, string extension = ".toml", Encoding? encoding = null)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "example" + extension);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavePreservesEncodingAndBacksUpExactBytes(bool unicode)
    {
        var path = FileWith("name = \"café\"\r\n", encoding: unicode ? Encoding.Unicode : new UTF8Encoding(true));
        var original = File.ReadAllBytes(path);
        var session = new ConfigEditSession(path);
        var backup = session.Save(session.Text.Replace("café", "tea"), () => { });
        Assert.Equal(original, File.ReadAllBytes(backup!));
        Assert.Contains("tea", File.ReadAllText(path));
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(unicode ? Encoding.Unicode.GetPreamble() : Encoding.UTF8.GetPreamble()));
        Assert.EndsWith("\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void OutsideChangesAreNotOverwritten()
    {
        var path = FileWith("enabled = true");
        var session = new ConfigEditSession(path);
        File.WriteAllText(path, "another editor");
        Assert.Throws<IOException>(() => session.Save("enabled = false", () => { }));
        Assert.Equal("another editor", File.ReadAllText(path));
    }

    [Fact]
    public void InvalidJsonDoesNotChangeFileOrCreateBackup()
    {
        var path = FileWith("{}", ".json");
        Assert.ThrowsAny<JsonException>(() => new ConfigEditSession(path).Save("{oops", () => { }));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(root, ".launcher-config-backups")));
    }

    [Fact]
    public void GuardRecheckedBeforeReplacementAndOnlyThreeBackupsRetained()
    {
        var path = FileWith("0");
        var session = new ConfigEditSession(path);
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => session.Save("changed", () => { if (++calls == 2) throw new InvalidOperationException("Ladder"); }));
        Assert.Equal("0", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        for (var i = 1; i <= 5; i++) session.Save(i.ToString(), () => { });
        var backups = Directory.GetFiles(Path.Combine(root, ".launcher-config-backups"));
        Assert.Equal(3, backups.Length);
        Assert.Equal(new[] { "2", "3", "4" }, backups.Select(File.ReadAllText).OrderBy(s => s));
    }

    [Fact]
    public void UnchangedSaveDoesNotCreateBackup()
    {
        var path = FileWith("enabled = true");
        var session = new ConfigEditSession(path);
        Assert.Null(session.Save(session.Text, () => { }));
        Assert.Single(Directory.GetFiles(root));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
