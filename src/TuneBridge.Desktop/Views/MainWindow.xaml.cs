using System.Windows;
using TuneBridge.Desktop.ViewModels;

namespace TuneBridge.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
