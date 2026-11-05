using System.Windows;

namespace DisplayBrightness;

public partial class App : Application
{
    private const string InstanceName = @"Local\DisplayBrightness-8B06AD97";
    private Mutex? instance;
    private EventWaitHandle? showWindow;
    private RegisteredWaitHandle? showRegistration;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool silent = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-Show");
        instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            if (!silent)
                showWindow.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showWindow,
            (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        if (!silent)
            window.Show();

        await window.InitializeAsync(silent);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showRegistration?.Unregister(null);
        showWindow?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
