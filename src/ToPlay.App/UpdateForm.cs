using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToPlay.App;

/// <summary>
/// The little "a new version is ready" window. It shows what changed, downloads
/// the new setup with a progress bar, and hands the finished file back to the
/// Control Panel through <see cref="SetupPath"/>. Nothing is installed unless
/// the user presses the button, and the download can be cancelled at any time.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly UpdateInfo _info;

    private readonly TextBox _notes = new();
    private readonly Label _lblStatus = new();
    private readonly ProgressBar _progress = new();
    private readonly GlassButton _btnInstall;
    private readonly GlassButton _btnLater;
    private readonly GlassButton _btnPage;

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>Where the verified installer was saved — set once the download succeeds.</summary>
    public string? SetupPath { get; private set; }

    public UpdateForm(UpdateInfo info, string currentVersion)
    {
        _info = info;

        Text = "ToPlay update";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 460);
        BackColor = Glass.GradBottom;
        ForeColor = Glass.Text;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label
        {
            Text = "A new version of ToPlay is ready",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            Location = new Point(20, 18),
            AutoSize = true,
            ForeColor = Glass.Text,
            BackColor = Color.Transparent
        });

        var size = info.SizeMb >= 1 ? $" · {info.SizeMb:0} MB download" : "";
        Controls.Add(new Label
        {
            Text = $"v{info.Version}{size}      (you have {currentVersion})",
            Location = new Point(22, 50),
            AutoSize = true,
            ForeColor = Glass.Muted,
            BackColor = Color.Transparent
        });

        Controls.Add(new Label
        {
            Text = "What's new",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(20, 82),
            AutoSize = true,
            ForeColor = Glass.Text,
            BackColor = Color.Transparent
        });

        _notes.Multiline = true;
        _notes.ReadOnly = true;
        _notes.ScrollBars = ScrollBars.Vertical;
        _notes.WordWrap = true;
        _notes.Location = new Point(20, 104);
        _notes.Size = new Size(520, 228);
        _notes.BackColor = Color.FromArgb(8, 12, 20);
        _notes.ForeColor = Color.FromArgb(206, 219, 238);
        _notes.BorderStyle = BorderStyle.FixedSingle;
        _notes.Text = info.Notes;
        _notes.Select(0, 0);
        Controls.Add(_notes);

        _lblStatus.Text = "Your accounts, settings and certificate are kept.";
        _lblStatus.Location = new Point(20, 344);
        _lblStatus.AutoSize = true;
        _lblStatus.ForeColor = Glass.Muted;
        _lblStatus.BackColor = Color.Transparent;
        Controls.Add(_lblStatus);

        _progress.Location = new Point(20, 370);
        _progress.Size = new Size(520, 12);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;
        Controls.Add(_progress);

        _btnInstall = MakeButton("Download and install", 20, 400, 190, accent: true);
        _btnInstall.Click += async (_, _) => await StartDownloadAsync();

        _btnLater = MakeButton("Not now", 222, 400, 110);
        _btnLater.Click += (_, _) => Close();

        _btnPage = MakeButton("View on GitHub", 344, 400, 140);
        _btnPage.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); } catch { }
        };

        CancelButton = _btnLater;
        FormClosing += (_, _) => _cts?.Cancel();
    }

    // same glassmorphism backdrop as the Control Panel
    protected override void OnPaintBackground(PaintEventArgs e)
        => Glass.PaintBackground(e.Graphics, ClientRectangle);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Glass.ApplyModernChrome(this);
    }

    private GlassButton MakeButton(string text, int x, int y, int w, bool accent = false)
    {
        var b = new GlassButton
        {
            Text = text,
            Accent = accent,
            Location = new Point(x, y),
            Size = new Size(w, 34),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(b);
        return b;
    }

    private async Task StartDownloadAsync()
    {
        if (_busy) return;
        _cts = new CancellationTokenSource();
        SetBusy(true);

        var progress = new Progress<int>(p =>
        {
            if (IsDisposed) return;
            _progress.Value = Math.Clamp(p, 0, 100);
            _lblStatus.Text = _info.SizeMb >= 1
                ? $"Downloading… {p}%  ({_info.SizeMb * p / 100:0} of {_info.SizeMb:0} MB)"
                : $"Downloading… {p}%";
        });

        try
        {
            SetupPath = await UpdateService.DownloadAsync(_info, progress, _cts.Token);
            if (IsDisposed) return;
            _lblStatus.Text = "Download verified. Installing…";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed) return;
            _lblStatus.Text = "Download cancelled.";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _lblStatus.Text = "Download failed.";
            SetBusy(false);
            MessageBox.Show(this,
                "The update could not be downloaded:\n\n" + ex.Message +
                "\n\nYou can try again, or download it yourself from the ToPlay page on GitHub.",
                "ToPlay update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnInstall.Enabled = !busy;
        _btnInstall.Text = busy ? "Downloading…" : "Download and install";
        _btnPage.Enabled = !busy;
        _btnLater.Text = busy ? "Cancel" : "Not now";
        _progress.Visible = busy;
        if (!busy) _progress.Value = 0;

        // While downloading, "Cancel" stops the download instead of closing the
        // window, so a stray Esc can't leave a half-written file behind.
        _btnLater.Click -= CancelDownload;
        if (busy) _btnLater.Click += CancelDownload;
    }

    private void CancelDownload(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _lblStatus.Text = "Cancelling…";
    }
}
