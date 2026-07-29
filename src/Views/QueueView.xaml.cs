using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<QueueViewModel>();
    }
}
