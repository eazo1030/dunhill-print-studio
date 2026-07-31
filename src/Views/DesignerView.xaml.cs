using System.Windows.Controls;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

/// <summary>
/// Designer tab — v1.2.5: stripped to a fixed-layout fill-in form.
/// No drag, no add-element, no properties panel. The view-model
/// exposes FabricName / Yardage / Po / DatePrinted and updates the
/// PPLZ preview on every keystroke.
/// </summary>
public partial class DesignerView : UserControl
{
    public DesignerView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DesignerViewModel>();
    }
}
