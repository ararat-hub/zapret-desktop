using System.Diagnostics;
using System.Windows;

namespace ZapretDesktop;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--handoff" && int.TryParse(args[1], out int pid))
        {
            try
            {
                using var parent = Process.GetProcessById(pid);
                parent.WaitForExit(5000);
            }
            catch (ArgumentException) { }
            args = args[2..];
        }

        instance = new Mutex(false, @"Local\ZapretDesktop");
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (!args.Contains("--startup"))
                MessageBox.Show("Zapret Desktop уже открыт.", "Zapret Desktop");
            Shutdown();
            return;
        }

        MainWindow = new MainWindow(args);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
