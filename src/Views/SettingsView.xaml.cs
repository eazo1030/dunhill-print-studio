using System.Windows;
using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }

    private void AuthBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.AuthToken = AuthBox.Password;
    }
}
