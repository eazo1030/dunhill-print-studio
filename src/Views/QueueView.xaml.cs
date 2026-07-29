using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class QueueView : UserControl
{
    public QueueView(QueueViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
