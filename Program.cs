using System;
using System.Linq;
using System.Threading;
using Velopack;

namespace SlimPenHaptics;

static class Program
{
    private const string InstanceName = "SlimPenHaptics.Instance";
    private const string ShowName     = "SlimPenHaptics.ShowWindow";

    [STAThread]
    static int Main(string[] args)
    {
        // Must run first: handles the installer's install/update/uninstall hooks.
        VelopackApp.Build().Run();

        // Diagnostic: fire one click and exit. 0 = sent, 1 = pen not found, 2 = write failed.
        if (args.Contains("--buzz"))
        {
            using var haptic = new HapticEngine();
            if (!haptic.Connect()) return 1;
            haptic.Buzz(Waveform.Click);
            Thread.Sleep(500);     // let the background writer send it
            return haptic.LastWriteOk ? 0 : 2;
        }

        // One copy at a time. Launching it again (say from the Start menu) asks the
        // running copy to open its window.
        using var instance = new Mutex(true, InstanceName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        if (!first)
        {
            show.Set();
            return 0;
        }

        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
            ThemeMode    = System.Windows.ThemeMode.System,
        };

        using var tray = new TrayApp(background: args.Contains(TrayApp.BackgroundFlag));

        var listener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    show.WaitOne();
                    app.Dispatcher.BeginInvoke(tray.ShowWindow);
                }
            }
            catch (Exception)
            {
                // The handle goes away when the app exits.
            }
        })
        { IsBackground = true, Name = "ShowWindowListener" };
        listener.Start();

        app.Run();
        return 0;
    }
}
