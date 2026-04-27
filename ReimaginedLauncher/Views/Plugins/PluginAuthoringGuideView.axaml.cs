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
                                               }
                                             ]
                                             """;

    public PluginAuthoringGuideView()
    {
        InitializeComponent();
        FolderLayoutTextBox.Text = FolderLayoutExample;
        PluginInfoExampleTextBox.Text = PluginInfoExample;
        OperationsExampleTextBox.Text = OperationsExample;
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
