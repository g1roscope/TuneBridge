using System.Windows;
using System.Windows.Threading;
using TuneBridge.Infrastructure.Diagnostics;

namespace TuneBridge.Desktop;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.Write(e.Exception, "wpf.dispatcher.unhandled");
        // Keep WPF's default fatal-error behavior; do not hide an unknown UI failure.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            DiagnosticsLogger.Write(exception, "runtime.domain.unhandled");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticsLogger.Write(e.Exception, "runtime.task.unobserved");
        e.SetObserved();
    }
}
