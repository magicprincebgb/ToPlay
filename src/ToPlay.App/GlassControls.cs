using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ToPlay.App;

/// <summary>
/// Shared palette + helpers for the "glassmorphism" look used across ToPlay's
/// WinForms UI: a soft gradient background, translucent frosted cards, rounded
/// accent buttons, and Windows 11 dark/rounded window chrome (best effort).
/// </summary>
internal static class Glass
{
    // ---- palette ----
    public static readonly Color GradTop = Color.FromArgb(24, 32, 54);
    public static readonly Color GradBottom = Color.FromArgb(9, 12, 20);
    public static readonly Color Accent = Color.FromArgb(56, 124, 246);
    public static readonly Color AccentHi = Color.FromArgb(90, 152, 255);
    public static readonly Color Text = Color.FromArgb(233, 239, 248);
    public static readonly Color Muted = Color.FromArgb(150, 167, 196);
    public static readonly Color Input = Color.FromArgb(20, 26, 40);

    /// <summary>Builds a rounded-rectangle path for cards/buttons.</summary>
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paints the shared gradient + a subtle top-right accent glow.</summary>
    public static void PaintBackground(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using (var b = new LinearGradientBrush(rect, GradTop, GradBottom, LinearGradientMode.Vertical))
            g.FillRectangle(b, rect);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var glowRect = new Rectangle(rect.Right - 320, -180, 460, 460);
        using var glow = new GraphicsPath();
        glow.AddEllipse(glowRect);
        using var pgb = new PathGradientBrush(glow)
        {
            CenterColor = Color.FromArgb(70, Accent),
            SurroundColors = new[] { Color.FromArgb(0, Accent) }
        };
        g.FillPath(pgb, glow);
    }

    // ---- Windows 11 chrome (dark title bar + rounded corners), best effort ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void ApplyModernChrome(Form form)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { /* older Windows — ignore */ }
    }
}

/// <summary>A frosted, rounded, translucent panel. Children can be placed on it.</summary>
internal sealed class GlassCard : Panel
{
    public int Radius { get; set; } = 16;

    public GlassCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
               | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Glass.Rounded(r, Radius);

        using (var fill = new LinearGradientBrush(r,
                   Color.FromArgb(58, 255, 255, 255),
                   Color.FromArgb(20, 255, 255, 255),
                   LinearGradientMode.Vertical))
            g.FillPath(fill, path);

        using (var border = new Pen(Color.FromArgb(72, 255, 255, 255)))
            g.DrawPath(border, path);
    }
}

/// <summary>A rounded, custom-drawn button with hover/press states and an optional accent style.</summary>
internal sealed class GlassButton : Button
{
    public int Radius { get; set; } = 9;
    public bool Accent { get; set; }

    private bool _hover;
    private bool _down;

    public GlassButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
               | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Glass.Rounded(r, Radius);

        Color top, bottom, border;
        if (Accent)
        {
            top = _hover ? Glass.AccentHi : Glass.Accent;
            bottom = _hover ? Color.FromArgb(52, 108, 232) : Color.FromArgb(37, 92, 214);
            border = Color.FromArgb(150, 170, 205, 255);
        }
        else
        {
            int a = _hover ? 64 : 40;
            top = Color.FromArgb(a + 18, 255, 255, 255);
            bottom = Color.FromArgb(a, 255, 255, 255);
            border = Color.FromArgb(80, 255, 255, 255);
        }

        using (var fill = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using (var pen = new Pen(border))
            g.DrawPath(pen, path);

        if (_down)
            using (var shade = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillPath(shade, path);

        var tc = !Enabled ? Color.FromArgb(120, 130, 150)
               : Accent ? Color.White : Glass.Text;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
