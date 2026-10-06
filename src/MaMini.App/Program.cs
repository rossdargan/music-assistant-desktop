using MaMini.App.Services;
using Velopack;

namespace MaMini.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AutostartService.Apply(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
