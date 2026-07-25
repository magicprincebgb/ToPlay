using System;
using System.Linq;
using System.Windows.Forms;

namespace ToPlay.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Uninstall mode: Add/Remove Programs launches "ToPlay.exe --uninstall".
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                       || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new UninstallForm());
            return;
        }

        // Launched by the "auto-start at logon" scheduled task: start hidden in
        // the system tray and bring the streaming server up automatically.
        bool startMinimized = args.Any(a =>
               a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)
            || a.Equals("/autostart", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startMinimized));

    }
}
