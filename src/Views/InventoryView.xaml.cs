using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class InventoryView : UserControl
{
    public InventoryView(InventoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
