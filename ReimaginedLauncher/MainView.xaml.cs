using System.Windows.Controls;

namespace ReimaginedLauncher;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void ConfigureButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ((MainWindow)System.Windows.Application.Current.MainWindow).ShowConfigurationView();
    }
}