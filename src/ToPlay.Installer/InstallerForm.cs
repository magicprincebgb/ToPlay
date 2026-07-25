using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;

using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ToPlay.Installer;

/// <summary>
/// ToPlaySetup.exe — a self-contained installer. It carries the published app
/// (ToPlay.exe + ToPlay.Host.exe + runtime + wwwroot) as an embedded zip and,
/// on install: extracts it to Program Files, (optionally) downloads ffmpeg,
/// opens the LAN firewall, and creates Start Menu / Desktop shortcuts.
/// </summary>
public sealed class InstallerForm : Form
{
    private const string PayloadResource = "ToPlay.payload.zip";
    private const string AppName = "ToPlay";

    private readonly TextBox _txtPath = new();
    private readonly CheckBox _chkFfmpeg = new();
    private readonly CheckBox _chkFirewall = new();
    private readonly CheckBox _chkDesktop = new();
    private readonly CheckBox _chkLaunch = new();
    private readonly Button _btnBrowse;
    private readonly Button _btnInstall;
    private readonly Button _btnClose;
    private readonly ProgressBar _progress = new();
    private readonly TextBox _txtLog = new();

    private readonly ConcurrentQueue<string> _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private string _installedExe = "";

    public InstallerForm()
    {
        Text = "ToPlay Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size(620, 560);
        BackColor = Color.FromArgb(11, 15, 23);
        ForeColor = Color.FromArgb(230, 237, 247);
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var accent = Color.FromArgb(31, 111, 235);

        var title = new Label
        {
            Text = "Install ToPlay",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Play your PC on your phone — this installs the app and everything it needs.",
            Location = new Point(22, 52),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(subtitle);

        Controls.Add(new Label
        {
            Text = "Install location",
            Location = new Point(20, 90),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        });

        _txtPath.Location = new Point(20, 110);
        _txtPath.Size = new Size(470, 26);
        _txtPath.BackColor = Color.FromArgb(13, 19, 30);
        _txtPath.ForeColor = Color.FromArgb(230, 237, 247);
        _txtPath.BorderStyle = BorderStyle.FixedSingle;
        _txtPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        Controls.Add(_txtPath);

        _btnBrowse = MakeButton("Browse…", 500, 109, 100);
        _btnBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Choose install folder" };
            if (d.ShowDialog(this) == DialogResult.OK)
                _txtPath.Text = Path.Combine(d.SelectedPath, AppName);
        };

        int y = 150;
        _chkFfmpeg.Text = "Set up ffmpeg (uses the bundled copy — only downloads if missing)";

        _chkFirewall.Text = "Open Windows Firewall for your local network";
        _chkDesktop.Text = "Create a Desktop shortcut";
        _chkLaunch.Text = "Launch ToPlay when setup finishes";
        foreach (var c in new[] { _chkFfmpeg, _chkFirewall, _chkDesktop, _chkLaunch })
        {
            c.Location = new Point(20, y);
            c.AutoSize = true;
            c.Checked = true;
            c.ForeColor = Color.FromArgb(230, 237, 247);
            Controls.Add(c);
            y += 28;
        }

        _btnInstall = MakeButton("Install", 20, y + 6, 150);
        _btnInstall.BackColor = accent;
        _btnInstall.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _btnInstall.Size = new Size(150, 38);
        _btnInstall.Click += async (_, _) => await InstallAsync();

        _btnClose = MakeButton("Close", 180, y + 6, 100);
        _btnClose.Size = new Size(100, 38);
        _btnClose.Click += (_, _) => Close();

        _progress.Location = new Point(20, y + 54);
        _progress.Size = new Size(580, 14);
        _progress.Style = ProgressBarStyle.Continuous;
        Controls.Add(_progress);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Location = new Point(20, y + 78);
        _txtLog.Size = new Size(580, 560 - (y + 78) - 16);
        _txtLog.BackColor = Color.FromArgb(6, 10, 16);
        _txtLog.ForeColor = Color.FromArgb(200, 214, 235);
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtLog);

        _timer.Interval = 150;
        _timer.Tick += (_, _) => Drain();
        _timer.Start();

        Load += (_, _) =>
        {
            Log("Ready to install.");
            if (GetPayload() == null)
                Log("WARNING: no application payload is embedded in this build. " +
                    "Use scripts\\build-installer.cmd to produce a real ToPlaySetup.exe.");
        };
    }

    private Button MakeButton(string text, int x, int yy, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, yy),
            Size = new Size(w, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(36, 49, 74)
        };
        b.FlatAppearance.BorderSize = 0;
        Controls.Add(b);
        return b;
    }

    // ======================= install pipeline =======================
    private async Task InstallAsync()
    {
        var installDir = _txtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show(this, "Please choose an install folder.", AppName);
            return;
        }
        if (GetPayload() == null)
        {
            MessageBox.Show(this,
                "This installer has no embedded application payload.\n" +
                "Build it with scripts\\build-installer.cmd.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(true);
        _progress.Style = ProgressBarStyle.Marquee;
        try
        {
            bool ffmpeg = _chkFfmpeg.Checked;
            bool firewall = _chkFirewall.Checked;
            bool desktop = _chkDesktop.Checked;

            await Task.Run(() => ExtractPayload(installDir));

            _installedExe = Path.Combine(installDir, "ToPlay.exe");
            if (!File.Exists(_installedExe))
                throw new FileNotFoundException("ToPlay.exe was not found after extraction.", _installedExe);

            if (ffmpeg) await EnsureFfmpegAsync(installDir);
            if (firewall) await Task.Run(OpenFirewall);

            await Task.Run(() => CreateShortcuts(installDir, desktop));
            await Task.Run(() => WriteUninstaller(installDir));

            Log("");
            Log("==================================================");
            Log("  Installation complete.");
            Log($"  Installed to: {installDir}");
            Log("  Launch ToPlay from the Start Menu or Desktop.");
            Log("==================================================");

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;

            if (_chkLaunch.Checked) LaunchApp();
            _btnClose.Text = "Finish";
        }
        catch (Exception ex)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, "Installation failed:\n\n" + ex.Message, AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        // NOTE: don't set Enabled=false on the check boxes / labels — WinForms then
        // paints their text in the greyed "disabled" system colour, which looks
        // washed-out on our dark theme. We only lock the buttons and make the path
        // box read-only (which keeps its text at full contrast).
        _btnInstall.Enabled = !busy;
        _btnInstall.Text = busy ? "Installing…" : "Install";
        _btnBrowse.Enabled = !busy;
        _txtPath.ReadOnly = busy;
    }


    private static Stream? GetPayload()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceStream(PayloadResource);
    }

    private void ExtractPayload(string installDir)
    {
        // Stop any running instance so we can overwrite files.
        foreach (var name in new[] { "ToPlay", "ToPlay.Host" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    Log($"Stopping running {name}…");
                    try { p.Kill(true); p.WaitForExit(4000); } catch { }
                }
            }
            catch { }
        }

        Log($"Extracting application to {installDir} …");
        Directory.CreateDirectory(installDir);

        using var payload = GetPayload()!;
        // Copy to a seekable temp file (ZipArchive prefers seekable streams).
        var tmpZip = Path.Combine(Path.GetTempPath(), "toplay-payload-" + Guid.NewGuid().ToString("n") + ".zip");
        try
        {
            using (var fs = File.Create(tmpZip)) payload.CopyTo(fs);
            using var archive = ZipFile.OpenRead(tmpZip);
            int total = archive.Entries.Count, done = 0;
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
                    continue; // guard against zip-slip
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (++done % 25 == 0) Log($"  …{done}/{total} files");
            }
            Log($"Extracted {total} files.");
        }
        finally
        {
            try { File.Delete(tmpZip); } catch { }
        }
    }

    private async Task EnsureFfmpegAsync(string installDir)
    {
        var dest = Path.Combine(installDir, "tools", "ffmpeg.exe");

        // 1) Bundled with the installer (the normal case) — nothing to download.
        if (File.Exists(dest)) { Log("ffmpeg is bundled with ToPlay — no download needed."); return; }

        // 2) Already installed on this PC (on PATH) — reuse it instead of downloading.
        var existing = FindFfmpegOnPath();
        if (existing != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(existing, dest, true);
                Log($"Found ffmpeg already on this PC ({existing}) — copied it, no download needed.");
                return;
            }
            catch (Exception ex) { Log("Could not reuse the existing ffmpeg (" + ex.Message + "); will download instead."); }
        }

        // 3) Last resort — download it once.
        Log("ffmpeg not bundled and not found on this PC — downloading (~90 MB, one time)…");

        var url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        var tmp = Path.Combine(Path.GetTempPath(), "toplay-ffmpeg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, "ffmpeg.zip");
        try
        {
            await DownloadFileAsync(url, zip);

            SetMarquee(true);
            Log("Extracting ffmpeg…");
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(zip, tmp, true);
                var found = Directory.EnumerateFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (found == null) { Log("WARNING: ffmpeg.exe not found in the archive."); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(found, dest, true);
                Log("Installed ffmpeg -> " + dest);
            });
        }
        catch (Exception ex)
        {
            Log("WARNING: ffmpeg download failed (" + ex.Message +
                "). You can run \"First-time setup\" inside ToPlay later.");
        }
        finally
        {
            SetMarquee(true);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// Streams a download to disk with a live progress bar and periodic log lines.
    /// A real User-Agent matters: some CDNs (gyan.dev included) will hang a
    /// connection that sends none — which is what made the download run forever.
    /// </summary>
    private async Task DownloadFileAsync(string url, string dest)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ToPlay-Installer/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1L;
        SetMarquee(total <= 0);       // indeterminate only if we don't know the size

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var fs = File.Create(dest);

        var buffer = new byte[81920];
        long received = 0;
        int lastPct = -1, n;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n));
            received += n;
            if (total > 0)
            {
                int pct = (int)(received * 100 / total);
                SetProgress(pct);
                if (pct >= lastPct + 5) { Log($"  …{pct}%  ({received / 1048576} / {total / 1048576} MB)"); lastPct = pct; }
            }
            else if (sw.ElapsedMilliseconds >= 1000)
            {
                Log($"  …{received / 1048576} MB downloaded"); sw.Restart();
            }
        }
        await fs.FlushAsync();
        Log($"Downloaded {received / 1048576} MB.");
    }

    /// <summary>Returns the first ffmpeg.exe found on PATH, or null.</summary>
    private static string? FindFfmpegOnPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return null;
    }

    private void SetProgress(int pct)

    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(pct))); return; }
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Min(100, Math.Max(0, pct));
    }

    private void SetMarquee(bool on)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetMarquee(on))); return; }
        _progress.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
    }


    private void OpenFirewall()
    {
        int[] ports = { 8080, 8443 };
        Log("Configuring Windows Firewall for the LAN…");

        RunHidden("powershell", "-NoProfile -ExecutionPolicy Bypass -Command " +
            "\"Get-NetConnectionProfile | Where-Object {$_.NetworkCategory -eq 'Public'} | " +
            "Set-NetConnectionProfile -NetworkCategory Private\"");

        foreach (var port in ports)
        {
            var name = $"ToPlay ({port})";
            RunHidden("netsh", $"advfirewall firewall delete rule name=\"{name}\"");
            RunHidden("netsh", $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}");
            Log($"  opened inbound TCP {port}.");
        }
    }

    // ======================= shortcuts & uninstall =======================
    private void CreateShortcuts(string installDir, bool desktop)
    {
        var exe = Path.Combine(installDir, "ToPlay.exe");
        var icon = File.Exists(Path.Combine(installDir, "app.ico"))
            ? Path.Combine(installDir, "app.ico")
            : exe;

        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
        MakeShortcut(startMenu, exe, installDir, "Play your PC on your phone", icon);
        Log("Created Start Menu shortcut.");

        if (desktop)
        {
            var deskDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var desk = Path.Combine(deskDir, AppName + ".lnk");
            MakeShortcut(desk, exe, installDir, "Play your PC on your phone", icon);
            Log("Created Desktop shortcut.");
        }
    }

    private void MakeShortcut(string lnkPath, string target, string workDir, string desc, string iconPath)
    {
        // Drive the WScript.Shell COM object from a tiny temp PowerShell script
        // so paths with spaces are handled without brittle inline quoting.
        var ps = Path.Combine(Path.GetTempPath(), "toplay-lnk-" + Guid.NewGuid().ToString("n") + ".ps1");
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference='Stop'")
            .AppendLine("$s = (New-Object -ComObject WScript.Shell).CreateShortcut(" + PsStr(lnkPath) + ")")
            .AppendLine("$s.TargetPath = " + PsStr(target))
            .AppendLine("$s.WorkingDirectory = " + PsStr(workDir))
            .AppendLine("$s.Description = " + PsStr(desc))
            .AppendLine("$s.IconLocation = " + PsStr(iconPath + ",0"))
            .AppendLine("$s.Save()")
            .ToString();
        try
        {
            File.WriteAllText(ps, script);
            RunHidden("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps}\"");
        }
        finally { try { File.Delete(ps); } catch { } }
    }

    // Single-quote a string for PowerShell (escape embedded single quotes).
    private static string PsStr(string s) => "'" + s.Replace("'", "''") + "'";

    private void WriteUninstaller(string installDir)
    {
        try
        {
            var uninstall = Path.Combine(installDir, "uninstall.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions");
            sb.AppendLine($"set \"TARGET={installDir}\"");
            sb.AppendLine("rem Relaunch from TEMP so we can delete our own folder.");
            sb.AppendLine("if /I not \"%~f0\"==\"%TEMP%\\toplay-uninstall.cmd\" (");
            sb.AppendLine("  copy /y \"%~f0\" \"%TEMP%\\toplay-uninstall.cmd\" >nul");
            sb.AppendLine("  start \"\" /min cmd /c \"%TEMP%\\toplay-uninstall.cmd\"");
            sb.AppendLine("  exit /b");
            sb.AppendLine(")");
            sb.AppendLine("taskkill /IM ToPlay.exe /F >nul 2>&1");
            sb.AppendLine("taskkill /IM ToPlay.Host.exe /F >nul 2>&1");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("netsh advfirewall firewall delete rule name=\"ToPlay (8080)\" >nul 2>&1");
            sb.AppendLine("netsh advfirewall firewall delete rule name=\"ToPlay (8443)\" >nul 2>&1");
            sb.AppendLine("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ToPlay\" /f >nul 2>&1");
            sb.AppendLine("del \"%PUBLIC%\\Desktop\\ToPlay.lnk\" >nul 2>&1");
            sb.AppendLine("del \"%ALLUSERSPROFILE%\\Microsoft\\Windows\\Start Menu\\Programs\\ToPlay.lnk\" >nul 2>&1");
            sb.AppendLine("rmdir /s /q \"%TARGET%\"");
            File.WriteAllText(uninstall, sb.ToString());

            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ToPlay");
            if (key != null)
            {
                var exe = Path.Combine(installDir, "ToPlay.exe");
                key.SetValue("DisplayName", "ToPlay");
                // Derived from the assembly version so it can never go stale.
                key.SetValue("DisplayVersion",
                    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0");
                key.SetValue("Publisher", "ToPlay");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", exe);
                // Uninstall through the app's built-in dark-themed GUI. Keep the
                // legacy uninstall.cmd on disk as a silent fallback.
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            }
            Log("Registered in Add/Remove Programs.");
        }
        catch (Exception ex)
        {
            Log("Note: could not register uninstaller (" + ex.Message + ").");
        }
    }

    private void LaunchApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_installedExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_installedExe)!
            });
            Log("Launched ToPlay.");
        }
        catch (Exception ex) { Log("Could not launch ToPlay: " + ex.Message); }
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
            p?.WaitForExit(30000);
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
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
