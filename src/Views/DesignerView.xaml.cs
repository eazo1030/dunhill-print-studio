using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

public partial class DesignerView : UserControl
{
    public DesignerView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DesignerViewModel>();
    }
}