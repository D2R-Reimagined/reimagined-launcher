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
    /// when any of them need converting or review. Only the most recent check may prompt, never over another dialog,
    /// and only once per installation and installed target data.
    /// </summary>
    public async Task CheckSavesForConversionAsync(string trigger)
    {
        var generation = ++_saveConversionCheckGeneration;
        var profile = Settings.CurrentProfile;
        var installDirectory = profile.InstallDirectory;
        try
        {
            var (files, targetFingerprint) = await Task.Factory.StartNew(
                () => SaveConversionService.FindOfflineSavesNeedingConversion(profile),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            LaunchDiagnostics.Log(
                $"Save conversion check ({trigger}): {files.Count} file(s) may need conversion or review.");
            if (files.Count == 0 || targetFingerprint is null ||
                SaveConversionService.WasAutomaticPromptShown(profile, targetFingerprint) ||
                generation != _saveConversionCheckGeneration || IsInstallInProgress ||
                IsGameRunning() || !IsVisible || OwnedWindows.Count > 0 ||
                !ReferenceEquals(profile, Settings.CurrentProfile) ||
                !SaveConversionService.IsAnalyzedInstallation(profile, installDirectory))
            {
                return;
            }

            var window = new SaveConversionWindow(
                "Some offline saves may need conversion or review for the installed Reimagined version. "
                + "Review them below; nothing is changed until you choose Convert Selected.",
                () =>
                {
                    if (!ReferenceEquals(profile, Settings.CurrentProfile) ||
                        !SaveConversionService.RecordAutomaticPromptShown(profile, installDirectory, targetFingerprint))
                    {
                        return false;
                    }

                    _ = SaveAutomaticPromptStateAsync();
                    return true;
                });
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException($"Save conversion check ({trigger}) failed", exception);
        }
    }

    private static async Task SaveAutomaticPromptStateAsync()
    {
        try
        {
            await SettingsManager.SaveAsync(Settings);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("Could not save the save conversion prompt state", exception);
        }
    }
}
