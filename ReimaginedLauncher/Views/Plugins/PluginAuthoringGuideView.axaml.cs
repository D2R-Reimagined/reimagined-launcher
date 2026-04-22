using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReimaginedLauncher.Views.Plugins;

public partial class PluginAuthoringGuideView : UserControl
{
    private const string FolderLayoutExample = """
                                              MyPlugin.zip
                                                plugininfo.json
                                                skills.json
                                              """;

    private const string PluginInfoExample = """
                                             {
                                               "name": "Lightning Balance",
                                               "version": "1.0.0",
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
                                                 "rowIdentifier": "Socketed Magic Weapon",
                                                 "column": "NumMods",
                                                 "updatedValue": "2"
                                               },
                                               {
                                                 "file": "skills.txt",
                                                 "rowIdentifier": "amazonlightningfury",
                                                 "column": "EMin",
                                                 "operation": "append",
                                                 "updatedValue": "+10*20"
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
}
