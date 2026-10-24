using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Views.Launch;

namespace ReimaginedLauncher.Views.Setup;

public partial class SetupWindow : Window
{
    private readonly LauncherAuthenticationService _authentication;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal Func<AppSettings, System.Threading.Tasks.Task> SaveSettingsAsync { get; set; } = SettingsManager.SaveAsync;
    private LaunchView? _editor;
    private bool _installationStep;
    private bool _signingIn;

    public SetupWindow()
    {
        _authentication = Program.ServiceProvider.GetRequiredService<LauncherAuthenticationService>();
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (Owner is { } owner)
            {
                Width = Math.Min(780, Math.Max(560, owner.Bounds.Width - 40));
                Height = Math.Min(600, Math.Max(420, owner.Bounds.Height - 60));
            }
            _statusTimer.Start();
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Closed += (_, _) => { _statusTimer.Stop(); _lifetime.Cancel(); };
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        ReimaginedStatus.Text = _authentication.CurrentUser is { } user ? $"Signed in as {user.DisplayName}" : "Optional · Sign-in opens in your browser.";
        ReimaginedSignIn.IsEnabled = !_authentication.IsSignedIn && !_signingIn;
        NexusStatus.Text = MainWindow.UserViewModel.User is not null ? "Signed in to Nexus Mods" : "Optional · Sign-in opens in your browser.";
        NexusSignIn.IsEnabled = MainWindow.UserViewModel.User is null;
        if (!_installationStep) return;
        var issue = SetupReadiness.GetIssue(MainWindow.Settings.CurrentProfile);
        InstallationStatus.Text = issue ?? "Installation verified. Confirm this location to finish setup.";
        NextButton.IsEnabled = issue is null && _editor?.LauncherService.IsDetecting != true;
    }

    private async void OnReimaginedSignIn(object? sender, RoutedEventArgs e)
    {
        _signingIn = true;
        RefreshStatus();
        if (Owner is MainWindow owner && !await owner.SignInToReimaginedAsync(_lifetime.Token)
            && !_lifetime.IsCancellationRequested)
            ErrorText.Text = "Sign-in did not finish. Try again, or continue and sign in later.";
        _signingIn = false;
        if (!_lifetime.IsCancellationRequested) RefreshStatus();
    }

    private async void OnNexusSignIn(object? sender, RoutedEventArgs e)
    {
        NexusSignIn.IsEnabled = false;
        try { if (Owner is MainWindow owner) await owner.SignInToNexusAsync(); }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
        finally { if (!_lifetime.IsCancellationRequested) RefreshStatus(); }
    }

    private async void OnNext(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = null;
        if (!_installationStep)
        {
            _installationStep = true;
            _editor ??= new LaunchView(installationOnly: true);
            InstallationHost.Content = _editor;
            AccountsStep.IsVisible = false;
            InstallationStep.IsVisible = BackButton.IsVisible = true;
            StepLabel.Text = "2 of 2 · Installation";
            NextButton.Content = "Confirm installation";
            RefreshStatus();
            return;
        }
        try
        {
            _editor!.RefreshInstallDirectoryState();
            var issue = SetupReadiness.GetIssue(MainWindow.Settings.CurrentProfile);
            if (issue is not null) { ErrorText.Text = issue; RefreshStatus(); return; }
            var wasComplete = MainWindow.Settings.FirstTimeSetupCompleted;
            MainWindow.Settings.FirstTimeSetupCompleted = true;
            try { await SaveSettingsAsync(MainWindow.Settings); }
            catch { MainWindow.Settings.FirstTimeSetupCompleted = wasComplete; throw; }
            Close();
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        _installationStep = false;
        AccountsStep.IsVisible = true;
        InstallationStep.IsVisible = BackButton.IsVisible = false;
        StepLabel.Text = "1 of 2 · Accounts";
        NextButton.Content = "Continue";
        NextButton.IsEnabled = true;
        ErrorText.Text = null;
    }

    private void OnFinishLater(object? sender, RoutedEventArgs e) => Close();
}
