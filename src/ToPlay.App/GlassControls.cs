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
///
/// IMPORTANT: these controls deliberately do NOT use BackColor=Transparent.
/// WinForms' simulated transparency composites translucent fills on top of
/// stale buffer pixels whenever anchored controls move or repaint, producing
/// smeared "ghost" copies of buttons. Instead every control opaquely repaints
/// what sits visually behind it (form gradient + ancestor card fills) itself,
/// which yields identical visuals with fully deterministic rendering.
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

    /// <summary>Paints the frosted translucent card fill + hairline border.</summary>
    public static void PaintCardFill(Graphics g, Rectangle r, int radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(r, radius);

        using (var fill = new LinearGradientBrush(r,
                   Color.FromArgb(58, 255, 255, 255),
                   Color.FromArgb(20, 255, 255, 255),
                   LinearGradientMode.Vertical))
            g.FillPath(fill, path);

        using var border = new Pen(Color.FromArgb(72, 255, 255, 255));
        g.DrawPath(border, path);
    }

    /// <summary>
    /// Opaquely repaints everything that sits visually behind <paramref name="c"/>
    /// (the form's gradient plus any ancestor <see cref="GlassCard"/> fills),
    /// translated into c's coordinate space. This replaces WinForms' fragile
    /// transparent-BackColor simulation, which leaves ghost images behind when
    /// anchored controls move or translucent fills repaint over stale pixels.
    /// </summary>
    public static void PaintBehind(Control c, Graphics g)
    {
        var form = c.FindForm();
        if (form == null || !form.IsHandleCreated || !c.IsHandleCreated)
        {
            g.Clear(GradBottom);
            return;
        }

        // 1) the form's gradient backdrop, shifted so it lines up pixel-perfect
        var inForm = form.PointToClient(c.PointToScreen(Point.Empty));
        var state = g.Save();
        g.TranslateTransform(-inForm.X, -inForm.Y);
        PaintBackground(g, form.ClientRectangle);
        g.Restore(state);

        // 2) replay ancestor card fills (outermost first) so nesting composes
        var cards = new System.Collections.Generic.List<GlassCard>();
        for (var p = c.Parent; p is not null and not Form; p = p.Parent)
            if (p is GlassCard card) cards.Add(card);
        cards.Reverse();
        foreach (var card in cards)
        {
            var cardInC = c.PointToClient(card.PointToScreen(Point.Empty));
            state = g.Save();
            g.TranslateTransform(cardInC.X, cardInC.Y);
            PaintCardFill(g, new Rectangle(0, 0, card.Width - 1, card.Height - 1), card.Radius);
            g.Restore(state);
        }
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

/// <summary>A frosted, rounded panel. Children can be placed on it. Paints
/// opaquely (backdrop + frosted fill) to avoid transparency ghosting.</summary>
internal sealed class GlassCard : Panel
{
    public int Radius { get; set; } = 16;

    public GlassCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Glass.GradBottom;   // opaque fallback; real pixels painted below
    }

    protected override void OnPaintBackground(PaintEventArgs e)
        => Glass.PaintBehind(this, e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
        => Glass.PaintCardFill(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius);
}

/// <summary>A rounded, custom-drawn button with hover/press states and an
/// optional accent style. Paints opaquely to avoid transparency ghosting.</summary>
internal sealed class GlassButton : Button
{
    public int Radius { get; set; } = 9;
    public bool Accent { get; set; }

    private bool _hover;
    private bool _down;

    public GlassButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Glass.GradBottom;   // opaque fallback; real pixels painted below
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
        => Glass.PaintBehind(this, e.Graphics);

    protected override void OnPaint(PaintEventArgs e)
    {
        // backdrop first (OnPaintBackground is not always invoked for buttons)
        Glass.PaintBehind(this, e.Graphics);

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