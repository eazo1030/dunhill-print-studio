using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

public partial class PrintView : UserControl
{
    public PrintView()
    {
        InitializeComponent();
        // WPF XAML loader creates views via the parameterless constructor.
        // Resolve the VM from the DI container here so each tab gets its own VM instance.
        DataContext = App.Services.GetRequiredService<PrintViewModel>();
    }
}
