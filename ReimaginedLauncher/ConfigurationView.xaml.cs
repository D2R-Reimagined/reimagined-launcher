using System.Windows;
using System.Windows.Controls;

namespace ReimaginedLauncher;

public partial class ConfigurationView : UserControl
{
    public ConfigurationView()
    {
        InitializeComponent();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ((MainWindow)Application.Current.MainWindow).ShowMainView();
    }
}