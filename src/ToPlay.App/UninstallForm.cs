using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToPlay.App;

/// <summary>
/// The dark-themed uninstaller shown by "ToPlay.exe --uninstall" (wired into
/// Add/Remove Programs by the installer). It stops the app + host, removes the
/// firewall rules, shortcuts and registry entry, then hands off to a tiny
/// self-deleting script that removes the install folder once this process exits.
/// </summary>
public sealed class UninstallForm : Form
{
    private const string AppName = "ToPlay";

    private readonly string _installDir;
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _txtLog = new();
    private readonly Button _btnUninstall;
    private readonly Button _btnCancel;

    private readonly ConcurrentQueue<string> _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    public UninstallForm()
    {
        _installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        Text = "Uninstall ToPlay";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 440);
        BackColor = Color.FromArgb(11, 15, 23);
        ForeColor = Color.FromArgb(230, 237, 247);
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _title.Text = "Uninstall ToPlay";

        _title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _title.Location = new Point(20, 16);
        _title.AutoSize = true;
        Controls.Add(_title);

        _subtitle.Text = "This removes ToPlay, its firewall rules and shortcuts from this PC.";
        _subtitle.Location = new Point(22, 52);
        _subtitle.AutoSize = true;
        _subtitle.ForeColor = Color.FromArgb(138, 160, 192);
        Controls.Add(_subtitle);

        Controls.Add(new Label
        {
            Text = "Location: " + _installDir,
            Location = new Point(22, 76),
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 130, 160)
        });

        _btnUninstall = MakeButton("Uninstall", 20, 104, 150);
        _btnUninstall.BackColor = Color.FromArgb(176, 46, 46);
        _btnUninstall.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _btnUninstall.Size = new Size(150, 38);
        _btnUninstall.Click += async (_, _) => await UninstallAsync();

        _btnCancel = MakeButton("Cancel", 180, 104, 100);
        _btnCancel.Size = new Size(100, 38);
        _btnCancel.Click += (_, _) => Close();

        _progress.Location = new Point(20, 154);
        _progress.Size = new Size(520, 14);
        _progress.Style = ProgressBarStyle.Continuous;
        Controls.Add(_progress);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Location = new Point(20, 178);
        _txtLog.Size = new Size(520, 240);
        _txtLog.BackColor = Color.FromArgb(6, 10, 16);
        _txtLog.ForeColor = Color.FromArgb(200, 214, 235);
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtLog);

        _timer.Interval = 150;
        _timer.Tick += (_, _) => Drain();
        _timer.Start();

        Load += (_, _) => Log("Ready to uninstall ToPlay.");
    }

    private Button MakeButton(string text, int x, int y, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(36, 49, 74)
        };
        b.FlatAppearance.BorderSize = 0;
        Controls.Add(b);
        return b;
    }

    private async Task UninstallAsync()
    {
        _btnUninstall.Enabled = false;
        _btnCancel.Enabled = false;
        _progress.Style = ProgressBarStyle.Marquee;

        try
        {
            await Task.Run(DoUninstall);

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            Log("");
            Log("ToPlay has been uninstalled. You can close this window.");
            _btnCancel.Text = "Close";

            _btnCancel.Enabled = true;
        }
        catch (Exception ex)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            Log("ERROR: " + ex.Message);
            _btnUninstall.Enabled = true;
            _btnCancel.Enabled = true;
            MessageBox.Show(this, "Uninstall failed:\n\n" + ex.Message, AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoUninstall()
    {
        // 1) Stop the streaming host (leave our own ToPlay.exe running until the end).
        Log("Stopping the streaming host…");
        RunHidden("taskkill", "/IM ToPlay.Host.exe /F");

        // 2) Remove the LAN firewall rules.
        Log("Removing firewall rules…");
        foreach (var proto in new[] { "UDP", "TCP" })
            RunHidden("netsh", $"advfirewall firewall delete rule name=\"ToPlay stream ({proto})\"");
        foreach (var port in new[] { 8080, 8443 })
            RunHidden("netsh", $"advfirewall firewall delete rule name=\"ToPlay ({port})\"");

        // 3) Remove Start Menu / Desktop shortcuts.
        Log("Removing shortcuts…");
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ToPlay.lnk"));
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ToPlay.lnk"));

        // 4) Remove the Add/Remove Programs registry entry.
        Log("Removing registry entry…");
        RunHidden("reg", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ToPlay\" /f");

        // 5) Hand off folder deletion to a detached script: it waits for THIS
        //    process (and the host) to exit, then removes the install folder and
        //    finally deletes itself. We can't delete our own running .exe directly.
        Log("Scheduling removal of the program files…");
        ScheduleFolderCleanup();
    }

    private void ScheduleFolderCleanup()
    {
        var cmdPath = Path.Combine(Path.GetTempPath(), "toplay-cleanup-" + Guid.NewGuid().ToString("n") + ".cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine($"set \"TARGET={_installDir}\"");
        sb.AppendLine("rem Wait until ToPlay.exe / ToPlay.Host.exe have fully exited.");
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq ToPlay.exe\" 2>nul | find /I \"ToPlay.exe\" >nul && (timeout /t 1 /nobreak >nul & goto waitloop)");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq ToPlay.Host.exe\" 2>nul | find /I \"ToPlay.Host.exe\" >nul && (timeout /t 1 /nobreak >nul & goto waitloop)");
        sb.AppendLine("rem Give the OS a moment to release file handles, then remove the folder.");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("rmdir /s /q \"%TARGET%\"");
        sb.AppendLine("rem Delete this script last.");
        sb.AppendLine("del \"%~f0\" >nul 2>&1");
        File.WriteAllText(cmdPath, sb.ToString());

        // Launch detached and hidden. As a child of this elevated process it
        // inherits admin rights, so it can delete files under Program Files.
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log($"  (could not delete {path}: {ex.Message})"); }
    }

    private void RunHidden(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(20000);
        }
        catch (Exception ex) { Log($"{file} error: {ex.Message}"); }
    }

    // ======================= log plumbing =======================
    private void Log(string text) => _log.Enqueue(text);

    private void Drain()
    {
        if (_log.IsEmpty) return;
        var sb = new StringBuilder();
        while (_log.TryDequeue(out var line)) sb.AppendLine(line);
        _txtLog.AppendText(sb.ToString());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // If cleanup was scheduled, closing this window lets the detached script
        // finish removing the install folder.
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
