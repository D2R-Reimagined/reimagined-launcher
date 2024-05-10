using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ReimaginedLauncher;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private string _selectedExePath = "";
    public MainWindow()
    {
        InitializeComponent();
    }
    
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (openFileDialog.ShowDialog() != true) return;
        _selectedExePath = openFileDialog.FileName;  
        LaunchButton.IsEnabled = true;  // Enable the Launch button
        BrowseButton.Content = "Change D2R.exe Location";  // Change the text of the browse button
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedExePath)) return;
            // Define the launch parameters
            var launchParameters = "-mod Merged -txt";

            // Use the saved file path to start the executable with additional parameters.
            Process.Start(new ProcessStartInfo(_selectedExePath) 
            { 
                UseShellExecute = true,
                Arguments = launchParameters
            });
        }
        catch (Exception ex)
        {
            // Handle any errors that might occur during the launch process.
            MessageBox.Show("Error: Could not execute the file. Original error: " + ex.Message);
        }
    }
}