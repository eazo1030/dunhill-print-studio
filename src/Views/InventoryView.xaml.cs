using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<InventoryViewModel>();
    }
}
