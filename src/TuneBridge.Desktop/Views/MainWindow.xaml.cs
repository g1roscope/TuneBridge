using System.Windows;
using System.Windows.Controls;
using TuneBridge.Desktop.ViewModels;

namespace TuneBridge.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void OnDestSpotify(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Destination = "spotify";
    }
}
