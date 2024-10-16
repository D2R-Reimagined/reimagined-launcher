using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ReimaginedLauncher.Properties;

namespace ReimaginedLauncher;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public sealed partial class MainWindow : INotifyPropertyChanged
{
    private string _selectedExePath;
    private bool _isLaunchButtonEnabled;
    private object _currentView;

    private ClientWebSocket _webSocket;
    private string _uuid;
    private string _token;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;  // Set the data context for data binding

        // Load the saved path from settings
        _selectedExePath = Properties.Settings.Default.D2RExePath;

        if (string.IsNullOrWhiteSpace(_selectedExePath)) return;
        IsLaunchButtonEnabled = true;
        BrowseButtonContent = "Change D2R.exe Location";
    }
    
    
    public object CurrentView
    {
        get => _currentView;
        set
        {
            if (_currentView != value)
            {
                _currentView = value;
                OnPropertyChanged(nameof(CurrentView));
            }
        }
    }
    
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (openFileDialog.ShowDialog() != true) return;
        _selectedExePath = openFileDialog.FileName;  // Save the selected file path
        LaunchButton.IsEnabled = true;               // Enable the Launch button
        BrowseButton.Content = "Change D2R.exe Location";  // Update the browse button text

        // Save the selected path to settings
        Properties.Settings.Default.D2RExePath = _selectedExePath;
        Properties.Settings.Default.Save();
    }
    
    public bool IsLaunchButtonEnabled
    {
        get => _isLaunchButtonEnabled;
        set
        {
            if (_isLaunchButtonEnabled == value) return;
            _isLaunchButtonEnabled = value;
            OnPropertyChanged(nameof(IsLaunchButtonEnabled));
        }
    }
    
    private string _browseButtonContent = "Select D2R.exe Location";

    public string BrowseButtonContent
    {
        get => _browseButtonContent;
        set
        {
            if (_browseButtonContent == value) return;
            _browseButtonContent = value;
            OnPropertyChanged(nameof(BrowseButtonContent));
        }
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedExePath)) return;
            // Define the launch parameters
            const string launchParameters = "-mod Merged -txt";

            // Use the saved file path to start the executable with additional parameters.
            Process.Start(new ProcessStartInfo(_selectedExePath) 
            { 
                UseShellExecute = true,
                Arguments = launchParameters
            });
            ShowOverlayWindow();
        }
        catch (Exception ex)
        {
            // Handle any errors that might occur during the launch process.
            MessageBox.Show("Error: Could not execute the file. Original error: " + ex.Message);
        }
    }
    
    private static void ShowOverlayWindow()
    {
        var overlayWindow = new OverlayWindow();
        overlayWindow.Show();
    }
    
    private void ConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("here");
        StartNexusModsSSOAsync();
        //ShowConfigurationView();
    }

    public void ShowConfigurationView()
    {
        CurrentView = new ConfigurationView();
    }

    public void ShowMainView()
    {
        CurrentView = null;
    }

    private async Task StartNexusModsSSOAsync()
    {
        Console.WriteLine("here2");
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri("wss://sso.nexusmods.com"), CancellationToken.None);

        if (!string.IsNullOrEmpty(Properties.Settings.Default.UUID))
        {
            Console.WriteLine("here3");
            _uuid = Guid.NewGuid().ToString();
            _token = "";
        }
        else
        {
            Console.WriteLine("here4");
            _uuid = Properties.Settings.Default.UUID;
            _token = Properties.Settings.Default.Token;
        }

        SaveSessionData(_uuid, _token);

        var data = new
        {
            id = _uuid,
            token = _token,
            protocol = 2
        };

        string jsonData = JsonSerializer.Serialize(data);
        await SendMessageAsync(jsonData);

        await ReceiveMessagesAsync();
    }

    private void SaveSessionData(string uuid, string token)
    {
        Properties.Settings.Default.UUID = uuid;
        Properties.Settings.Default.Token = token;
        Properties.Settings.Default.Save();
    }

    private async Task SendMessageAsync(string message)
    {
        Console.WriteLine("here5", message);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveMessagesAsync()
    {
        var buffer = new byte[1024 * 4];
        while (_webSocket.State == WebSocketState.Open)
        {
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);

            var response = JsonSerializer.Deserialize<NexusModsResponse>(receivedMessage);
            if (response != null && response.success)
            {
                if (!string.IsNullOrEmpty(response.data.connection_token))
                {
                    _token = response.data.connection_token;
                    SaveSessionData(_uuid, _token);

                    OpenAuthorizationPage(_uuid);
                }
                else if (!string.IsNullOrEmpty(response.data.api_key))
                {
                    string apiKey = response.data.api_key;
                    MessageBox.Show($"Authenticated! API Key: {apiKey}");
                }
            }
            else
            {
                MessageBox.Show($"Error: {response?.error ?? "Unknown error"}");
            }
        }
    }

    // Open the browser for user authorization
    private void OpenAuthorizationPage(string uuid)
    {
        string applicationSlug = "Reimagined";
        string url = $"https://www.nexusmods.com/sso?id={uuid}&application={applicationSlug}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private class NexusModsResponse
    {
        public bool success { get; set; }
        public Data data { get; set; }
        public string error { get; set; }
    }

    private class Data
    {
        public string connection_token { get; set; }
        public string api_key { get; set; }
    }
}