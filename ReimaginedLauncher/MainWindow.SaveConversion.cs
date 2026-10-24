using System;
using System.Threading;
using System.Threading.Tasks;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Views.SaveConversion;

namespace ReimaginedLauncher;

public partial class MainWindow
{
    private int _saveConversionCheckGeneration;

    /// <summary>
    /// Analyzes the offline saves on a dedicated background thread and opens the conversion window
    /// when any of them need converting. Only the most recent check may prompt, and never over another dialog.
    /// </summary>
    public async Task CheckSavesForConversionAsync(string trigger)
    {
        var generation = ++_saveConversionCheckGeneration;
        var profile = Settings.CurrentProfile;
        try
        {
            var files = await Task.Factory.StartNew(
                () => SaveConversionService.FindOfflineSavesNeedingConversion(profile),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            LaunchDiagnostics.Log($"Save conversion check ({trigger}): {files.Count} file(s) may need conversion.");
            if (files.Count == 0 || generation != _saveConversionCheckGeneration || IsInstallInProgress ||
                IsGameRunning() || !IsVisible || OwnedWindows.Count > 0 ||
                !ReferenceEquals(profile, Settings.CurrentProfile))
            {
                return;
            }

            await new SaveConversionWindow(
                    $"{files.Count} save file(s) may need to be converted to the installed Reimagined version. "
                    + "Review them below; nothing is changed until you choose Convert Selected.")
                .ShowDialog(this);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException($"Save conversion check ({trigger}) failed", exception);
        }
    }
}
