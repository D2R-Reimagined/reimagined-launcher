using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;

namespace ReimaginedLauncher.Views
{
    public partial class UpdateFoundWindow : Window
    {
        private readonly string _downloadUrl;

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
}