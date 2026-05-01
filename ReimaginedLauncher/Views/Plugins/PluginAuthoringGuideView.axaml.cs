using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Plugins;

public partial class PluginAuthoringGuideView : UserControl
{
    private const string PluginCreationWikiUrl =
        "https://wiki.d2r-reimagined.com/en/DesktopLauncher/PluginCreation";
    private const string FolderLayoutExample = """
                                              MyPlugin.zip
                                                plugininfo.json
                                                skills.json
                                                assets/
                                                  item_flippy_hd.flac
                                              """;

    private const string PluginInfoExample = """
                                             {
                                               "name": "Lightning Balance",
                                               "version": "1.0.0",
                                               "modVersion": "3.0.7",
                                               "author": "YourName",
                                               "description": "Scales lightning skill damage.",
                                               "files": [
                                                 "skills.json"
                                               ],
                                               "parameters": [
                                                 {
                                                   "key": "damageMultiplier",
                                                   "name": "Damage Multiplier",
                                                   "description": "Scales the chosen skill damage.",
                                                   "defaultValue": "1.25"
                                                 }
                                               ],
                                               "assets": [
                                                 {
                                                   "source": "assets/item_flippy_hd.flac",
                                                   "target": "data/hd/global/sfx/item/item_flippy_hd.flac"
                                                 }
                                               ]
                                             }
                                             """;

    private const string OperationsExample = """
                                             [
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "Param8",
                                                 "updatedValue": "96"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "EMin",
                                                 "operation": "multiplyExisting",
                                                 "parameterKey": "damageMultiplier"
                                               },
                                               {
                                                 "file": "cubemain.txt",
                                                 "rowIdentifier": "5",
                                                 "column": "NumMods",
                                                 "updatedValue": "2"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "calc1",
                                                 "operation": "append",
                                                 "updatedValue": "+10*20"
                                               },
                                               {
                                                 "file": "monstats.txt",
                                                 "rowIdentifier": "skeleton1",
                                                 "column": "Level",
                                                 "updatedValue": "50"
                                               },
                                               {
                                                 "file": "monstats.txt",
                                                 "rowIdentifier": {
                                                   "Id": "skeleton1",
                                                   "NameStr": "Skeleton",
                                                   "hcIdx": "86"
                                                 },
                                                 "column": "Level",
                                                 "updatedValue": "55"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": [
                                                   "amazonjavazon",
                                                   "amazonbowzon",
                                                   "amazonlightningfury"
                                                 ],
                                                 "column": "reqlevel",
                                                 "updatedValue": "1"
                                               },
                                               {
                                                 "file": "monstats.txt",
                                                 "rowIdentifier": [
                                                   "skeleton1",
                                                   { "Class": "zombie", "hcIdx": "12" },
                                                   "50-55"
                                                 ],
                                                 "column": "Level",
                                                 "updatedValue": "10"
                                               },
                                               {
                                                 "file": "magicprefix.txt",
                                                 "rowIdentifier": "86",
                                                 "column": "Spawnable",
                                                 "updatedValue": "0"
                                               },
                                               {
                                                 "file": "item-runes.json",
                                                 "Key": "DoomStaff",
                                                 "enUS": "NoDoom"
                                               },
                                               {
                                                 "file": "item-runes.json",
                                                 "Key": "DoomStaff",
                                                 "enUS": "NoDoom",
                                                 "ptBR": "SemFatalidade",
                                                 "frFR": "PasDeDévastation"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "operation": "multiplyExisting",
                                                 "parameterKey": "damageMultiplier",
                                                 "columns": [
                                                   { "column": "EMin" },
                                                   { "column": "EMax" },
                                                   { "column": "EMinLev" },
                                                   { "column": "EMaxLev" }
                                                 ]
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "reqlevel",
                                                 "operation": "addExisting",
                                                 "updatedValue": "5"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "manacost",
                                                 "operation": "subtractExisting",
                                                 "updatedValue": "2"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "ResultFlags",
                                                 "operation": "divideExisting",
                                                 "updatedValue": "2"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "operation": "addRow",
                                                 "columns": [
                                                   { "column": "Skill", "updatedValue": "MyNewSkill" },
                                                   { "column": "charclass", "updatedValue": "ama" },
                                                   { "column": "reqlevel", "updatedValue": "30" },
                                                   { "column": "manacost", "updatedValue": "10" }
                                                 ]
                                               },
                                               {
                                                 "file": "cubemain.txt",
                                                 "rowIdentifier": "10",
                                                 "operation": "addRow",
                                                 "columns": [
                                                   { "column": "Description", "updatedValue": "My Custom Recipe" },
                                                   { "column": "NumInputs", "updatedValue": "2" },
                                                   { "column": "Output", "updatedValue": "ssp" }
                                                 ]
                                               },
                                               {
                                                 "file": "missiles.json",
                                                 "Key": "FireBolt",
                                                 "updatedValue": "safe_arrow"
                                               },
                                               {
                                                 "file": "missiles.json",
                                                 "Key": "MyNewMissile",
                                                 "operation": "addRow",
                                                 "parameterKey": "myMissileAssetPath"
                                               },
                                               {
                                                 "file": "monsters.json",
                                                 "Key": "Skeleton1",
                                                 "updatedValue": "fallen1"
                                               },
                                               {
                                                 "file": "monsters.json",
                                                 "Key": "MyNewMonster",
                                                 "operation": "addRow",
                                                 "parameterKey": "myMonsterAssetPath"
                                               }
                                             ]
                                             """;

    private const string ConditionsExample = """
                                             {
                                               "parameters": [
                                                 {
                                                   "key": "damageMultiplier",
                                                   "name": "Damage Multiplier",
                                                   "defaultValue": "1.25",
                                                   "description": "Plain text parameter (no 'type' field is also fine)."
                                                 },
                                                 {
                                                   "key": "enableBaalPortal",
                                                   "name": "Enable Baal Portal",
                                                   "type": "checkbox",
                                                   "defaultValue": "false",
                                                   "description": "Adds a town portal to Baal."
                                                 },
                                                 {
                                                   "key": "enableTristramPortal",
                                                   "name": "Enable Tristram Portal",
                                                   "type": "checkbox",
                                                   "defaultValue": "false"
                                                 }
                                               ],
                                               "assets": [
                                                 {
                                                   "source": "assets/town-baal-only.ds1",
                                                   "target": "data/global/tiles/act1/town/example.ds1",
                                                   "condition": {
                                                     "all": [
                                                       { "parameterKey": "enableBaalPortal",     "equals": "true"  },
                                                       { "parameterKey": "enableTristramPortal", "equals": "false" }
                                                     ]
                                                   }
                                                 }
                                               ]
                                             }

                                             // operations.json — conditional excel edits
                                             [
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "Bind Demon",
                                                 "column": "calc1",
                                                 "operation": "replace",
                                                 "updatedValue": "100",
                                                 "condition": {
                                                   "parameterKey": "bindDemonAlwaysSucceeds",
                                                   "equals": "true"
                                                 }
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "Bind Demon",
                                                 "column": "maxlvl",
                                                 "operation": "replace",
                                                 "updatedValue": "20",
                                                 "condition": {
                                                   "any": [
                                                     { "parameterKey": "enableExpertMode", "equals": "true" },
                                                     { "not": { "parameterKey": "enableNoviceMode", "equals": "true" } }
                                                   ]
                                                 }
                                               }
                                             ]
                                             """;

    public PluginAuthoringGuideView()
    {
        InitializeComponent();
        FolderLayoutTextBox.Text = FolderLayoutExample;
        PluginInfoExampleTextBox.Text = PluginInfoExample;
        OperationsExampleTextBox.Text = OperationsExample;
        ConditionsExampleTextBox.Text = ConditionsExample;
    }

    private void OnBackToPluginsClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToPluginsView();
        }
    }

    private void OnOpenWikiClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = PluginCreationWikiUrl,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception ex)
        {
            // Keep launcher stable if the shell cannot open the URL, but
            // surface the failure so the user knows to copy the link manually.
            LaunchDiagnostics.LogException("Failed to open plugin creation wiki URL", ex);
            Notifications.SendNotification(
                $"Could not open the plugin wiki in your browser: {ex.Message}",
                "Warning");
        }
    }
}
