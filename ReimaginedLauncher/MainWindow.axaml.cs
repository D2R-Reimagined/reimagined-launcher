using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Notification;
using Avalonia.Platform.Storage;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Views.Launch;
using ReimaginedLauncher.Views.Settings;

namespace ReimaginedLauncher;

public partial class MainWindow : Window
{
    public static INotificationMessageManager ManagerInstance { get; } = new NotificationMessageManager();
    public static AppSettings Settings = new();

    public MainWindow()
    {
        InitializeComponent();
        _ = LoadSettingsAsync();
        ContentArea.Content = new LaunchView();
    }
    
    private async Task LoadSettingsAsync()
    {
        Settings = await SettingsManager.LoadAsync();
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
        var url = "https://www.nexusmods.com/diablo2resurrected/mods/503";
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception ex)
        {
            // Handle exception (log, display error, etc.)
        }
    }

}