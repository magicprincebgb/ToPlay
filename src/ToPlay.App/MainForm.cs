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
using System.Reflection;
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
    private readonly Button _btnCert;
    private readonly Button _btnBug;
    private readonly CheckBox _chkAutostart = new();
    private readonly CheckBox _chkAutostartServer = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly NotifyIcon _tray = new();
    private bool _trayHintShown;
    private bool _suppressAutostart;
    private bool _forceExit;
    private readonly bool _startMinimized;


    private static readonly string[] Encoders = { "Auto", "Nvenc", "QuickSync", "Amf", "Software" };

    public MainForm(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        (_hostExe, _hostCsproj) = ResolveHost();
        _hostDir = Path.GetDirectoryName(_hostExe)!;
        _dataDir = Path.Combine(_hostDir, "data");
        _configPath = Path.Combine(_dataDir, "config.json");
        _toolsFfmpeg = Path.Combine(_hostDir, "tools", "ffmpeg.exe");
        _dotnet = FindDotnet();

        // ----- window chrome -----
        Text = $"ToPlay — Control Panel  {AppVersion()}";

        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 764);
        MinimumSize = new Size(660, 624);
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

        var version = new Label
        {
            Text = AppVersion(),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(110, 130, 160),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Controls.Add(version);
        version.Location = new Point(ClientSize.Width - version.PreferredWidth - 16, 24);

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

        // second row: auto-start (+ sub-option) + certificate + bug report
        _chkAutostart.Text = "Start ToPlay when I sign in to Windows";
        _chkAutostart.Location = new Point(18, 316);
        _chkAutostart.AutoSize = true;
        _chkAutostart.ForeColor = Color.FromArgb(230, 237, 247);
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            if (!_suppressAutostart) SetAutostart(_chkAutostart.Checked);
            _chkAutostartServer.Enabled = _chkAutostart.Checked;
        };
        Controls.Add(_chkAutostart);

        // sub-option (indented): also bring the streaming server up at sign-in
        _chkAutostartServer.Text = "Also start the streaming server automatically";
        _chkAutostartServer.Location = new Point(38, 340);
        _chkAutostartServer.AutoSize = true;
        _chkAutostartServer.ForeColor = Color.FromArgb(138, 160, 192);
        _chkAutostartServer.CheckedChanged += (_, _) =>
        {
            if (_suppressAutostart) return;
            SetAutostartServer(_chkAutostartServer.Checked);
        };
        Controls.Add(_chkAutostartServer);

        _btnCert = MakeButton("Certificate", 470, 314, 130);
        _btnCert.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnCert.Click += (_, _) => InstallCertificate();

        _btnBug = MakeButton("Report a bug", 608, 314, 136);
        _btnBug.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnBug.Click += (_, _) => { using var f = new BugReportForm(_txtLog.Text, AppVersion()); f.ShowDialog(this); };

        // log
        var lblLog = new Label
        {
            Text = "Log",
            Location = new Point(16, 376),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(lblLog);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Location = new Point(16, 396);
        _txtLog.Size = new Size(728, ClientSize.Height - 396 - 16);

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

            _suppressAutostart = true;
            _chkAutostart.Checked = IsAutostartEnabled();
            _chkAutostartServer.Checked = ReadBool(ReadConfig(), "AutostartServer", true);
            _chkAutostartServer.Enabled = _chkAutostart.Checked;
            _suppressAutostart = false;

            if (_startMinimized)
            {
                var startServer = ReadBool(ReadConfig(), "AutostartServer", true);
                BeginInvoke(new Action(async () =>
                {
                    HideToTray();
                    if (startServer) await EnsureAndStartAsync();
                }));
            }
        };

        FormClosing += MainForm_FormClosing;

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

    /// <summary>Returns the app version formatted as "vMAJOR.MINOR.PATCH".</summary>
    private static string AppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }


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

    private int RunForExitCode(string file, string args)
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
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception ex) { Log($"{file} error: {ex.Message}"); return -1; }
    }

    // ======================= auto-start at logon =======================
    private const string AutostartTaskName = "ToPlay Autostart";

    private bool IsAutostartEnabled()
        => RunForExitCode("schtasks", $"/query /tn \"{AutostartTaskName}\"") == 0;

    private void SetAutostart(bool enable)
    {
        if (!_isAdmin)
        {
            Log("Not elevated — cannot change auto-start. Re-launch ToPlay as Administrator.");
            _suppressAutostart = true;
            _chkAutostart.Checked = IsAutostartEnabled();
            _suppressAutostart = false;
            return;
        }

        if (enable)
        {
            var exe = Application.ExecutablePath;
            // An "onlogon" task with highest run level starts ToPlay elevated at
            // sign-in without a UAC prompt (ToPlay itself requires administrator).
            var rc = RunForExitCode("schtasks",
                $"/create /tn \"{AutostartTaskName}\" /tr \"\\\"{exe}\\\" --autostart\" /sc onlogon /rl highest /f");
            Log(rc == 0
                ? "Auto-start enabled — ToPlay will launch (minimized to the tray) when you sign in to Windows."
                : "Could not enable auto-start (schtasks returned " + rc + ").");
        }
        else
        {
            var rc = RunForExitCode("schtasks", $"/delete /tn \"{AutostartTaskName}\" /f");
            Log(rc == 0 ? "Auto-start disabled." : "Auto-start was already off.");
        }
    }

    // Persists whether the streaming server should also start when ToPlay
    // auto-launches at sign-in (a sub-option of "Start ToPlay when I sign in").
    private void SetAutostartServer(bool enable)
    {
        var cfg = ReadConfig();
        cfg["AutostartServer"] = enable;
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_configPath,
                cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log(enable
                ? "The streaming server will start automatically when ToPlay launches at sign-in."
                : "Auto-start will open ToPlay only — the server won't start until you click \"Start server\".");
        }
        catch (Exception ex) { Log("Could not save the auto-start option: " + ex.Message); }
    }

    // ======================= certificate (trust like Sunshine) =======================
    private void InstallCertificate()
    {
        var caCrt = Path.Combine(_dataDir, "toplay-ca.crt");
        if (!File.Exists(caCrt))
        {
            Log("Certificate not created yet — click \"Start server\" once (ToPlay generates and trusts it automatically), then try again.");
            MessageBox.Show(this,
                "ToPlay hasn't created its certificate yet.\n\n" +
                "Start the server once — the certificate is generated and trusted automatically — then click \"Certificate\" again.",
                "Certificate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_isAdmin)
        {
            // Sunshine-style: install the CA into the machine-wide Trusted Root store.
            var rc = RunForExitCode("certutil", $"-addstore -f Root \"{caCrt}\"");
            Log(rc == 0
                ? "Certificate installed into this PC's Trusted Root store — browsers here trust ToPlay with no warning."
                : "certutil returned " + rc + " (the server also trusts the certificate automatically on start).");
        }
        else
        {
            Log("Not elevated — the server trusts the certificate automatically when it runs as Administrator.");
        }

        string? savedTo = null;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            savedTo = Path.Combine(desktop, "ToPlay-Certificate.crt");
            File.Copy(caCrt, savedTo, true);
            Log("Saved a copy for your phone to: " + savedTo);
        }
        catch (Exception ex) { Log("Could not copy the certificate to the Desktop: " + ex.Message); }

        var cfg = ReadConfig();
        var ip = PrimaryLanIp() ?? "<your-pc-ip>";
        var httpPort = ReadInt(cfg, "HttpPort", 8080);
        var caUrl = $"http://{ip}:{httpPort}/toplay-ca.crt";

        MessageBox.Show(this,
            "This PC now trusts ToPlay's certificate.\n\n" +
            "To remove the \"Not secure\" warning on your phone, open this address in your " +
            "phone's browser once and install the certificate:\n\n" + caUrl +
            (savedTo != null ? "\n\nA copy was also saved to your Desktop (ToPlay-Certificate.crt)." : ""),
            "Certificate", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ======================= safe close =======================
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // If the server is still streaming and the user clicks the window's X,
        // offer to keep it running in the tray instead of quitting outright.
        if (!_forceExit && _proc is { HasExited: false } && e.CloseReason == CloseReason.UserClosing)
        {
            var choice = MessageBox.Show(this,
                "The ToPlay server is still running.\n\n" +
                "•  Yes  —  Minimize to the tray and keep streaming (recommended)\n" +
                "•  No   —  Stop the server and quit ToPlay\n" +
                "•  Cancel  —  Don't close",
                "ToPlay is still running",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

            if (choice == DialogResult.Cancel) { e.Cancel = true; return; }
            if (choice == DialogResult.Yes) { e.Cancel = true; HideToTray(); return; }
            // No → fall through and shut everything down.
        }

        StopServer();
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
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

    private void ExitApp() { _forceExit = true; Close(); }



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
