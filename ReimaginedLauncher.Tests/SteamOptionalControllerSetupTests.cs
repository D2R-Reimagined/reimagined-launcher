using System.Text;
using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamOptionalControllerSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-optional-layout-" + Guid.NewGuid().ToString("N"));
    private const string OldName = "D2R Reimagined (Native Launcher)";
    private const string NewName = "Diablo II: Reimagined (Native Launcher)";
    private const string Template = "\"controller_mappings\" { \"group\" { \"mode\" \"absolute_mouse\" \"inputs\" { \"click\" { \"binding\" \"xinput_button JOYSTICK_RIGHT\" } } } }";
    private const string CustomLayout = "\"controller_config\" { \"diablo ii reimagined (native launcher)\" { \"workshop\" \"custom\" } \"other game\" { \"template\" \"custom.vdf\" } }";
    private string Templates => Path.Combine(_root, "controller_base", "templates");
    private string Config => Path.Combine(_root, "steamapps", "common", "Steam Controller Configs", "123", "config");

    /// <summary>Creates disposable Steam files without accessing the user's Steam installation.</summary>
    public SteamOptionalControllerSetupTests()
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "configset_controller_neptune.vdf"), CustomLayout);
        File.WriteAllText(Path.Combine(Config, "configset_controller_xbox360.vdf"), CustomLayout);
    }

    /// <summary>Creates a source-template fixture for optional layout configuration.</summary>
    private void WriteTemplate(string text)
    {
        Directory.CreateDirectory(Templates);
        File.WriteAllText(Path.Combine(Templates, "controller_neptune_gamepad+mouse.vdf"), text);
    }

    /// <summary>Verifies that unavailable source templates leave every saved controller file unchanged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("malformed template")]
    [InlineData("\"controller_mappings\" { \"group\" { \"mode\" \"joystick_move\" } }")]
    public void UnavailableTemplatePreservesSavedLayoutsEvenWhenResetRequested(string? source)
    {
        if (source != null) WriteTemplate(source);
        var before = Directory.GetFiles(Config).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

        var warning = SteamControllerLayout.SetUpOptionalLauncher(_root, "123", OldName, NewName, true);

        Assert.NotNull(warning);
        Assert.Equal(before.Count, Directory.GetFiles(Config).Length);
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Config, name!)));
        Assert.False(File.Exists(Path.Combine(Templates, "controller_neptune_reimagined.vdf")));
    }

    /// <summary>Verifies that supported layout generation preserves stock templates and custom preferences.</summary>
    [Fact]
    public void SupportedTemplatePreservesCustomLayoutsUnlessResetRequested()
    {
        WriteTemplate(Template);
        var source = Path.Combine(Templates, "controller_neptune_gamepad+mouse.vdf");
        var original = File.ReadAllBytes(source);

        Assert.Null(SteamControllerLayout.SetUpOptionalLauncher(_root, "123", OldName, NewName));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_neptune.vdf")));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_xbox360.vdf")));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Null(SteamControllerLayout.SetUpOptionalLauncher(_root, "123", OldName, NewName, true));
        Assert.Contains("controller_neptune_reimagined.vdf", File.ReadAllText(Path.Combine(Config, "configset_controller_neptune.vdf")));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_xbox360.vdf")));
        var configured = File.ReadAllBytes(Path.Combine(Config, "configset_controller_neptune.vdf"));
        Assert.Null(SteamControllerLayout.SetUpOptionalLauncher(_root, "123", OldName, NewName, true));
        Assert.Equal(configured, File.ReadAllBytes(Path.Combine(Config, "configset_controller_neptune.vdf")));
    }

    /// <summary>Verifies that a failed template write is a warning rather than a required setup dependency.</summary>
    [Fact]
    public void UnwritableTemplateDestinationPreservesControllerPreferences()
    {
        WriteTemplate(Template);
        Directory.CreateDirectory(Path.Combine(Templates, "controller_neptune_reimagined.vdf"));

        Assert.NotNull(SteamControllerLayout.SetUpOptionalLauncher(_root, "123", OldName, NewName, true));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_neptune.vdf")));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_xbox360.vdf")));
        Assert.Empty(Directory.GetFiles(Templates, "*.tmp"));
    }

    /// <summary>Exercises real Linux registration with disposable Steam roots and unavailable or supported templates.</summary>
    [LinuxTheory]
    [InlineData(null, true)]
    [InlineData("malformed template", true)]
    [InlineData(Template, false)]
    public void LinuxRegistrationSucceedsIndependentlyOfControllerTemplate(string? source, bool expectWarning)
    {
        if (source != null) WriteTemplate(source);
        var account = Path.Combine(_root, "userdata", "123", "config");
        Directory.CreateDirectory(account);
        var launcher = Path.Combine(_root, "Launcher.AppImage");
        File.WriteAllText(launcher, "fixture only; never executed");
        var unrelated = SteamShortcutFile.AppendOwnedShortcuts([], new LauncherSteamShortcut("Unrelated game", "\"/games/other\"", "-custom"));
        var shortcutFile = Path.Combine(account, "shortcuts.vdf");
        File.WriteAllBytes(shortcutFile, unrelated);
        var state = Path.Combine(_root, "state");

        var result = SteamGameHandoff.Install(_root, "123", launcher, stateDirectory: state);

        Assert.Equal(expectWarning, result.ControllerLayoutWarning != null);
        var registered = JsonSerializer.Deserialize<SteamHandoffRegistration>(File.ReadAllText(Path.Combine(state, "registration.json")))!;
        Assert.Equal(result.AppId, registered.AppId);
        Assert.Equal(_root, registered.SteamRoot);
        var updated = File.ReadAllBytes(shortcutFile);
        Assert.Equal(unrelated[..^2], updated[..(unrelated.Length - 2)]);
        Assert.Contains(NewName, Encoding.UTF8.GetString(updated));
        Assert.Contains("Diablo II: Reimagined (Launcher Game Session)", Encoding.UTF8.GetString(updated));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_xbox360.vdf")));
        Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_neptune.vdf")));
        SteamGameHandoff.Install(_root, "123", launcher, stateDirectory: state);
        Assert.Equal(updated, File.ReadAllBytes(shortcutFile));
    }

    /// <summary>Verifies real Linux write-permission failure cannot prevent shortcut registration.</summary>
    [LinuxFact]
    public void ReadOnlyTemplateDirectoryDoesNotBlockLinuxRegistration()
    {
        if (!OperatingSystem.IsLinux()) return;
        WriteTemplate(Template);
        var account = Path.Combine(_root, "userdata", "123", "config");
        Directory.CreateDirectory(account);
        var launcher = Path.Combine(_root, "Launcher.AppImage");
        File.WriteAllText(launcher, "fixture only; never executed");
        var permissions = File.GetUnixFileMode(Templates);
        try
        {
            File.SetUnixFileMode(Templates, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = SteamGameHandoff.Install(_root, "123", launcher, true, Path.Combine(_root, "state"));
            Assert.NotNull(result.ControllerLayoutWarning);
            Assert.True(File.Exists(Path.Combine(account, "shortcuts.vdf")));
            Assert.True(File.Exists(Path.Combine(_root, "state", "registration.json")));
            Assert.Equal(CustomLayout, File.ReadAllText(Path.Combine(Config, "configset_controller_neptune.vdf")));
        }
        finally { File.SetUnixFileMode(Templates, permissions); }
    }

    /// <summary>Verifies unsupported platforms still reject Linux shortcut registration without writing files.</summary>
    [Fact]
    public void WindowsStillRejectsLinuxShortcutRegistration()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.Throws<InvalidOperationException>(() => SteamGameHandoff.Install(_root, "123", "unused", stateDirectory: Path.Combine(_root, "state")));
        Assert.False(Directory.Exists(Path.Combine(_root, "state")));
    }

    /// <summary>Deletes only the disposable Steam fixture.</summary>
    public void Dispose() => Directory.Delete(_root, true);
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    /// <summary>Marks Linux filesystem integration tests as skipped on other operating systems.</summary>
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires an actual Linux filesystem."; }
}

internal sealed class LinuxTheoryAttribute : TheoryAttribute
{
    /// <summary>Marks Linux registration scenarios as skipped on other operating systems.</summary>
    public LinuxTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires native Linux registration."; }
}
