using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToPlay.App;

/// <summary>
/// ToPlay Control Panel — the GUI shipped as ToPlay.exe. Starts/stops the
/// streaming host, edits the main settings, shows the phone URL and a live log.
/// </summary>
public sealed class MainForm : Form
{
    // ---- paths / environment ----
    private readonly string _hostExe;
    private readonly string _hostDir;
    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly string _toolsFfmpeg;
    private readonly string? _hostCsproj;   // set only when running from source (dev)
    private readonly string? _dotnet;       // .NET SDK for dev builds (optional)
    private readonly bool _isAdmin;

    // ---- runtime state ----
    private Process? _proc;
    private readonly ConcurrentQueue<string> _log = new();
    private string[] _presetIds = Array.Empty<string>();

    // ---- controls ----
    private readonly Label _status = new();
    private readonly TextBox _txtUrl = new();
    private readonly ComboBox _cmbQuality = new();
    private readonly ComboBox _cmbEncoder = new();
    private readonly NumericUpDown _numMonitor = new();
    private readonly NumericUpDown _numHttp = new();
    private readonly NumericUpDown _numHttps = new();
    private readonly CheckBox _chkHttps = new();
    private readonly TextBox _txtLog = new();
    private readonly Button _btnStart, _btnStop, _btnRestart, _btnRebuild, _btnSetup, _btnAccounts;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly NotifyIcon _tray = new();
    private bool _trayHintShown;


    private static readonly string[] Encoders = { "Auto", "Nvenc", "QuickSync", "Amf", "Software" };

    public MainForm()
    {
        _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        (_hostExe, _hostCsproj) = ResolveHost();
        _hostDir = Path.GetDirectoryName(_hostExe)!;
        _dataDir = Path.Combine(_hostDir, "data");
        _configPath = Path.Combine(_dataDir, "config.json");
        _toolsFfmpeg = Path.Combine(_hostDir, "tools", "ffmpeg.exe");
        _dotnet = FindDotnet();

        // ----- window chrome -----
        Text = "ToPlay — Control Panel";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 700);
        MinimumSize = new Size(660, 560);
        BackColor = Color.FromArgb(11, 15, 23);
        ForeColor = Color.FromArgb(230, 237, 247);
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var accent = Color.FromArgb(31, 111, 235);

        // header
        var title = new Label
        {
            Text = "ToPlay",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true
        };
        Controls.Add(title);

        _status.Text = "Stopped";
        _status.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _status.Location = new Point(120, 20);
        _status.AutoSize = true;
        _status.ForeColor = Color.Gray;
        Controls.Add(_status);

        // phone URL
        var lblUrl = new Label
        {
            Text = "Open on your phone (same Wi-Fi):",
            Location = new Point(16, 52),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(lblUrl);

        _txtUrl.ReadOnly = true;
        _txtUrl.Location = new Point(16, 72);
        _txtUrl.Size = new Size(430, 26);
        _txtUrl.Font = new Font("Consolas", 11f, FontStyle.Bold);
        _txtUrl.BackColor = Color.FromArgb(13, 19, 30);
        _txtUrl.ForeColor = Color.FromArgb(63, 185, 80);
        _txtUrl.BorderStyle = BorderStyle.FixedSingle;
        _txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_txtUrl);

        var btnCopy = MakeButton("Copy", 456, 71, 70);
        btnCopy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnCopy.Click += (_, _) => { try { Clipboard.SetText(_txtUrl.Text); Log("URL copied to clipboard."); } catch { } };
        var btnOpen = MakeButton("Open here", 532, 71, 90);
        btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnOpen.Click += (_, _) => OpenUrl(_txtUrl.Text.Replace("<your-pc-ip>", "localhost"));

        // settings group
        var grp = new GroupBox
        {
            Text = "Settings (restart the server to apply)",
            Location = new Point(16, 110),
            Size = new Size(728, 150),
            ForeColor = Color.FromArgb(138, 160, 192),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(grp);

        grp.Controls.Add(MakeLabel("Quality", 14, 26));
        _cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbQuality.Location = new Point(14, 46);
        _cmbQuality.Size = new Size(220, 24);
        grp.Controls.Add(_cmbQuality);

        grp.Controls.Add(MakeLabel("Encoder", 250, 26));
        _cmbEncoder.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbEncoder.Location = new Point(250, 46);
        _cmbEncoder.Size = new Size(200, 24);
        _cmbEncoder.Items.AddRange(Encoders);
        grp.Controls.Add(_cmbEncoder);

        grp.Controls.Add(MakeLabel("Monitor", 466, 26));
        _numMonitor.Location = new Point(466, 46);
        _numMonitor.Size = new Size(80, 24);
        _numMonitor.Minimum = 0;
        _numMonitor.Maximum = Math.Max(0, Screen.AllScreens.Length - 1);
        grp.Controls.Add(_numMonitor);

        grp.Controls.Add(MakeLabel("HTTP port", 14, 82));
        _numHttp.Location = new Point(14, 102);
        _numHttp.Size = new Size(100, 24);
        _numHttp.Minimum = 1; _numHttp.Maximum = 65535;
        grp.Controls.Add(_numHttp);

        grp.Controls.Add(MakeLabel("HTTPS port", 130, 82));
        _numHttps.Location = new Point(130, 102);
        _numHttps.Size = new Size(100, 24);
        _numHttps.Minimum = 1; _numHttps.Maximum = 65535;
        grp.Controls.Add(_numHttps);

        _chkHttps.Text = "Use HTTPS (required for iPhone)";
        _chkHttps.Location = new Point(250, 104);
        _chkHttps.AutoSize = true;
        _chkHttps.ForeColor = Color.FromArgb(230, 237, 247);
        grp.Controls.Add(_chkHttps);

        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(574, 100),
            Size = new Size(140, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = accent
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => SaveSettings();
        grp.Controls.Add(btnSave);

        // control buttons
        _btnStart = MakeButton("Start server", 16, 272, 130);
        _btnStart.BackColor = accent;
        _btnStop = MakeButton("Stop", 154, 272, 90);
        _btnRestart = MakeButton("Restart", 252, 272, 90);
        _btnRebuild = MakeButton("Rebuild", 350, 272, 90);
        _btnSetup = MakeButton("First-time setup", 448, 272, 130);
        _btnAccounts = MakeButton("Accounts", 586, 272, 120);

        _btnStart.Click += async (_, _) => await EnsureAndStartAsync();
        _btnStop.Click += (_, _) => StopServer();
        _btnRestart.Click += async (_, _) => { StopServer(); await Task.Delay(800); await EnsureAndStartAsync(); };
        _btnRebuild.Click += async (_, _) => { StopServer(); if (await Task.Run(BuildHost)) await EnsureAndStartAsync(); };
        _btnSetup.Click += async (_, _) => await FirstTimeSetupAsync();
        _btnAccounts.Click += (_, _) => OpenAccounts();

        // log
        var lblLog = new Label
        {
            Text = "Log",
            Location = new Point(16, 316),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(lblLog);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Location = new Point(16, 336);
        _txtLog.Size = new Size(728, 336);
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtLog.BackColor = Color.FromArgb(6, 10, 16);
        _txtLog.ForeColor = Color.FromArgb(200, 214, 235);
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtLog);

        // timer: drain the log queue + detect server exit on the UI thread
        _timer.Interval = 200;
        _timer.Tick += (_, _) => Pump();
        _timer.Start();

        // ---- system tray: minimize here instead of the taskbar ----
        _tray.Text = "ToPlay";
        _tray.Icon = Icon ?? System.Drawing.SystemIcons.Application;
        _tray.Visible = true;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open ToPlay", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Start server", null, async (_, _) => await EnsureAndStartAsync());
        trayMenu.Items.Add("Stop server", null, (_, _) => StopServer());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

        Load += (_, _) =>

        {
            LoadSettingsIntoUi();
            RefreshUrl();
            Log($"ToPlay control panel ready.{(_isAdmin ? "" : "  (NOT elevated — games may ignore input.)")}");
            if (!File.Exists(_hostExe))
                Log(_hostCsproj != null
                    ? "Host not built yet — click Start (it will build), or Rebuild."
                    : "WARNING: ToPlay.Host.exe was not found next to this app.");
            if (!File.Exists(_toolsFfmpeg) && !OnPath("ffmpeg.exe"))
                Log("ffmpeg not found yet — click \"First-time setup\" once.");
        };

        FormClosing += (_, _) =>
        {
            StopServer();
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
        };

    }

    // ======================= helpers: UI factories =======================
    private Button MakeButton(string text, int x, int y, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 34),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(36, 49, 74)
        };
        b.FlatAppearance.BorderSize = 0;
        Controls.Add(b);
        return b;
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = Color.FromArgb(138, 160, 192)
    };

    // ======================= path / env resolution =======================
    private static (string exe, string? csproj) ResolveHost()
    {
        var baseDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(baseDir, "ToPlay.Host.exe");
        if (File.Exists(p1)) return (p1, null);
        var p2 = Path.Combine(baseDir, "host", "ToPlay.Host.exe");
        if (File.Exists(p2)) return (p2, null);

        // dev: walk up to find the host project and use its build output
        var csproj = FindUp(baseDir, Path.Combine("src", "ToPlay.Host", "ToPlay.Host.csproj"));
        if (csproj != null)
        {
            var projDir = Path.GetDirectoryName(csproj)!;
            var rel = Path.Combine(projDir, "bin", "Release", "net8.0", "ToPlay.Host.exe");
            var dbg = Path.Combine(projDir, "bin", "Debug", "net8.0", "ToPlay.Host.exe");
            var exe = (File.Exists(dbg) && !File.Exists(rel)) ? dbg : rel;
            return (exe, csproj);
        }
        return (p1, null);
    }

    private static string? FindUp(string start, string relative)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindDotnet()
    {
        var user = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
        if (File.Exists(user)) return user;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "dotnet.exe");
                if (!string.IsNullOrWhiteSpace(dir) && File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    private static bool OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), exe))) return true; }
            catch { }
        }
        return false;
    }

    // ======================= config read / write =======================
    private JsonObject ReadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(_configPath));
                if (node is JsonObject o) return o;
            }
        }
        catch { }
        return new JsonObject
        {
            ["HttpPort"] = 8080,
            ["HttpsPort"] = 8443,
            ["UseHttps"] = true,
            ["MonitorIndex"] = 0,
            ["Encoder"] = 0,
            ["ActivePresetId"] = "720p60"
        };
    }

    private void SaveSettings()
    {
        var cfg = ReadConfig();
        if (_presetIds.Length > 0 && _cmbQuality.SelectedIndex >= 0)
            cfg["ActivePresetId"] = _presetIds[_cmbQuality.SelectedIndex];
        cfg["Encoder"] = Math.Max(0, _cmbEncoder.SelectedIndex);   // enum index (matches host)
        cfg["MonitorIndex"] = (int)_numMonitor.Value;
        cfg["HttpPort"] = (int)_numHttp.Value;
        cfg["HttpsPort"] = (int)_numHttps.Value;
        cfg["UseHttps"] = _chkHttps.Checked;
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_configPath, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log("Settings saved. Restart the server to apply.");
            RefreshUrl();
        }
        catch (Exception ex) { Log("Could not save settings: " + ex.Message); }
    }

    private void LoadSettingsIntoUi()
    {
        var cfg = ReadConfig();

        // presets
        _cmbQuality.Items.Clear();
        var ids = new System.Collections.Generic.List<string>();
        if (cfg["Presets"] is JsonArray arr && arr.Count > 0)
        {
            foreach (var p in arr)
            {
                var id = p?["Id"]?.ToString() ?? "";
                var name = p?["Name"]?.ToString() ?? id;
                _cmbQuality.Items.Add(name);
                ids.Add(id);
            }
        }
        else
        {
            (string id, string name)[] defs =
            {
                ("720p60", "720p 60fps (lowest latency)"),
                ("1080p60", "1080p 60fps"),
                ("1080p30", "1080p 30fps (smoother wifi)"),
                ("native60", "Native res 60fps"),
            };
            foreach (var d in defs) { _cmbQuality.Items.Add(d.name); ids.Add(d.id); }
        }
        _presetIds = ids.ToArray();
        var activeId = cfg["ActivePresetId"]?.ToString() ?? "720p60";
        var qi = Array.IndexOf(_presetIds, activeId);
        _cmbQuality.SelectedIndex = qi >= 0 ? qi : 0;

        // encoder (accept number or name)
        _cmbEncoder.SelectedIndex = ReadEncoderIndex(cfg);

        var monitor = ReadInt(cfg, "MonitorIndex", 0);
        _numMonitor.Value = Math.Min(Math.Max(monitor, (int)_numMonitor.Minimum), (int)_numMonitor.Maximum);
        _numHttp.Value = Math.Min(Math.Max(ReadInt(cfg, "HttpPort", 8080), 1), 65535);
        _numHttps.Value = Math.Min(Math.Max(ReadInt(cfg, "HttpsPort", 8443), 1), 65535);
        _chkHttps.Checked = ReadBool(cfg, "UseHttps", true);
    }

    private static int ReadInt(JsonObject o, string key, int fallback)
    {
        try { if (o[key] is JsonValue v && v.TryGetValue<int>(out var i)) return i; } catch { }
        try { if (o[key] is JsonValue v && v.TryGetValue<double>(out var d)) return (int)d; } catch { }
        return fallback;
    }

    private static bool ReadBool(JsonObject o, string key, bool fallback)
    {
        try { if (o[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b; } catch { }
        return fallback;
    }

    private static int ReadEncoderIndex(JsonObject o)
    {
        var n = o["Encoder"];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i) && i >= 0 && i < Encoders.Length) return i;
            if (v.TryGetValue<string>(out var s))
            {
                var idx = Array.FindIndex(Encoders, e => string.Equals(e, s, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) return idx;
            }
        }
        return 0;
    }

    // ======================= phone URL =======================
    private void RefreshUrl()
    {
        var cfg = ReadConfig();
        var ip = PrimaryLanIp() ?? "<your-pc-ip>";
        var https = ReadBool(cfg, "UseHttps", true);
        var scheme = https ? "https" : "http";
        var port = https ? ReadInt(cfg, "HttpsPort", 8443) : ReadInt(cfg, "HttpPort", 8080);
        _txtUrl.Text = $"{scheme}://{ip}:{port}/";
    }

    // Mirrors DevCertificate.PrimaryLanIp: prefer the NIC with a gateway, skip virtual adapters.
    private static string? PrimaryLanIp()
    {
        IPAddress? best = null;
        int bestScore = int.MinValue;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                bool virt = LooksVirtual(ni);
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!IsPrivate(ua.Address)) continue;
                    int score = 0;
                    if (hasGateway) score += 100;
                    if (!virt) score += 50;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                    else if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) score += 15;
                    if (score > bestScore) { bestScore = score; best = ua.Address; }
                }
            }
        }
        catch { }
        return best?.ToString();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    private static bool LooksVirtual(NetworkInterface ni)
    {
        var s = (ni.Description + " " + ni.Name).ToLowerInvariant();
        return s.Contains("virtual") || s.Contains("hyper-v") || s.Contains("vmware")
            || s.Contains("virtualbox") || s.Contains("vethernet") || s.Contains("pseudo")
            || s.Contains("loopback") || s.Contains("tap") || s.Contains("tunnel");
    }

    // ======================= start / stop / build =======================
    private bool BuildHost()
    {
        if (_hostCsproj == null) { Log("No source project to build (this is a prebuilt install)."); return false; }
        if (_dotnet == null) { Log("ERROR: .NET 8 SDK not found; cannot build the host."); SetStatus("No .NET SDK", Color.Red); return false; }

        SetStatus("Building…", Color.Orange);
        Log($"Building host (Release) with {_dotnet} …");
        var psi = new ProcessStartInfo(_dotnet, $"build \"{_hostCsproj}\" -c Release --nologo")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!_dotnet.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(_dotnet)!;
        try
        {
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0) { SetStatus("Build failed", Color.Red); return false; }
            Log("Build succeeded.");
            return true;
        }
        catch (Exception ex) { Log("Build error: " + ex.Message); return false; }
    }

    private async Task EnsureAndStartAsync()
    {
        if (_proc is { HasExited: false }) { Log("Server is already running."); return; }
        if (!File.Exists(_hostExe))
        {
            var ok = await Task.Run(BuildHost);
            if (!ok || !File.Exists(_hostExe)) { Log($"ERROR: host not found at {_hostExe}"); return; }
        }
        StartServer();
    }

    private void StartServer()
    {
        Log("Starting server…");
        try
        {
            var psi = new ProcessStartInfo(_hostExe)
            {
                WorkingDirectory = _hostDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            SetStatus("Running", Color.LimeGreen);
            RefreshUrl();
        }
        catch (Exception ex)
        {
            Log("ERROR starting server: " + ex.Message);
            SetStatus("Error", Color.Red);
        }
    }

    private void StopServer()
    {
        if (_proc is { HasExited: false })
        {
            Log("Stopping server…");
            try
            {
                var kill = new ProcessStartInfo("taskkill", $"/PID {_proc.Id} /T /F")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(kill)?.WaitForExit(4000);
            }
            catch { }
        }
        _proc = null;
        SetStatus("Stopped", Color.Gray);
    }

    // ======================= first-time setup =======================
    private async Task FirstTimeSetupAsync()
    {
        _btnSetup.Enabled = false;
        SetStatus("Setting up…", Color.Orange);
        try
        {
            Log("--- First-time setup ---");
            await EnsureFfmpegAsync();
            OpenFirewall();
            Log("Setup complete. Click \"Start server\".");
        }
        catch (Exception ex) { Log("Setup error: " + ex.Message); }
        finally
        {
            SetStatus(_proc is { HasExited: false } ? "Running" : "Stopped",
                      _proc is { HasExited: false } ? Color.LimeGreen : Color.Gray);
            _btnSetup.Enabled = true;
            RefreshUrl();
        }
    }

    private async Task EnsureFfmpegAsync()
    {
        if (File.Exists(_toolsFfmpeg)) { Log("ffmpeg already present."); return; }
        if (OnPath("ffmpeg.exe")) { Log("ffmpeg found on PATH."); return; }

        Log("Downloading ffmpeg (~90 MB, one time)…");
        var url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        var tmp = Path.Combine(Path.GetTempPath(), "toplay-ffmpeg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, "ffmpeg.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zip);
                await resp.Content.CopyToAsync(fs);
            }
            Log("Extracting ffmpeg…");
            ZipFile.ExtractToDirectory(zip, tmp, true);
            var found = Directory.EnumerateFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) { Log("ERROR: ffmpeg.exe not found in the archive."); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_toolsFfmpeg)!);
            File.Copy(found, _toolsFfmpeg, true);
            Log("Installed ffmpeg -> " + _toolsFfmpeg);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private void OpenFirewall()
    {
        if (!_isAdmin) { Log("Not elevated — cannot change the firewall. Re-launch as Administrator."); return; }
        var cfg = ReadConfig();
        int[] ports = { ReadInt(cfg, "HttpPort", 8080), ReadInt(cfg, "HttpsPort", 8443) };

        // Make connected "Public" networks Private so inbound rules apply, then open the ports.
        RunHidden("powershell", "-NoProfile -ExecutionPolicy Bypass -Command " +
            "\"Get-NetConnectionProfile | Where-Object {$_.NetworkCategory -eq 'Public'} | " +
            "Set-NetConnectionProfile -NetworkCategory Private\"");

        foreach (var port in ports.Distinct())
        {
            var name = $"ToPlay ({port})";
            RunHidden("netsh", $"advfirewall firewall delete rule name=\"{name}\"");
            RunHidden("netsh", $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}");
            Log($"Opened inbound TCP {port}.");
        }
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

    // ======================= misc actions =======================
    private void OpenAccounts()
    {
        var cfg = ReadConfig();
        var https = ReadBool(cfg, "UseHttps", true);
        var port = https ? ReadInt(cfg, "HttpsPort", 8443) : ReadInt(cfg, "HttpPort", 8080);
        OpenUrl($"{(https ? "https" : "http")}://localhost:{port}/admin.html");
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log("Open failed: " + ex.Message); }
    }

    // ======================= log plumbing =======================
    private void Log(string text) => _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {text}");

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
        _status.Text = text;
        _status.ForeColor = color;
    }

    // ======================= system tray =======================
    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            try { _tray.ShowBalloonTip(1500, "ToPlay", "Still running — double-click the tray icon to reopen.", ToolTipIcon.Info); } catch { }
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp() => Close();


    private void Pump()
    {
        if (_log.Count > 0)
        {
            var sb = new StringBuilder();
            while (_log.TryDequeue(out var line)) sb.AppendLine(line);
            if (sb.Length > 0)
            {
                _txtLog.AppendText(sb.ToString());
                if (_txtLog.TextLength > 120_000)
                    _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 80_000);
            }
        }
        if (_proc is { HasExited: true })
        {
            Log($"Server exited (code {_proc.ExitCode}).");
            _proc = null;
            SetStatus("Stopped", Color.Gray);
        }
    }
}
