using System.Windows;
using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void AuthBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.AuthToken = AuthBox.Password;
    }
}
