using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ReimaginedLauncher.Settings;

namespace ReimaginedLauncher;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public sealed partial class MainWindow : INotifyPropertyChanged
{
    private string _selectedExePath;
    private bool _isLaunchButtonEnabled;
    private object _currentView;
    
    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;  // Set the data context for data binding

        // Load the saved path from settings
        _selectedExePath = Properties.Default.D2RExePath;

        if (string.IsNullOrWhiteSpace(_selectedExePath)) return;
        IsLaunchButtonEnabled = true;
        BrowseButtonContent = "Change D2R.exe Location";
        CurrentView = new MainView();
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
        Properties.Default.D2RExePath = _selectedExePath;
        Properties.Default.Save();
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
        ShowConfigurationView();
    }

    public void ShowConfigurationView()
    {
        CurrentView = new ConfigurationView();
    }

    public void ShowMainView()
    {
        CurrentView = new MainView();
    }
}