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

        Application.Run(new MainForm());
    }
}
