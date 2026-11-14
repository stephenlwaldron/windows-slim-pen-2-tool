using System;
using System.Windows.Forms;

namespace SlimPenHaptics;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostic: fire one click and exit. 0 = sent, 1 = pen not found, 2 = write failed.
        if (args.Length > 0 && args[0] == "--buzz")
        {
            using var haptic = new HapticEngine();
            if (!haptic.Connect()) return 1;
            haptic.Buzz(Waveform.Click);
            System.Threading.Thread.Sleep(500);     // let the background writer send it
            return haptic.IsConnected ? 0 : 2;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
        return 0;
    }
}
