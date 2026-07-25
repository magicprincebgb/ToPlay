using System;
using System.Windows.Forms;

namespace ToPlay.Installer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Normal double-click run: no arguments at all.
        // In-app updater run: --update "C:\Program Files\ToPlay"
        var unattended = false;
        string? targetDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = (args[i] ?? string.Empty).Trim();
            if (a.Length == 0) continue;

            if (a.Equals("--update", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/S", StringComparison.OrdinalIgnoreCase))
            {
                unattended = true;

                // An immediately following non-flag value is the install folder.
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-") && !args[i + 1].StartsWith("/"))
                {
                    targetDir = args[++i].Trim().Trim('"');
                }
                continue;
            }

            if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                targetDir = a.Substring("--dir=".Length).Trim().Trim('"');
            }
        }

        Application.Run(new InstallerForm(unattended, targetDir));
    }
}
