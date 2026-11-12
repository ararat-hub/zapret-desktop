using System.Diagnostics;
using System.Windows;

namespace ZapretDesktop;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    protected override async void OnStartup(StartupEventArgs e)
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

        bool background = args is ["--startup" or "--login"];
        try
        {
            instance = new Mutex(false, @"Local\ZapretDesktop");
            ownsInstance = instance.WaitOne(0);
        }
        catch (AbandonedMutexException) { ownsInstance = true; }
        catch (UnauthorizedAccessException) { }
        if (!ownsInstance)
        {
            if (!background)
                MessageBox.Show("Zapret Desktop уже открыт. Если окно свёрнуто, откройте его через значок в трее.", "Zapret Desktop");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (background)
            window.HideToTray();
        else
            window.Show();
        await window.InitializeAsync(args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
