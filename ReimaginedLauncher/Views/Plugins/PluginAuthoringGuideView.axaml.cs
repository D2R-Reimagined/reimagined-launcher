using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReimaginedLauncher.Views.Plugins;

public partial class PluginAuthoringGuideView : UserControl
{
    private const string PluginCreationWikiUrl =
        "https://github.com/D2R-Reimagined/reimagined-launcher/wiki/Plugin-Creation";
    private const string FolderLayoutExample = """
                                              MyPlugin.zip
                                                plugininfo.json
                                                skills.json
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
                                                 "file": "monstats.txt",
                                                 "rowIdentifier": "skeleton1",
                                                 "column": "Level",
                                                 "updatedValue": "50"
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
        catch (Exception)
        {
            // Keep launcher stable if the shell cannot open the URL.
        }
    }
}
