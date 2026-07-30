using System.IO;
using Velopack;

namespace Dunhill.PrintStudio;

/// <summary>
/// Custom entry point. Must call <see cref="VelopackApp.Build()"/>.
/// <c>Run()</c> FIRST so install / uninstall / restart hooks can short-circuit
/// the process with the correct exit code before WPF starts up. Without this
/// first call the hooks would fire after WPF begins, causing mysterious
/// double-launch behavior on Windows.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's documented startup contract: Run() must be the very first
        // line in Main. It inspects args for hook flags (--veloapp, --uninstall,
        // etc.) and exits the process from inside Run() if one matches.
        VelopackApp.Build()
            .OnFirstRun(_ => { /* first run after install: nothing to do yet */ })
            .OnRestarted(_ => { /* restarted after update: nothing to do yet */ })
            .Run();

        // Normal startup path. Hand off to WPF's Application.Run() — App.ctor
        // and OnStartup will fire as usual.
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}