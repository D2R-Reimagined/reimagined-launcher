using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReimaginedLauncher.Views.Update;

public partial class UpdateFoundWindow : Window
{
    private readonly string _downloadUrl;

    public UpdateFoundWindow()
        : this(string.Empty, string.Empty, string.Empty)
    {
    }

    public UpdateFoundWindow(string currentVersion, string newVersion, string downloadUrl)
    {
        InitializeComponent();
        CurrentVersionText.Text = currentVersion;
        NewVersionText.Text = newVersion;
        _downloadUrl = downloadUrl;
    }

    private void OnDownloadClicked(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _downloadUrl,
            UseShellExecute = true
        });
    }
}
