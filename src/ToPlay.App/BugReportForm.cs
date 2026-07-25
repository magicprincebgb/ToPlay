using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ToPlay.App;

/// <summary>
/// "Report a bug" dialog. Collects an optional note from the user, bundles it
/// with the current session log and basic system info, saves a copy to disk,
/// puts the full report on the clipboard, and opens the user's mail client
/// (via mailto:) pre-addressed to the ToPlay maintainer.
/// </summary>
public sealed class BugReportForm : Form
{
    private const string ReportEmail = "mihrazhossain@gmail.com";

    private readonly string _logText;
    private readonly string _appVersion;
    private readonly TextBox _txtOpinion = new();

    public BugReportForm(string logText, string appVersion)
    {
        _logText = logText ?? "";
        _appVersion = appVersion ?? "";

        // ----- window chrome (matches the Control Panel dark theme) -----
        Text = "Report a bug";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 380);
        BackColor = Color.FromArgb(11, 15, 23);
        ForeColor = Color.FromArgb(230, 237, 247);
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var accent = Color.FromArgb(31, 111, 235);

        var title = new Label
        {
            Text = "Report a bug",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Location = new Point(16, 14),
            AutoSize = true
        };
        Controls.Add(title);

        var blurb = new Label
        {
            Text = "Tell us what went wrong (optional). Your current log is attached\n" +
                   "automatically so we can diagnose the problem.",
            Location = new Point(18, 50),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(blurb);

        var lblWhat = new Label
        {
            Text = "What happened? (optional)",
            Location = new Point(18, 96),
            AutoSize = true,
            ForeColor = Color.FromArgb(138, 160, 192)
        };
        Controls.Add(lblWhat);

        _txtOpinion.Multiline = true;
        _txtOpinion.ScrollBars = ScrollBars.Vertical;
        _txtOpinion.Location = new Point(18, 118);
        _txtOpinion.Size = new Size(524, 170);
        _txtOpinion.BackColor = Color.FromArgb(6, 10, 16);
        _txtOpinion.ForeColor = Color.FromArgb(200, 214, 235);
        _txtOpinion.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtOpinion);

        var lblTo = new Label
        {
            Text = "Report goes to: " + ReportEmail,
            Location = new Point(18, 300),
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 130, 160)
        };
        Controls.Add(lblTo);

        var btnSend = new Button
        {
            Text = "Send report",
            Location = new Point(300, 330),
            Size = new Size(140, 34),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = accent
        };
        btnSend.FlatAppearance.BorderSize = 0;
        btnSend.Click += (_, _) => Send();
        Controls.Add(btnSend);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(450, 330),
            Size = new Size(92, 34),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(36, 49, 74),
            DialogResult = DialogResult.Cancel
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        Controls.Add(btnCancel);

        CancelButton = btnCancel;
    }

    private void Send()
    {
        var opinion = _txtOpinion.Text.Trim();
        var report = BuildReport(opinion);

        var savedTo = SaveReport(report);

        try { Clipboard.SetText(report); } catch { }

        // mailto: bodies are limited (~2000 chars via ShellExecute), so include
        // system info + the note + the tail of the log; the full report is on
        // the clipboard and saved to disk for anything longer.
        var logTail = _logText.Length > 900 ? _logText[^900..] : _logText;
        var body =
            "Describe the bug here (or paste the full report from your clipboard):\n\n" +
            (opinion.Length > 0 ? opinion + "\n\n" : "") +
            "----- system info -----\n" +
            SystemInfo() + "\n" +
            "----- recent log -----\n" +
            logTail + "\n\n" +
            "(A full report was copied to your clipboard" +
            (savedTo != null ? " and saved to:\n" + savedTo : "") + ")";

        var subject = "ToPlay bug report " + _appVersion;
        var mailto = $"mailto:{ReportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

        try { Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true }); }
        catch { /* no default mail client — the clipboard + file copies remain */ }

        MessageBox.Show(this,
            "Your bug report is ready.\n\n" +
            "Your email app should now be open, addressed to:\n" + ReportEmail + "\n\n" +
            "The full report was also copied to your clipboard" +
            (savedTo != null ? " and saved to:\n" + savedTo : "") + ".\n\n" +
            "Just press Send in your email app to finish.",
            "Thank you", MessageBoxButtons.OK, MessageBoxIcon.Information);

        DialogResult = DialogResult.OK;
        Close();
    }

    private string BuildReport(string opinion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== ToPlay bug report =====");
        sb.AppendLine("Date       : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("App version: " + _appVersion);
        sb.AppendLine(SystemInfo());
        sb.AppendLine();
        sb.AppendLine("----- user notes -----");
        sb.AppendLine(opinion.Length > 0 ? opinion : "(none provided)");
        sb.AppendLine();
        sb.AppendLine("----- full log -----");
        sb.AppendLine(_logText.Length > 0 ? _logText : "(log was empty)");
        return sb.ToString();
    }

    private static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("OS         : " + Environment.OSVersion);
        sb.AppendLine("64-bit OS  : " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("Machine    : " + Environment.MachineName);
        sb.AppendLine("User       : " + Environment.UserName);
        sb.Append("CPU cores  : " + Environment.ProcessorCount);
        return sb.ToString();
    }

    /// <summary>Saves the report to the Desktop, falling back to Temp. Returns the path or null.</summary>
    private static string? SaveReport(string report)
    {
        var name = "ToPlay-bugreport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetTempPath()
        })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, report);
                return path;
            }
            catch { /* try the next location */ }
        }
        return null;
    }
}
