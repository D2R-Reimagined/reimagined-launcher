using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamControllerLayoutTests
{
    private const string OldName = "D2R Reimagined (Native Launcher)";
    private const string NewName = "Diablo II: Reimagined (Native Launcher)";
    private const string Original = "\"controller_config\"\n{\n\t\"d2r reimagined (native launcher)\"\n\t{\n\t\t\"template\" \"controller_neptune_mouse.vdf\"\n\t}\n\t\"game session\" { \"template\" \"gamepad.vdf\" }\n}\n";

    [Fact]
    public void RenameCarriesLayoutForwardWithoutChangingExistingBytes()
    {
        var updated = SteamControllerLayout.CarryForward(Original, OldName, NewName);
        var insertion = "\n\t\"diablo ii reimagined (native launcher)\"\n\t{\n\t\t\"template\" \"controller_neptune_mouse.vdf\"\n\t}\n";
        Assert.Equal(Original.Insert(Original.LastIndexOf('}'), insertion), updated);
        Assert.Equal(updated, SteamControllerLayout.CarryForward(updated, OldName, NewName));
    }

    [Fact]
    public void ExistingNewNamePreferenceIsPreserved()
    {
        var original = Original.Insert(Original.LastIndexOf('}'), "\"Diablo II Reimagined (Native Launcher)\" { \"workshop\" \"custom\" }\n");
        Assert.Equal(original, SteamControllerLayout.CarryForward(original, OldName, NewName));
    }

    [Fact]
    public void MissingLegacyPreferenceIsNotInvented()
    {
        const string original = "\"controller_config\" { \"game\" { \"template\" \"gamepad\" } }";
        Assert.Equal(original, SteamControllerLayout.CarryForward(original, OldName, NewName));
    }

    [Theory]
    [InlineData("\"controller_config\" {")]
    [InlineData("\"controller_config\" { \"x\" { } \"x\" { } }")]
    [InlineData("\"other\" { }")]
    [InlineData("\"controller_config\" { } trailing")]
    public void MalformedFilesAreRejected(string original)
        => Assert.Throws<InvalidDataException>(() => SteamControllerLayout.CarryForward(original, OldName, NewName));

    [Fact]
    public void DefaultSeedsOnlyMissingPreferenceAndKeepsCustomLegacyLayout()
    {
        var empty = "\"controller_config\" { }";
        var seeded = SteamControllerLayout.CarryForward(empty, OldName, NewName, true);
        Assert.Contains("controller_neptune_mouse.vdf", seeded);
        Assert.Equal(seeded, SteamControllerLayout.CarryForward(seeded, OldName, NewName, true));
        var custom = Original.Replace("controller_neptune_mouse.vdf", "custom-layout.vdf");
        Assert.DoesNotContain("controller_neptune_mouse.vdf", SteamControllerLayout.CarryForward(custom, OldName, NewName, true));
        Assert.Equal(SteamControllerLayout.CarryForward(custom, OldName, NewName),
            SteamControllerLayout.CarryForward(custom, OldName, NewName, true));
    }

    [Fact]
    public void DefaultDoesNotOverwriteExistingNewNameLayout()
    {
        var original = "\"controller_config\" { \"Diablo II Reimagined (Native Launcher)\" { \"autosave\" \"1\" } }";
        Assert.Equal(original, SteamControllerLayout.CarryForward(original, OldName, NewName, true));
    }

    [Fact]
    public void IncorrectDisplayNameKeyCarriesForwardToSteamsColonFreeKey()
    {
        var original = "\"controller_config\" { \"diablo ii: reimagined (native launcher)\" { \"template\" \"custom.vdf\" } }";
        var updated = SteamControllerLayout.CarryForward(original, OldName, NewName, true);
        Assert.Contains("\"diablo ii reimagined (native launcher)\"\n\t{ \"template\" \"custom.vdf\" }", updated);
        Assert.Equal(updated, SteamControllerLayout.CarryForward(updated, OldName, NewName, true));
    }

    [Fact]
    public void SetupSeedsDeckConfigsOnlyWhenTheInstalledTemplateExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "deck-default-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "steamapps", "common", "Steam Controller Configs", "123", "config");
        Directory.CreateDirectory(config);
        try
        {
            var deck = Path.Combine(config, "configset_device.vdf");
            var xbox = Path.Combine(config, "configset_controller_xbox360.vdf");
            const string empty = "\"controller_config\" { }";
            File.WriteAllText(deck, empty);
            File.WriteAllText(xbox, empty);
            File.WriteAllText(Path.Combine(config, "preferences_device.vdf"), "\"ControllerPersonalization\" { \"name\" \"Steam Deck Controller \" }");
            Assert.Equal(0, SteamControllerLayout.SetUpLauncher(root, "123", OldName, NewName));
            var template = Path.Combine(root, "controller_base", "templates", "controller_neptune_mouse.vdf");
            Directory.CreateDirectory(Path.GetDirectoryName(template)!);
            File.WriteAllText(template, "template fixture");
            Assert.Equal(2, SteamControllerLayout.SetUpLauncher(root, "123", OldName, NewName));
            Assert.Contains("controller_neptune_mouse.vdf", File.ReadAllText(deck));
            Assert.Contains("controller_neptune_mouse.vdf", File.ReadAllText(Path.Combine(config, "configset_controller_neptune.vdf")));
            Assert.Equal(empty, File.ReadAllText(xbox));
            Assert.Equal(0, SteamControllerLayout.SetUpLauncher(root, "123", OldName, NewName));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExplicitResetReplacesOnlyOwnedPreferenceAndIsIdempotent()
    {
        const string original = "\"controller_config\" { \"diablo ii reimagined (native launcher)\" { \"template\" \"controller_neptune_gamepad_fps.vdf\" \"workshop\" \"custom\" } \"game session\" { \"template\" \"gamepad.vdf\" } }";
        var updated = SteamControllerLayout.CarryForward(original, OldName, NewName, true, true);
        var expected = original.Replace("{ \"template\" \"controller_neptune_gamepad_fps.vdf\" \"workshop\" \"custom\" }",
            "{\n\t\t\"template\"\t\t\"controller_neptune_mouse.vdf\"\n\t}");
        Assert.Equal(expected, updated);
        Assert.Equal(updated, SteamControllerLayout.CarryForward(updated, OldName, NewName, true, true));
        Assert.Equal(original, SteamControllerLayout.CarryForward(original, OldName, NewName, true));
        Assert.Equal(original, SteamControllerLayout.CarryForward(original, OldName, NewName, false, true));
    }

    [Fact]
    public void ExplicitResetDoesNotCarryLegacyGamepadChoice()
    {
        var original = Original.Replace("controller_neptune_mouse.vdf", "controller_neptune_gamepad_fps.vdf");
        var updated = SteamControllerLayout.CarryForward(original, OldName, NewName, true, true);
        Assert.Contains("\"diablo ii reimagined (native launcher)\"\n\t{\n\t\t\"template\"\t\t\"controller_neptune_mouse.vdf\"", updated);
        Assert.StartsWith(original[..original.LastIndexOf('}')], updated);
    }

    [Fact]
    public void FileMigrationBacksUpAndPreservesUnrelatedConfigs()
    {
        var root = Path.Combine(Path.GetTempPath(), "controller-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "configset_example.vdf");
            File.WriteAllText(path, Original);
            Assert.Equal(1, SteamControllerLayout.MigrateLauncherName(root, OldName, NewName));
            Assert.Equal(Original, File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.bak"))));
            Assert.Equal(0, SteamControllerLayout.MigrateLauncherName(root, OldName, NewName));
        }
        finally { Directory.Delete(root, true); }
    }
}
