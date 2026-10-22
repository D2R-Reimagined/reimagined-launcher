using System;
using System.Threading.Tasks;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Views.Setup;

namespace ReimaginedLauncher;

public partial class MainWindow
{
    private bool _setupGuideOpen;
    internal bool IsSetupGuideOpen => _setupGuideOpen;

    public async Task ShowSetupGuideAsync()
    {
        if (_setupGuideOpen) return;
        _setupGuideOpen = true;
        try
        {
            Settings.FirstTimeSetupShown = true;
            await SettingsManager.SaveAsync(Settings);
            var guide = new SetupWindow();
            await guide.ShowDialog(this);
            RefreshLocalModState();
            RefreshCurrentContent();
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("Setup guide", exception);
            Notifications.SendNotification(exception.Message, "Setup could not finish");
        }
        finally { _setupGuideOpen = false; }
    }
}
