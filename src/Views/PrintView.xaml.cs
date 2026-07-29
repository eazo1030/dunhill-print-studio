using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

public partial class PrintView : UserControl
{
    public PrintView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<PrintViewModel>();
    }
}
