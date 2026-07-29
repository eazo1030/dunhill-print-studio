using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class HistoryView : UserControl
{
    public HistoryView(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
