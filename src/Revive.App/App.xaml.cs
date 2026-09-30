using System.Windows;
using System.Windows.Threading;

namespace Revive.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app (and any results the user hasn't recovered yet) alive.
        MessageBox.Show($"Something went wrong: {e.Exception.Message}", "Revive", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
