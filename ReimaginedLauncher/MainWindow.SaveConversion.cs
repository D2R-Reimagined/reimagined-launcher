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
    /// and once per installation, play mode, save contents and installed target data outside launch and install checks.
    /// A launch check is skipped once any automatic window opened since the last completed mod install or update.
    /// </summary>
    /// <returns>True when no conversion review was pending; always true for a launch check, which never blocks the launch.</returns>
    public async Task<bool> CheckSavesForConversionAsync(string trigger, bool beforeLaunch = false,
        bool afterInstall = false)
    {
        var generation = ++_saveConversionCheckGeneration;
        var ignorePromptHistory = beforeLaunch || afterInstall;
        var profile = Settings.CurrentProfile;
        if (beforeLaunch && profile.SaveConversionLaunchPromptShown)
        {
            LaunchDiagnostics.Log(
                $"Save conversion check ({trigger}): skipped, already prompted since the last install or update.");
            return true;
        }

        var installDirectory = profile.InstallDirectory;
        var experience = profile.LaunchExperience;
        try
        {
            var (files, targetFingerprint, saveDirectory, promptAlreadyShown) = await Task.Factory.StartNew(
                () => SaveConversionService.FindOfflineSavesNeedingConversion(profile,
                    ignorePromptHistory: ignorePromptHistory),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            LaunchDiagnostics.Log(promptAlreadyShown
                ? $"Save conversion check ({trigger}): skipped, the prompt was already shown for this installed target and save folder."
                : $"Save conversion check ({trigger}): {files.Count} file(s) may need conversion or review.");

            if (files.Count == 0 || targetFingerprint is not { } analyzedTarget)
            {
                return beforeLaunch || files.Count == 0;
            }

            bool IsPromptCurrent() =>
                ReferenceEquals(profile, Settings.CurrentProfile) && profile.LaunchExperience == experience &&
                SaveConversionService.IsAnalyzedInstallation(profile, installDirectory, saveDirectory) &&
                (ignorePromptHistory || !SaveConversionService.WasAutomaticPromptShown(profile, saveDirectory, analyzedTarget));

            if (generation != _saveConversionCheckGeneration || IsInstallInProgress ||
                IsGameRunning() || !IsVisible || OwnedWindows.Count > 0 || !IsPromptCurrent())
            {
                return beforeLaunch;
            }

            var window = new SaveConversionWindow(
                "Some offline saves may need conversion or review for the installed Reimagined version. "
                + "Review them below; nothing is changed until you choose Convert Selected.",
                IsPromptCurrent,
                (shownTargetFingerprint, shownPlans) =>
                {
                    if (IsPromptCurrent() && SaveConversionService.RecordAutomaticPromptShown(profile,
                            installDirectory, saveDirectory, analyzedTarget, shownTargetFingerprint, shownPlans))
                    {
                        _ = SaveAutomaticPromptStateAsync();
                    }
                });
            profile.SaveConversionLaunchPromptShown = true;
            await SaveAutomaticPromptStateAsync();
            await window.ShowDialog(this);
            return beforeLaunch;
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException($"Save conversion check ({trigger}) failed", exception);
            if (beforeLaunch)
                Notifications.SendNotification("Save compatibility could not be checked; launching anyway. Open Convert Save Data to review your saves. " + exception.Message, "Save check failed");
            return true;
        }
    }

    /// <summary>Persists the save conversion prompt state; failures are logged, never thrown.</summary>
    internal static async Task SaveAutomaticPromptStateAsync()
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
