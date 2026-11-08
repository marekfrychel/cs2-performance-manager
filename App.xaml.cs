using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace CS2PerformanceManager;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\CS2PerformanceManager", out bool firstInstance);
        if (!firstInstance)
        {
            if (!e.Args.Contains("--tray"))
                MessageBox.Show("Menedżer działa już w zasobniku systemowym.", "CS2 Performance Manager");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--tray"))
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
