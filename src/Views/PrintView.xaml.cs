using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class PrintView : UserControl
{
    public PrintView(PrintViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
