using System.Windows;
using System.Windows.Threading;
using MaMini.App.Services;
using MaMini.Core.Diagnostics;
using MaMini.Core.Settings;

namespace MaMini.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstance("MaMini-7C3E1B52");
        if (!_singleInstance.TryAcquire())
        {
            // Another instance is running: ask it to show itself and quit.
            _singleInstance.SignalExisting();
            Shutdown();
            return;
        }

        Log.Initialize(System.IO.Path.Combine(SettingsService.DefaultDirectory, "mamini.log"));
        Log.Info($"MA Mini {typeof(App).Assembly.GetName().Version} starting.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        // A tiny widget gains nothing from the GPU pipeline, which costs hundreds of MB of driver memory and threads.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        _controller = new AppController(this);
        _singleInstance.ShowRequested += (_, _) => Dispatcher.BeginInvoke(() => _controller.ShowWidget());
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("MA Mini exited.");
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep a tiny tray utility alive rather than crashing on an unexpected UI error.
        Log.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }
}
