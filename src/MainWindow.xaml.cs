using System.Windows;
using System.Windows.Threading;
using Dunhill.PrintStudio.Services;

namespace Dunhill.PrintStudio;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock;

    public MainWindow(PrintService print)
    {
        InitializeComponent();
        // `MainWindow` is created by DI in App.OnStartup — constructor injection is fine here.
        // (The 5 tab views can't use constructor injection because WPF's XAML loader
        // creates them via the parameterless constructor — see their code-behind.)

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clock.Start();

        // Reflect printer status into the header dot
        print.StatusChanged += (_, s) =>
        {
            StatusDot.Fill = s.Online
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            PrinterStatusText.Text = s.Online
                ? $"{s.Model}  •  {s.ConnectionType}"
                : "Disconnected";
        };
    }
}
