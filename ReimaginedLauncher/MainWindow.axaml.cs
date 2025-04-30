using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Notification;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.ViewModels;
using ReimaginedLauncher.Views.Launch;
using ReimaginedLauncher.Views.Settings;

namespace ReimaginedLauncher;

public partial class MainWindow : Window
{
    // Make URLs readonly for safe reuse across the file
    private const string WebsiteUrl = "https://www.d2r-reimagined.com";
    private const string WikiUrl = "https://wiki.d2r-reimagined.com";
    private const string NexusUrl = "https://www.nexusmods.com/diablo2resurrected/mods/503";
    private const string DiscordUrl = "https://discord.gg/5bbjneJCrr";
    private readonly INexusModsHttpClient _nexusModsHttpClient;
    public NexusModsValidateResponse? User { get; set; }
    public static NexusUserViewModel UserViewModel { get; } = new();
    
    public static INotificationMessageManager ManagerInstance { get; } = new NotificationMessageManager();
    public static AppSettings Settings = new();
    private NexusModsSSO _nexusSSO;

    public MainWindow()
    {
        _nexusModsHttpClient = Program.ServiceProvider.GetRequiredService<NexusModsHttpClient>();;
        InitializeComponent();
        
        DataContext = UserViewModel;
        _ = LoadSettingsAsync();
        ContentArea.Content = new LaunchView();
        
        // Set the window icon
        Icon = new WindowIcon("Assets/ReimaginedLauncher.ico");
    }
    
    private async Task LoadSettingsAsync()
    {
        Settings = await SettingsManager.LoadAsync();
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync();
            UserViewModel.User = User;
        }
    }
    
    private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is ListBoxItem item)
        {
            switch (item.Content?.ToString())
            {
                case "Launch":
                    ContentArea.Content = new LaunchView();
                    break;
                case "Settings":
                    ContentArea.Content = new SettingsView();
                    break;
            }
        }
    }
    
    private void OnVisitWebsiteClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? urlToOpen = null;

            // Use the sender's context to determine which URL to use
            if (sender is Button button)
            {
                urlToOpen = button.Name switch
                {
                    "WebsiteButton" => WebsiteUrl,
                    "WikiButton" => WikiUrl,
                    "NexusButton" => NexusUrl,
                    "DiscordButton" => DiscordUrl,
                    _ => urlToOpen
                };
            }

            if (!string.IsNullOrEmpty(urlToOpen))
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = urlToOpen,
                    UseShellExecute = true
                };
                process.Start();
            }
        }
        catch (Exception ex)
        {
            // Handle exception (log, display error, etc.)
        }
    }

    private async void OnNexusLoginClicked(object sender, RoutedEventArgs e)
    {
        _nexusSSO = new NexusModsSSO();
        _nexusSSO.OnApiKeyReceived += apiKey =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Settings.NexusModsSSOApiKey = apiKey;
                _ = ValidateKey();
                Notifications.SendNotification($"Nexus Login API Key: {apiKey}");
            });
        };

        await _nexusSSO.ConnectAsync();
    }
    
    private async Task ValidateKey()
    {
        await SettingsManager.SaveAsync(Settings);
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync(Settings.NexusModsSSOApiKey);
            UserViewModel.User = User;
        }
    }
}